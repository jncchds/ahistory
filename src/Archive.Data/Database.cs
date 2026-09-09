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
