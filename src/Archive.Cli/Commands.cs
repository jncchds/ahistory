using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Media;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Cli;

internal static class Commands
{
    private static ILoggerFactory _loggerFactory = NullLoggerFactory.Instance;

    internal static int Run(string[] args, ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;

        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            return args[0] switch
            {
                "init" => Init(args),
                "hash" => Hash(args),
                "import" => Import(args),
                "sources" => Sources(args),
                "--help" or "-h" or "help" => Usage(),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            // A CLI that prints a stack trace for a bad path is a CLI nobody reads the output of.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Creates a save at the given path, or brings an existing one up to date.
    /// </summary>
    /// <remarks>
    /// Safe to run twice: migrations are recorded by name, so a second run applies nothing. That
    /// is not a convenience — it is the same property the importer relies on, checked in the one
    /// place where it is trivial to observe.
    /// </remarks>
    private static int Init(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ahistory init <path-to-save.db>");
            return 2;
        }

        var options = new ArchiveOptions { DatabasePath = args[1] };
        options.Validate();

        var directory = Path.GetDirectoryName(Path.GetFullPath(options.DatabasePath));

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        Directory.CreateDirectory(options.ResolveMediaDirectory());

        var database = new Database(options, _loggerFactory.CreateLogger<Database>());
        database.Migrate();

        Console.WriteLine($"save     {database.DatabasePath}");
        Console.WriteLine($"media    {options.ResolveMediaDirectory()}");
        Console.WriteLine($"schema   {database.SchemaFingerprint()[..12]}");

        return 0;
    }

    /// <summary>
    /// Reports the content address a file would take, without storing it.
    /// </summary>
    /// <remarks>
    /// Exists so the media store is demonstrable on real files — a photo, a voice note, the same
    /// sticker from two different chats — before there is an importer to feed it.
    /// </remarks>
    private static int Hash(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ahistory hash <file>");
            return 2;
        }

        var path = args[1];

        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"error: no such file: {path}");
            return 1;
        }

        // A throwaway root: this command answers "what address would this take", so nothing is
        // written into a real save.
        var scratch = Path.Combine(Path.GetTempPath(), "ahistory-hash", Guid.NewGuid().ToString("N"));

        try
        {
            var store = new FileSystemMediaStore(scratch);
            var result = store.PutFileAsync(path).GetAwaiter().GetResult();

            Console.WriteLine($"hash     {result.Hash}");
            Console.WriteLine($"size     {result.ByteSize} bytes");
            Console.WriteLine($"path     {MediaAddress.RelativePath(result.Hash, result.Extension)}");

            return 0;
        }
        finally
        {
            if (Directory.Exists(scratch))
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    /// <summary>
    /// Imports a Telegram export folder into a save.
    /// </summary>
    /// <remarks>
    /// This is how the acceptance criterion is checked against a real archive: run it, run it
    /// again, and read the second run's numbers. A clean re-import reports inserted 0 with
    /// skipped equal to seen.
    /// </remarks>
    private static int Import(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: ahistory import <path-to-save.db> <export-folder>");
            return 2;
        }

        var options = new ArchiveOptions { DatabasePath = args[1] };
        options.Validate();

        var database = new Database(options, _loggerFactory.CreateLogger<Database>());
        database.Migrate();

        var mediaStore = new FileSystemMediaStore(options);
        var runner = new ImportRunner(database, mediaStore, _loggerFactory.CreateLogger<ImportRunner>());

        // Auto-detection suggests; the caller decides. --source is how a scripted caller answers
        // the question the import UI will ask.
        var chosenSource = Option(args, "--source");
        var preview = runner.Preview(args[2]);

        Console.WriteLine($"source   {chosenSource ?? preview.SuggestedSourceId}");

        // A save is one person's archive (decisions.md D13). A previously unseen account gets
        // attached to that person — correct for a second account of your own, wrong for someone
        // else's archive, and the importer cannot tell the difference.
        if (preview.AccountIsNewToOwner)
        {
            Console.WriteLine(
                $"warning  this export belongs to {preview.DetectedAccountName ?? preview.DetectedAccountId}, "
                + $"which is not yet one of {preview.OwnerName}'s accounts.");
            Console.WriteLine(
                "         it will be treated as another of their accounts. If this is someone "
                + "else's archive, import it into a separate save instead.");
        }

        if (chosenSource is null)
        {
            Console.WriteLine($"         {preview.SuggestionReason}");

            if (preview.ExistingSources.Count > 1)
            {
                Console.WriteLine("         other sources in this archive (use --source to pick one):");

                foreach (var option in preview.ExistingSources.Where(s => !s.IsSuggested))
                {
                    Console.WriteLine($"           {option.Id}  ({option.MessageCount:N0} messages)");
                }
            }
        }

        var started = DateTimeOffset.UtcNow;
        var lastReport = started;

        var stats = runner.Run(args[2], progress =>
        {
            // Progress is throttled rather than printed per message: at import speed the console
            // write would dominate the import.
            var now = DateTimeOffset.UtcNow;

            if ((now - lastReport).TotalMilliseconds < 250)
            {
                return;
            }

            lastReport = now;
            Console.Write($"\r{progress.MessagesSeen,9:N0} messages  {Truncate(progress.CurrentChat, 32),-32}");
        }, sourceId: chosenSource);

        var elapsed = DateTimeOffset.UtcNow - started;
        var rate = elapsed.TotalSeconds > 0 ? stats.MessagesSeen / elapsed.TotalSeconds : 0;

        Console.Write('\r');
        Console.WriteLine($"imported in {elapsed.TotalSeconds:N1}s ({rate:N0} messages/sec)");
        Console.WriteLine($"  {stats}");

        if (stats.MessagesInserted == 0 && stats.MessagesSeen > 0)
        {
            Console.WriteLine("  nothing new — this export was already fully imported.");
        }

        return 0;
    }

    /// <summary>
    /// Lists the sources in a save — what the archive can be filtered by, and what an import can
    /// be attributed to.
    /// </summary>
    private static int Sources(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ahistory sources <path-to-save.db>");
            return 2;
        }

        var options = new ArchiveOptions { DatabasePath = args[1] };
        options.Validate();

        var database = new Database(options);
        database.Migrate();

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT s.id,
                   ifnull(s.label, '-'),
                   (SELECT count(*) FROM message_source ms WHERE ms.source_id = s.id),
                   (SELECT count(*) FROM import i WHERE i.source_id = s.id)
            FROM import_source s
            ORDER BY 3 DESC, s.id;
            """;

        using var reader = command.ExecuteReader();
        var any = false;

        while (reader.Read())
        {
            any = true;
            Console.WriteLine($"{reader.GetString(0)}");
            Console.WriteLine($"  label    {reader.GetString(1)}");
            Console.WriteLine($"  messages {reader.GetInt64(2):N0}");
            Console.WriteLine($"  runs     {reader.GetInt64(3):N0}");
        }

        if (!any)
        {
            Console.WriteLine("no sources yet — import an export first.");
        }

        return 0;
    }

    /// <summary>Reads a <c>--name value</c> option, or null when it is absent.</summary>
    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);

        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static string Truncate(string value, int length) =>
        value.Length <= length ? value : value[..(length - 1)] + "…";

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"error: unknown command '{command}'");
        Usage();
        return 2;
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            ahistory — local-first message archive

            usage:
              ahistory init <path-to-save.db>   create or migrate a save
              ahistory hash <file>              show the content address a file would take
              ahistory import <save.db> <folder> [--source <id>]
                                                import a Telegram export folder
              ahistory sources <save.db>        list the sources in a save

            A save is the .db file plus a media folder beside it. Both are created by `init`.
            """);

        return 2;
    }
}
