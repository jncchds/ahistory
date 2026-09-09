using Archive.Core;
using Archive.Data;
using Archive.Media;

namespace Archive.Cli;

internal static class Commands
{
    internal static int Run(string[] args)
    {
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

        var database = new Database(options);
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

            A save is the .db file plus a media folder beside it. Both are created by `init`.
            """);

        return 2;
    }
}
