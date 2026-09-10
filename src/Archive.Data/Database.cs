using System.Data;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using Archive.Core;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Data;

/// <summary>
/// The single place a connection to the save is opened, and the owner of schema migration.
/// </summary>
/// <remarks>
/// <para>
/// Every connection — hand-opened here, or opened by EF Core through the interceptor — gets the
/// same pragma block. A connection that skips it behaves subtly differently (no foreign key
/// enforcement, no WAL, no trigger firing on cascade), and those differences show up as
/// corrupted-looking data rather than as errors, so there is exactly one code path for it.
/// </para>
/// <para>
/// The schema is owned by the numbered .sql files in Migrations/, not by EF Core
/// (decisions.md D3). EF's SQLite provider rebuilds tables to alter them, which silently drops
/// the FTS5 triggers and leaves search returning stale results instead of failing.
/// </para>
/// </remarks>
public sealed class Database
{
    /// <summary>Prefix of the embedded migration resources, e.g. "Archive.Data.Migrations.".</summary>
    private static readonly string ResourcePrefix = typeof(Database).Namespace + ".Migrations.";

    /// <summary>
    /// The build's version, as recorded in a save it creates or upgrades.
    /// </summary>
    /// <remarks>
    /// Read from this assembly rather than the entry assembly: the CLI and the desktop head are
    /// built from one version property, but a test host is not, and a save should record the
    /// version of the code that owns the schema.
    /// </remarks>
    public static string AppVersion { get; } =
        typeof(Database).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(Database).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    private readonly ArchiveOptions _options;
    private readonly ILogger _log;

