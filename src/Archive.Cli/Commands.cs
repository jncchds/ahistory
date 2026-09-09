using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Import.Synthetic;
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
                "synth" => Synth(args),
                "stats" => Stats(args),
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

        // Carriage-return progress only works on a terminal. Piped to a file or another command,
        // every update becomes its own line and buries the result underneath hundreds of them.
        var showProgress = !Console.IsOutputRedirected;

        var stats = runner.Run(args[2], progress =>
        {
            if (!showProgress)
            {
                return;
            }

            // Throttled rather than printed per message: at import speed the console write would
            // dominate the import.
            var now = DateTimeOffset.UtcNow;

            if ((now - lastReport).TotalMilliseconds < 250)
            {
                return;
            }

            lastReport = now;
            Console.Write($"\r{progress.MessagesSeen,9:N0} messages  {Truncate(progress.CurrentChat, 32),-32}");
        }, sourceId: chosenSource, storeRawJson: !args.Contains("--no-raw-json"));

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

    /// <summary>
    /// Reports what a save is made of, and what it costs.
    /// </summary>
    /// <remarks>
    /// §1 keeps the raw export JSON per message so a parser gap can be re-run rather than
    /// re-requested. That is worth paying for, but it is the single largest thing in the database
    /// by a wide margin, and someone deciding whether to keep it should be able to see the number
    /// rather than guess at it.
    /// </remarks>
    private static int Stats(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ahistory stats <path-to-save.db>");
            return 2;
        }

        var options = new ArchiveOptions { DatabasePath = args[1] };
        options.Validate();

        var database = new Database(options);

        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT (SELECT count(*) FROM message),
                   (SELECT count(*) FROM thread),
                   (SELECT count(*) FROM person),
                   (SELECT count(*) FROM media),
                   (SELECT count(*) FROM reaction),
                   (SELECT total(length(raw_json)) FROM message),
                   (SELECT total(length(plaintext)) FROM message),
                   (SELECT total(length(entities_json)) FROM message),
                   (SELECT page_count * page_size FROM pragma_page_count(), pragma_page_size());
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        var messages = reader.GetInt64(0);
        var rawJson = reader.GetDouble(5);
        var plaintext = reader.GetDouble(6);
        var entities = reader.GetDouble(7);
        var fileBytes = reader.GetInt64(8);

        var mediaRoot = options.ResolveMediaDirectory();
        var mediaBytes = Directory.Exists(mediaRoot)
            ? Directory.EnumerateFiles(mediaRoot, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
            : 0;

        Console.WriteLine($"messages    {messages:N0}");
        Console.WriteLine($"threads     {reader.GetInt64(1):N0}");
        Console.WriteLine($"people      {reader.GetInt64(2):N0}");
        Console.WriteLine($"media       {reader.GetInt64(3):N0} files, {Mb(mediaBytes)}");
        Console.WriteLine($"reactions   {reader.GetInt64(4):N0}");
        Console.WriteLine();
        Console.WriteLine($"database    {Mb(fileBytes)}");
        Console.WriteLine($"  raw_json  {Mb((long)rawJson)}  ({Share(rawJson, fileBytes)} of the file)");
        Console.WriteLine($"  text      {Mb((long)plaintext)}");
        Console.WriteLine($"  entities  {Mb((long)entities)}");

        if (messages > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"per message {fileBytes / (double)messages:N0} bytes, "
                            + $"of which {rawJson / messages:N0} is raw_json");
        }

        return 0;
    }

    private static string Mb(long bytes) => $"{bytes / 1024.0 / 1024.0:N1} MB";

    private static string Share(double part, long whole) =>
        whole == 0 ? "—" : $"{part / whole:P0}";

    /// <summary>
    /// Writes a synthetic Telegram export containing no real data.
    /// </summary>
    /// <remarks>
    /// For seeing the app populated before you have exported your own archive, and for measuring
    /// it at a size where the query plans matter. It reproduces the awkward properties of a real
    /// archive — bursts and long silences, a long tail of two-word messages, Cyrillic alongside
    /// Latin, colliding timestamps, one sticker repeated everywhere — but only shapes the importer
    /// already understands, so it can never surface a parsing trap nobody has thought of.
    /// </remarks>
    private static int Synth(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ahistory synth <output-folder> [--messages N] [--chats N] [--seed N]");
            return 2;
        }

        var options = new SyntheticOptions(
            Messages: Number(args, "--messages") ?? 10_000,
            Chats: Number(args, "--chats") ?? 20,
            Seed: Number(args, "--seed") ?? 20260909);

        var started = DateTimeOffset.UtcNow;
        var summary = SyntheticExport.Write(args[1], options);
        var elapsed = DateTimeOffset.UtcNow - started;

        Console.WriteLine($"folder    {summary.Folder}");
        Console.WriteLine($"messages  {summary.Messages:N0}");
        Console.WriteLine($"chats     {summary.Chats:N0}");
        Console.WriteLine($"json      {summary.JsonBytes / 1024.0 / 1024.0:N1} MB");
        Console.WriteLine($"written   in {elapsed.TotalSeconds:N1}s");
        Console.WriteLine();
        Console.WriteLine("This contains no real data. Import it like any export:");
        Console.WriteLine($"  ahistory import <save.db> {summary.Folder}");

        return 0;
    }

    /// <summary>Reads a numeric <c>--name value</c> option.</summary>
    private static int? Number(string[] args, string name) =>
        Option(args, name) is { } text && int.TryParse(text, out var value) ? value : null;

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
              ahistory import <save.db> <folder> [--source <id>] [--no-raw-json]
                                                import a Telegram export folder
              ahistory sources <save.db>        list the sources in a save
              ahistory stats <save.db>          what the archive is made of
              ahistory synth <folder> [--messages N] [--chats N]
                                                write a synthetic export (no real data)

            A save is the .db file plus a media folder beside it. Both are created by `init`.
            """);

        return 2;
    }
}