    public Database(ArchiveOptions options, ILogger<Database>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        _options = options;
        _log = logger ?? NullLogger<Database>.Instance;
        ConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(options.DatabasePath),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            ForeignKeys = true,
        }.ToString();
    }

    public string ConnectionString { get; }

    public string DatabasePath => Path.GetFullPath(_options.DatabasePath);

    public SqliteConnection Open()
    {
        var connection = new SqliteConnection(ConnectionString);
        connection.Open();
        Configure(connection);
        return connection;
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        Configure(connection);
        return connection;
    }

    /// <summary>
    /// Applies the pragma block that every connection to a save must have.
    /// </summary>
    /// <remarks>
    /// recursive_triggers is the one that is easy to omit and expensive to debug. In SQLite,
    /// rows removed by an ON DELETE CASCADE action do not fire DELETE triggers unless it is on.
    /// Deleting a thread would cascade to its messages and to search_document while leaving
    /// orphaned rows in search_fts that still match queries — a search index that lies rather
    /// than one that errors.
    /// </remarks>
    public static void Configure(IDbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);

        Execute(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA foreign_keys = ON;
            PRAGMA recursive_triggers = ON;
            PRAGMA busy_timeout = 5000;
            """);
    }

    /// <summary>
    /// Applies every migration that has not been applied yet, in filename order.
    /// </summary>
    /// <remarks>
    /// Each file runs inside its own transaction, and its name is recorded in schema_migration,
    /// so calling this on an up-to-date database does nothing and calling it on a half-migrated
    /// one resumes. Foreign keys are disabled for the duration and verified before commit: a
    /// migration that leaves a dangling reference fails at migration time rather than at some
    /// unrelated INSERT weeks later.
    /// </remarks>
    public void Migrate()
    {
        var status = Inspect();

        switch (status.State)
        {
            case SchemaState.UpToDate:
                return;

            // Creating a save is its own consent; there is nothing in it yet to lose.
            case SchemaState.Empty:
                ApplyPending();
                VerifySchema();
                RecordProvenance(created: true);
                return;

            // One-way and not undoable in place, so somebody has to say so first.
            case SchemaState.Behind:
                throw new SchemaUpgradeRequiredException(DatabasePath, status.Pending);

            case SchemaState.Ahead:
                throw new InvalidOperationException(
                    $"The save at '{DatabasePath}' was made by {DescribeSaveVersion()}. It has "
                    + $"migrations this build ({AppVersion}) does not: "
                    + $"{string.Join(", ", status.Unknown)}. Update ahistory to open it. Do not "
                    + "start a new save — this one is the newer of the two.");

            default:
                throw new InvalidOperationException(
                    $"The schema in '{DatabasePath}' is not the one any version of these migrations "
                    + "produces, so it was neither made by this build nor left behind by an older "
                    + "one. Nothing can reconcile it in place: import into a new save, which is the "
                    + "only operation that builds the schema from the migrations.");
        }
    }

    /// <summary>
    /// Carries a save that is behind forward to this build's schema.
    /// </summary>
    /// <param name="backupPath">
    /// Where to put a copy of the save first, or null for no copy. There is no rollback, so a
    /// caller that passes null is choosing to have no way back.
    /// </param>
    /// <returns>The migrations that were applied, in the order they ran.</returns>
    public IReadOnlyList<string> Upgrade(string? backupPath)
    {
        var status = Inspect();

        if (!status.CanUpgrade)
        {
            // Anything else is either fine already or not upgradeable; Migrate says which.
            Migrate();

            return [];
        }

        if (backupPath is not null)
        {
            BackupTo(backupPath);
        }

        ApplyPending();
        VerifySchema();
        RecordProvenance(created: false);

        return status.Pending;
    }

    /// <summary>
    /// Records which build shaped this save, after migrations have run.
    /// </summary>
    /// <remarks>
    /// Only ever written when the schema changes — on creation and on upgrade — not on every
    /// open. A save that is merely read should not be written to, and "which build last looked at
    /// this" answers nothing that "which build last changed it" does not.
    /// </remarks>
    private void RecordProvenance(bool created)
    {
        using var connection = Open();

        if (!TableExists(connection, "save_provenance"))
        {
            // A save upgraded to a schema that predates this table. Nothing to record, and
            // nothing wrong.
            return;
        }

        using var command = connection.CreateCommand();
        command.CommandText = created
            ? """
              UPDATE save_provenance
                 SET created_by = $version, upgraded_by = $version, upgraded_utc = $now
               WHERE id = 1;
              """
            : """
              UPDATE save_provenance
                 SET upgraded_by = $version, upgraded_utc = $now
               WHERE id = 1;
              """;

        command.Parameters.AddWithValue("$version", AppVersion);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// How to refer to the version that shaped this save, for a message about it.
    /// </summary>
    /// <remarks>
    /// A save from a newer build has this table by definition, since it has every migration this
    /// build has. A save that predates it, or one that recorded nothing, gets a phrase that reads
    /// correctly in the sentence rather than an empty gap.
    /// </remarks>
    private string DescribeSaveVersion()
    {
        try
        {
            using var connection = Open();

            if (!TableExists(connection, "save_provenance"))
            {
                return "a newer version of ahistory";
            }

            using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT ifnull(upgraded_by, created_by) FROM save_provenance WHERE id = 1;";

            // Naming a version identical to our own would read as nonsense — "made by 0.2.0,
            // which this 0.2.0 cannot open" — even though the migrations genuinely differ.
            return command.ExecuteScalar() is string version
                && version.Length > 0
                && version != AppVersion
                    ? $"ahistory {version}"
                    : "a newer version of ahistory";
        }
        catch (SqliteException)
        {
            // Explaining a problem must not become a second one.
            return "a newer version of ahistory";
        }
    }

    /// <summary>
    /// Writes a standalone copy of the save.
    /// </summary>
    /// <remarks>
    /// <c>VACUUM INTO</c> rather than a file copy: it produces one consistent file with the WAL
    /// already folded in, so the copy is complete on its own. Copying archive.db while a -wal sits
    /// beside it yields a file that is missing whatever had not been checkpointed.
    ///
    /// Only the database is copied. Migrations never touch media, and media is content-addressed
    /// in a directory of its own, so it is not at risk from anything this does.
    /// </remarks>
    public void BackupTo(string backupPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);

        var full = Path.GetFullPath(backupPath);

        if (File.Exists(full))
        {
            throw new InvalidOperationException(
                $"'{full}' already exists. Refusing to overwrite what may be the only copy of an "
                + "earlier save.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        using var connection = Open();
        using var command = connection.CreateCommand();

        // Parameterised: a path is data, and one containing a quote would otherwise be a
        // hand-rolled injection into our own maintenance statement.
        command.CommandText = "VACUUM INTO $path;";
        command.Parameters.AddWithValue("$path", full);
        command.ExecuteNonQuery();

        _log.LogInformation("Copied the save before upgrading it.");
    }

    /// <summary>
    /// Classifies the save's schema against the migrations this build carries.
    /// </summary>
    /// <remarks>
    /// The names in schema_migration are the evidence, not the schema itself: they say what ran,
    /// which is what distinguishes an older save from one made by a newer build. The fingerprint
    /// then settles whether a save claiming to be current actually is.
    /// </remarks>
    public SchemaStatus Inspect()
    {
        var embedded = EmbeddedMigrations().Select(m => m.Name).ToArray();

        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        if (!TableExists(connection, "schema_migration"))
        {
            return new SchemaStatus(SchemaState.Empty, embedded, []);
        }

        var applied = AppliedMigrations(connection);

        if (applied.Count == 0)
        {
            return new SchemaStatus(SchemaState.Empty, embedded, []);
        }

        var unknown = applied.Except(embedded, StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal).ToArray();

        if (unknown.Length > 0)
        {
            return new SchemaStatus(SchemaState.Ahead, [], unknown);
        }

        var pending = embedded.Where(n => !applied.Contains(n)).ToArray();

        // Migrations are append-only and run in filename order, so what an older save has run is
        // always a leading run of this build's list. A gap in the middle is something else —
        // a migration withdrawn, or a save assembled by hand — and is not an upgrade.
        var expectedPrefix = embedded.Take(applied.Count).ToArray();

        if (pending.Length > 0 && !applied.SetEquals(expectedPrefix))
        {
            return new SchemaStatus(SchemaState.Diverged, pending, []);
        }

        if (pending.Length > 0)
        {
            return new SchemaStatus(SchemaState.Behind, pending, []);
        }

        // Every migration has run. Whether that produced the right schema is a separate question,
        // and the one an edited migration gets wrong.
        return SchemaFingerprint() == ReferenceFingerprint()
            ? new SchemaStatus(SchemaState.UpToDate, [], [])
            : new SchemaStatus(SchemaState.Diverged, [], []);
    }

    private static bool TableExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", name);

        return command.ExecuteScalar() is not null;
    }

    /// <summary>Runs every migration the save has not run, in filename order.</summary>
    private void ApplyPending()
    {
        using var connection = new SqliteConnection(ConnectionString);
        connection.Open();

        // PRAGMA foreign_keys is a no-op inside a transaction, so it has to be set here, on a
        // connection that is not yet in one.
        Execute(connection, """
            PRAGMA journal_mode = WAL;
            PRAGMA busy_timeout = 5000;
            PRAGMA foreign_keys = OFF;
            """);

        Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_migration (
                name        TEXT PRIMARY KEY,
                applied_utc TEXT NOT NULL
            ) STRICT;
            """);

        var applied = AppliedMigrations(connection);

        foreach (var (name, sql) in EmbeddedMigrations())
        {
            if (applied.Contains(name))
            {
                continue;
            }

            // Migration names are our own file names, so they carry nothing personal.
            _log.LogInformation("Applying migration {Migration}.", name);

            var stopwatch = Stopwatch.StartNew();
            using var transaction = connection.BeginTransaction();

            try
            {
                Execute(connection, sql, transaction);
                VerifyForeignKeys(connection, transaction, name);

                using var record = connection.CreateCommand();
                record.Transaction = transaction;
                record.CommandText =
                    "INSERT INTO schema_migration (name, applied_utc) VALUES ($name, $applied);";
                record.Parameters.AddWithValue("$name", name);
                record.Parameters.AddWithValue("$applied", DateTimeOffset.UtcNow.ToString("O"));
                record.ExecuteNonQuery();

                transaction.Commit();

                _log.LogInformation(
                    "Applied migration {Migration} in {ElapsedMs} ms.", name, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                transaction.Rollback();

                _log.LogError(ex, "Migration {Migration} failed and was rolled back.", name);

                throw new InvalidOperationException($"Migration '{name}' failed: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Checks that applying the migrations actually produced the schema they describe.
    /// </summary>
    /// <remarks>
    /// Runs after migrations, where <see cref="Inspect"/> runs before them. The two exist for the
    /// same reason: migrations are recorded by name, so editing one that has already been applied
    /// changes what new saves get and leaves existing ones behind, with nothing to notice. That is
    /// not hypothetical — a column was once dropped from 001_core.sql after saves existed, and the
    /// first sign of it was a NOT NULL failure on a column the code no longer knew about, raised
    /// from the middle of an import (decisions.md D23).
    /// </remarks>
    private void VerifySchema()
    {
        var actual = SchemaFingerprint();
        var expected = ReferenceFingerprint();

        if (actual == expected)
        {
            return;
        }

        throw new InvalidOperationException(
            $"The schema in '{DatabasePath}' is not the one these migrations produce "
            + $"(schema {actual}, expected {expected}). Nothing here can repair it in place: "
            + "import into a new save, which is the only operation that builds the schema from "
            + "the migrations.");
    }

    /// <summary>
    /// The fingerprint of a database built from the migrations and nothing else.
    /// </summary>
    /// <remarks>
    /// Computed once per process. It depends only on the embedded migrations, which cannot change
    /// while the process runs, and it costs a scratch database and a full migration run — worth
    /// paying once and not on every save that is opened.
    /// </remarks>
    private static string ReferenceFingerprint() => _reference.Value;

    private static readonly Lazy<string> _reference = new(BuildReferenceFingerprint);

    private static string BuildReferenceFingerprint()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ahistory-schema", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            var reference = new Database(
                new ArchiveOptions { DatabasePath = Path.Combine(directory, "reference.db") });

            // ApplyPending, not Migrate: Migrate would inspect, and inspecting asks for the
            // reference fingerprint, which is what is being built.
            reference.ApplyPending();

            return reference.SchemaFingerprint();
        }
        finally
        {
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A scratch file in the temp directory; the OS will reclaim it.
            }
        }
    }

    /// <summary>
    /// A stable hash of the whole schema, used by tests to prove a second Migrate() changed nothing.
    /// </summary>
    public string SchemaFingerprint()
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, ifnull(sql, '')
            FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%'
            ORDER BY type, name;
            """;

        // Unit separator: cannot occur in SQL text, so no field boundary is ambiguous.
        const char Separator = (char)31;

        var builder = new StringBuilder();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            builder.Append(reader.GetString(0)).Append(Separator)
                   .Append(reader.GetString(1)).Append(Separator)
                   .Append(reader.GetString(2)).Append(Separator);
        }

        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>Migration resources, ordered by their numeric filename prefix.</summary>
    internal static IEnumerable<(string Name, string Sql)> EmbeddedMigrations()
    {
        var assembly = typeof(Database).Assembly;

        return assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal)
                     && n.EndsWith(".sql", StringComparison.Ordinal))
            .Select(n => (Name: ShortName(n), Resource: n))
            .OrderBy(x => x.Name, StringComparer.Ordinal)
            .Select(x => (x.Name, Sql: ReadResource(assembly, x.Resource)))
            .ToArray();
    }

    private static string ShortName(string resourceName) => resourceName[ResourcePrefix.Length..];

    private static string ReadResource(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static HashSet<string> AppliedMigrations(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM schema_migration;";

        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static void VerifyForeignKeys(SqliteConnection connection, SqliteTransaction transaction, string migration)
    {
        using var check = connection.CreateCommand();
        check.Transaction = transaction;
        check.CommandText = "PRAGMA foreign_key_check;";

        using var reader = check.ExecuteReader();

        if (reader.Read())
        {
            var table = reader.IsDBNull(0) ? "?" : reader.GetString(0);
            var parent = reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetString(2) : "?";

            throw new InvalidOperationException(
                $"Migration '{migration}' left a dangling foreign key: table '{table}' references '{parent}'.");
        }
    }

    private static void Execute(IDbConnection connection, string sql, IDbTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;

        if (transaction is not null)
        {
            command.Transaction = transaction;
        }

        command.ExecuteNonQuery();
    }
}
