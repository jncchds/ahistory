using Archive.Ai;
using Archive.Ai.Attachments;
using Archive.Ai.Diary;
using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Merging;
using Archive.Ai.Search;
using Archive.Ai.Sessions;
using Archive.Core;
using Archive.Data;
using Archive.Import;
using Archive.Import.Synthetic;
using Archive.Media;
using Archive.Sync;
using Archive.Sync.Telegram;
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
                "watch" => Watch(args),
                "connect" => Connect(args),
                "chats" => Chats(args),
                "sync" => Sync(args),
                "sync-check" => SyncCheck(args),
                "synth" => Synth(args),
                "stats" => Stats(args),
                "ai" => Ai(args),
                "--help" or "-h" or "help" => Usage(),
                _ => Unknown(args[0]),
            };
        }
        catch (SchemaUpgradeRequiredException ex)
        {
            // Reachable from every command that opens a save. The answer is the same for all of
            // them, and it is a thing to type rather than a thing to know.
            ReportUpgradeNeeded(ex.SavePath, ex.Pending);
            return 1;
        }
        catch (Exception ex)
        {
            // A CLI that prints a stack trace for a bad path is a CLI nobody reads the output of.
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Creates a save at the given path, or carries an existing one forward with
    /// <c>--upgrade</c>.
    /// </summary>
    /// <remarks>
    /// Safe to run twice: migrations are recorded by name, so a second run applies nothing. That
    /// is not a convenience — it is the same property the importer relies on, checked in the one
    /// place where it is trivial to observe.
    ///
    /// Upgrading an existing save is the one thing here that is not repeatable, so it needs
    /// <c>--upgrade</c> to be asked for by name. This command is where that lives, and every other
    /// command points at it rather than quietly migrating a save out from under someone.
    /// </remarks>
    private static int Init(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ahistory init <path-to-save.db> [--upgrade] [--no-backup]");
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
        var status = database.Inspect();

        if (status.CanUpgrade && !args.Contains("--upgrade"))
        {
            ReportUpgradeNeeded(database.DatabasePath, status.Pending);
            return 1;
        }

        if (status.CanUpgrade)
        {
            var backup = args.Contains("--no-backup")
                ? null
                : BackupPathFor(database.DatabasePath, status.Pending);

            var applied = database.Upgrade(backup);

            Console.WriteLine($"upgraded {applied.Count} migration(s): {string.Join(", ", applied)}");
            Console.WriteLine(backup is null
                ? "backup   none (--no-backup)"
                : $"backup   {backup}");
        }
        else
        {
            database.Migrate();
        }

        Console.WriteLine($"save     {database.DatabasePath}");
        Console.WriteLine($"media    {options.ResolveMediaDirectory()}");
        Console.WriteLine($"schema   {database.SchemaFingerprint()[..12]}");

        return 0;
    }

    /// <summary>
    /// Explains that a save is behind and what to type, on stderr.
    /// </summary>
    /// <remarks>
    /// Every command that opens a save can hit this, and all of them say the same thing, because
    /// the answer does not depend on what the caller was trying to do.
    /// </remarks>
    private static void ReportUpgradeNeeded(string savePath, IReadOnlyList<string> pending)
    {
        Console.Error.WriteLine(
            $"error: '{savePath}' was made by an older version of ahistory.");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"  {pending.Count} migration(s) would be applied:");

        foreach (var name in pending)
        {
            Console.Error.WriteLine($"    {name}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("  Upgrading cannot be undone, so it is not done automatically.");
        Console.Error.WriteLine("  A copy of the save is written first unless --no-backup is passed.");
        Console.Error.WriteLine();
        Console.Error.WriteLine($"    ahistory init \"{savePath}\" --upgrade");
    }

    /// <summary>
    /// Where to put the copy taken before an upgrade: beside the save, named for the migration
    /// it is about to run, and never overwriting an existing file.
    /// </summary>
    private static string BackupPathFor(string savePath, IReadOnlyList<string> pending)
    {
        // "004_annotations.sql" -> "004", so the name says what the copy predates.
        var stage = pending[0].Split('_')[0];
        var candidate = $"{savePath}.pre-{stage}";

        for (var n = 2; File.Exists(candidate); n++)
        {
            candidate = $"{savePath}.pre-{stage}-{n}";
        }

        return candidate;
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
    /// Imports an export folder into a save, whichever platform produced it.
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

        // Which account is yours, for the formats that never say (VK, QIP). Without it the
        // importer uses a placeholder and says so, rather than inventing an owner.
        var ownerAccount = Option(args, "--me");

        // Which format to read, when the folder holds more than one — a Takeout with Hangouts and
        // Google Chat side by side, a Meta download with Messenger and Instagram.
        var format = Option(args, "--format");
        var preview = runner.Preview(args[2], format);

        Console.WriteLine($"format   {preview.PlatformName} ({preview.FileCount:N0} file(s))");

        if (preview.FormatNote is { } note)
        {
            Console.WriteLine($"         {note}");
        }

        foreach (var other in preview.OtherFormats)
        {
            Console.WriteLine(
                $"also     {other.DisplayName} — not read by this run; import again with --format {other.Platform}");
        }

        Console.WriteLine($"source   {chosenSource ?? preview.SuggestedSourceId}");

        // A format that will not name its account gets asked rather than guessed at: your own
        // messages otherwise attach to a placeholder that lines up with nothing on any other
        // platform, and a history file for your own account reads as a chat with yourself.
        if (preview.DetectedAccountIsGuess && ownerAccount is null)
        {
            Console.WriteLine(
                $"me       not stated by this format — pass --me <account id> to attribute your own "
                + "messages to you.");

            if (preview.AccountCandidates.Count > 0)
            {
                Console.WriteLine($"         candidates: {string.Join(", ", preview.AccountCandidates)}");
            }
        }
        else if (ownerAccount is not null)
        {
            Console.WriteLine($"me       {ownerAccount}");
        }

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
        },
        sourceId: chosenSource,
        platform: format,
        storeRawJson: !args.Contains("--no-raw-json"),
        ownerAccountId: ownerAccount);

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
    /// Watched folders: where a scheduled export lands, re-read when it changes.
    /// </summary>
    /// <remarks>
    /// The cheapest way to keep an archive current, and it needs no network and no new reader: an
    /// SMS backup written nightly, a Takeout scheduled every two months, a DiscordChatExporter run
    /// on a timer. Re-import is idempotent (P3), so reading a folder again is safe; the fingerprint
    /// means an unchanged one is not read at all.
    /// </remarks>
    private static int Watch(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ahistory watch <save.db> [list | add <folder> [--format <p>] [--source <id>] [--me <id>] "
                + "| remove <folder> | check | follow]");
            return 2;
        }

        var options = new ArchiveOptions { DatabasePath = args[1] };
        options.Validate();

        var database = new Database(options, _loggerFactory.CreateLogger<Database>());
        database.Migrate();

        var runner = new ImportRunner(
            database, new FileSystemMediaStore(options), _loggerFactory.CreateLogger<ImportRunner>());

        var store = new SyncSettingsStore(logger: _loggerFactory.CreateLogger<SyncSettingsStore>());

        using var watcher = new FolderWatcher(
            runner, store, database.DatabasePath, _loggerFactory.CreateLogger<FolderWatcher>());

        var command = args.Length > 2 && !args[2].StartsWith("--", StringComparison.Ordinal) ? args[2] : "list";

        switch (command)
        {
            case "add":
                if (args.Length < 4)
                {
                    Console.Error.WriteLine("usage: ahistory watch <save.db> add <folder> [--format <p>] [--source <id>] [--me <id>]");
                    return 2;
                }

                watcher.Add(new WatchedFolder(
                    Path.GetFullPath(args[3]),
                    Option(args, "--format"),
                    Option(args, "--source"),
                    Option(args, "--me")));

                Console.WriteLine($"watching {Path.GetFullPath(args[3])}");

                // Read straight away rather than at the next change: someone who points at a folder
                // means "import this", and waiting for it to change next would look like nothing
                // happened.
                return Check(watcher);

            case "remove":
                if (args.Length < 4)
                {
                    Console.Error.WriteLine("usage: ahistory watch <save.db> remove <folder>");
                    return 2;
                }

                watcher.Remove(args[3]);
                Console.WriteLine($"no longer watching {Path.GetFullPath(args[3])}");
                return 0;

            case "check":
                return Check(watcher);

            case "follow":
                return Follow(watcher);

            case "list":
                var folders = watcher.Folders;

                if (folders.Count == 0)
                {
                    Console.WriteLine("no watched folders — add one with `ahistory watch <save.db> add <folder>`.");
                    return 0;
                }

                foreach (var folder in folders)
                {
                    Console.WriteLine(folder.Path);
                    Console.WriteLine($"  format   {folder.Platform ?? "detected"}");
                    Console.WriteLine($"  source   {folder.SourceId ?? "suggested at import"}");
                    Console.WriteLine($"  present  {(Directory.Exists(folder.Path) ? "yes" : "no — not reachable right now")}");
                }

                return 0;

            default:
                Console.Error.WriteLine($"error: unknown watch command '{command}'");
                return 2;
        }
    }

    private static int Check(FolderWatcher watcher)
    {
        var checks = watcher.CheckAllAsync().GetAwaiter().GetResult();

        if (checks.Count == 0)
        {
            Console.WriteLine("no watched folders.");
            return 0;
        }

        var failed = false;

        foreach (var check in checks)
        {
            Console.Write($"{check.Folder.Path}  ");

            switch (check.Outcome)
            {
                case FolderCheckOutcome.Imported:
                    Console.WriteLine($"imported — {check.Stats}");
                    break;
                case FolderCheckOutcome.Unchanged:
                    Console.WriteLine("unchanged since the last import");
                    break;
                case FolderCheckOutcome.Missing:
                    Console.WriteLine("not there right now");
                    break;
                case FolderCheckOutcome.Failed:
                    failed = true;
                    Console.WriteLine($"failed — {check.Error}");
                    break;
            }
        }

        return failed ? 1 : 0;
    }

    /// <summary>Stays running, importing each watched folder as it changes, until Ctrl+C.</summary>
    private static int Follow(FolderWatcher watcher)
    {
        using var stopping = new ManualResetEventSlim(false);

        Console.CancelKeyPress += (_, e) =>
        {
            // Handled here so the watcher stops cleanly rather than the process being torn down
            // mid-import.
            e.Cancel = true;
            stopping.Set();
        };

        watcher.Checked += check =>
        {
            if (check.Outcome is FolderCheckOutcome.Imported or FolderCheckOutcome.Failed)
            {
                Console.WriteLine(
                    $"{DateTimeOffset.Now:HH:mm:ss}  {check.Folder.Path}  "
                    + (check.Outcome == FolderCheckOutcome.Imported ? $"imported — {check.Stats}" : $"failed — {check.Error}"));
            }
        };

        watcher.Start();

        Console.WriteLine($"watching {watcher.Folders.Count} folder(s). Ctrl+C to stop.");
        stopping.Wait();

        watcher.Stop();
        Console.WriteLine("stopped.");

        return 0;
    }

    /// <summary>
    /// Signs in to Telegram, so the archive can read the account rather than an export of it.
    /// </summary>
    /// <remarks>
    /// Nothing is contacted until this is run: the connection is off until switched on, and this is
    /// the switch. Signing in needs an api_id and api_hash of the user's own, from my.telegram.org —
    /// they identify the application rather than the person, and using the user's own means their
    /// account never runs under somebody else's.
    /// </remarks>
    private static int Connect(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ahistory connect <save.db> [--api-id <n>] [--api-hash <h>] [--phone <+number>] [--off]");
            return 2;
        }

        var (database, options) = Open(args[1]);
        var store = new SyncSettingsStore(logger: _loggerFactory.CreateLogger<SyncSettingsStore>());

        if (args.Contains("--off"))
        {
            store.Update(s => s.Telegram.Enabled = false);
            Console.WriteLine("telegram off — nothing will be contacted. The session is kept; `--off` is not a sign-out.");
            return 0;
        }

        var apiId = Number(args, "--api-id");
        var apiHash = Option(args, "--api-hash");

        var settings = store.Update(s =>
        {
            s.Telegram.ApiId = apiId ?? s.Telegram.ApiId;
            s.Telegram.ApiHash = apiHash ?? s.Telegram.ApiHash;
        });

        if (!settings.Telegram.HasApplication)
        {
            Console.Error.WriteLine("error: Telegram needs an application of your own.");
            Console.Error.WriteLine();
            Console.Error.WriteLine("  Sign in at https://my.telegram.org, open 'API development tools',");
            Console.Error.WriteLine("  create an application, then pass what it gives you:");
            Console.Error.WriteLine();
            Console.Error.WriteLine($"    ahistory connect \"{args[1]}\" --api-id 12345 --api-hash abc123…");
            return 2;
        }

        // Switching it on is what the user just asked for by running this.
        settings = store.Update(s => s.Telegram.Enabled = true);

        using var source = new TelegramSource(
            settings.Telegram, TelegramSession(store), _loggerFactory.CreateLogger<TelegramSource>());

        var needed = source.ConnectAsync(Option(args, "--phone")).GetAwaiter().GetResult();

        while (needed is not null)
        {
            var answer = Ask(needed);

            if (string.IsNullOrWhiteSpace(answer))
            {
                Console.Error.WriteLine("error: nothing entered; not signed in.");
                return 1;
            }

            needed = source.ContinueLoginAsync(answer).GetAwaiter().GetResult();
        }

        var me = source.Me!;

        Console.WriteLine($"signed in as {me.first_name} {me.last_name} ({me.id})".Replace("  ", " ", StringComparison.Ordinal));
        Console.WriteLine($"session  {TelegramSession(store).Path}");
        Console.WriteLine(SecretFile.IsEncryptedAtRest
            ? "         encrypted for this Windows account"
            : "         readable only by you; this platform has no key store the app uses");

        // Listing the chats is what makes the next step possible: nothing is read until the user
        // says which conversations belong in the archive.
        var result = new SyncEngine(database, new FileSystemMediaStore(options), _loggerFactory.CreateLogger<SyncEngine>())
            .SyncAsync(source).GetAwaiter().GetResult();

        Console.WriteLine();
        Console.WriteLine($"{result.ChatsNotIncluded} chat(s) found and none read yet — decide which ones to keep:");
        Console.WriteLine($"    ahistory chats \"{args[1]}\"");

        return 0;
    }

    /// <summary>
    /// Lists a connected account's chats, and includes or ignores them.
    /// </summary>
    /// <remarks>
    /// An account is every channel someone follows as well as everyone they have ever written to.
    /// Reading all of it would bury the correspondence, so a chat contributes nothing until it is
    /// included — and an undecided chat is not an ignored one: it is waiting to be decided.
    /// </remarks>
    private static int Chats(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine(
                "usage: ahistory chats <save.db> [--include <id>…] [--ignore <id>…] "
                + "[--include-kind dm|group|channel|saved] [--ignore-kind …] [--undecided]");
            return 2;
        }

        var (database, _) = Open(args[1]);
        var store = new SyncStore(database);
        var sourceId = TelegramSourceId(database);

        if (sourceId is null)
        {
            Console.Error.WriteLine("error: no connected account in this save. Run `ahistory connect` first.");
            return 1;
        }

        foreach (var id in Values(args, "--include"))
        {
            store.Decide(sourceId, id, "include");
        }

        foreach (var id in Values(args, "--ignore"))
        {
            store.Decide(sourceId, id, "ignore");
        }

        if (Option(args, "--include-kind") is { } includeKind)
        {
            Console.WriteLine($"included {store.DecideKind(sourceId, includeKind, "include")} undecided {includeKind} chat(s)");
        }

        if (Option(args, "--ignore-kind") is { } ignoreKind)
        {
            Console.WriteLine($"ignored {store.DecideKind(sourceId, ignoreKind, "ignore")} undecided {ignoreKind} chat(s)");
        }

        var chats = store.Chats(sourceId, "telegram");
        var undecidedOnly = args.Contains("--undecided");

        foreach (var chat in chats.Where(c => !undecidedOnly || c.IsUndecided))
        {
            var mark = chat.Decision switch
            {
                "include" => "[keep]  ",
                "ignore" => "[skip]  ",
                _ => "[ ?  ]  ",
            };

            var when = chat.LastMessageUnix is { } unix
                ? DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("d MMM yyyy")
                : "—";

            Console.WriteLine(
                $"{mark}{chat.ChatId,-14} {Truncate(chat.Title ?? "(no title)", 34),-34} "
                + $"{chat.ThreadKind,-7} {when,-12} {chat.MessageCount,8:N0} in archive");
        }

        Console.WriteLine();
        Console.WriteLine(
            $"{chats.Count(c => c.IsIncluded)} kept, {chats.Count(c => c.Decision == "ignore")} skipped, "
            + $"{chats.Count(c => c.IsUndecided)} undecided.");

        return 0;
    }

    /// <summary>
    /// Reads the chats that were kept, and optionally stays connected for what arrives next.
    /// </summary>
    private static int Sync(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ahistory sync <save.db> [--live] [--no-raw-json]");
            return 2;
        }

        var (database, options) = Open(args[1]);
        var store = new SyncSettingsStore(logger: _loggerFactory.CreateLogger<SyncSettingsStore>());
        var settings = store.Load();

        if (!settings.Telegram.Enabled)
        {
            Console.Error.WriteLine("error: Telegram is switched off. `ahistory connect <save.db>` switches it on.");
            return 1;
        }

        using var source = new TelegramSource(
            settings.Telegram, TelegramSession(store), _loggerFactory.CreateLogger<TelegramSource>());

        var needed = source.ConnectAsync().GetAwaiter().GetResult();

        if (needed is not null)
        {
            Console.Error.WriteLine($"error: signing in is not finished ({needed}). Run `ahistory connect <save.db>`.");
            return 1;
        }

        var engine = new SyncEngine(
            database, new FileSystemMediaStore(options), _loggerFactory.CreateLogger<SyncEngine>());

        var syncOptions = new SyncOptions(StoreRawJson: !args.Contains("--no-raw-json"));
        var progress = new Progress<SyncProgress>(p =>
            Console.Write($"\r{p.ChatsDone,4}/{p.ChatsTotal} chats  {p.MessagesInserted,9:N0} new  {Truncate(p.Chat, 30),-30}"));

        var result = engine.SyncAsync(source, syncOptions, progress).GetAwaiter().GetResult();

        Console.Write('\r');
        Console.WriteLine($"read {result.ChatsRead} chat(s); {result.ChatsNotIncluded} not included");
        Console.WriteLine($"  {result.Stats}");

        if (!args.Contains("--live"))
        {
            return 0;
        }

        using var stopping = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stopping.Cancel();
        };

        Console.WriteLine();
        Console.WriteLine("following for new messages. Ctrl+C to stop.");

        engine.FollowAsync(source, syncOptions, stopping.Token).GetAwaiter().GetResult();

        Console.WriteLine("stopped.");

        return 0;
    }

    /// <summary>
    /// Reads the same messages twice — from an export and from the account — and reports every
    /// place the two disagree.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the test no fixture can be: the translator and the reader were written by the same
    /// hand from the same reading of the format, so they agree with each other whether or not they
    /// are right (D22, and the QIP reader that passed six tests and could not open a real file).
    /// Telegram's export and Telegram's API were written by different people; when both say the
    /// same thing about the same message, that is evidence.
    /// </para>
    /// <para>
    /// Attachment paths are deliberately not compared. The export names a file on disk and the
    /// account names an object id, and they are never going to match — which is exactly why a
    /// difference in them is not treated as an edit.
    /// </para>
    /// </remarks>
    private static int SyncCheck(string[] args)
    {
        if (args.Length < 3)
        {
            Console.Error.WriteLine("usage: ahistory sync-check <save.db> <telegram-export-folder> [--show N]");
            return 2;
        }

        var store = new SyncSettingsStore(logger: _loggerFactory.CreateLogger<SyncSettingsStore>());
        var settings = store.Load();

        if (!settings.Telegram.Enabled)
        {
            Console.Error.WriteLine("error: Telegram is switched off. `ahistory connect <save.db>` switches it on.");
            return 1;
        }

        var exported = ReadExport(args[2]);

        Console.WriteLine($"export   {exported.Count:N0} message(s) in {exported.Values.Select(c => c.ChatId).Distinct().Count()} chat(s)");

        using var source = new TelegramSource(
            settings.Telegram, TelegramSession(store), _loggerFactory.CreateLogger<TelegramSource>());

        if (source.ConnectAsync().GetAwaiter().GetResult() is { } needed)
        {
            Console.Error.WriteLine($"error: signing in is not finished ({needed}). Run `ahistory connect <save.db>`.");
            return 1;
        }

        var chats = source.ChatsAsync().GetAwaiter().GetResult().ToDictionary(c => c.ChatId, StringComparer.Ordinal);
        var fromAccount = new Dictionary<string, Claim>(StringComparer.Ordinal);

        foreach (var chatId in exported.Values.Select(c => c.ChatId).Distinct())
        {
            if (!chats.TryGetValue(chatId, out var chat))
            {
                Console.WriteLine($"chat     {chatId} is in the export but not offered by the account — skipped");
                continue;
            }

            ReadAccountChat(source, chat, exported, fromAccount);
        }

        return Report(exported, fromAccount, Number(args, "--show") ?? 10);
    }

    /// <summary>Walks one chat back to where the export's oldest message for it sits.</summary>
    private static void ReadAccountChat(
        TelegramSource source, RemoteChat chat, Dictionary<string, Claim> exported, Dictionary<string, Claim> into)
    {
        var wanted = exported.Values.Where(c => c.ChatId == chat.ChatId).Select(c => c.Id).ToArray();
        var oldest = wanted.Min();
        string? cursor = null;

        while (true)
        {
            // Nothing is downloaded: this compares what was said, and a photo's bytes are the same
            // bytes whichever route fetched them.
            var page = source.ReadAsync(chat, cursor, _ => false).GetAwaiter().GetResult();

            foreach (var message in page.Messages)
            {
                into[message.Uid] = Claim.Of(chat.ChatId, message);
            }

            cursor = page.NextCursor;

            if (page.IsComplete || cursor is null)
            {
                return;
            }

            // Far enough back: everything this page carried is older than anything the export has.
            if (page.Messages.Count > 0 && page.Messages.Max(m => Claim.Of(chat.ChatId, m).Id) < oldest)
            {
                return;
            }
        }
    }

    /// <summary>Reads a Telegram export into the same shape, without writing anything.</summary>
    private static Dictionary<string, Claim> ReadExport(string folder)
    {
        var sink = new ClaimSink();

        new Archive.Import.Telegram.TelegramImporter().Read(folder, sink);

        return sink.Claims;
    }

    private static int Report(
        Dictionary<string, Claim> exported, Dictionary<string, Claim> fromAccount, int show)
    {
        // Only where the two overlap: the export may predate messages the account still has, and
        // the account may have lost what the export kept.
        var shared = exported.Keys.Where(fromAccount.ContainsKey).ToArray();
        var differing = shared.Where(uid => !exported[uid].SaysTheSame(fromAccount[uid])).ToArray();

        // The stretch both sides describe, per chat. An export is a snapshot and both sides keep
        // moving, so a message outside it is not a disagreement — and working that out per uid
        // meant rescanning the whole export for each one.
        var covered = exported.Values
            .GroupBy(c => c.ChatId, StringComparer.Ordinal)
            .ToDictionary(
                g => g.Key,
                g => (Oldest: g.Min(c => c.Id), Newest: g.Max(c => c.Id)),
                StringComparer.Ordinal);

        var onlyExported = exported.Keys.Where(uid => !fromAccount.ContainsKey(uid) && Compared(exported[uid])).ToArray();
        var onlyAccount = fromAccount.Keys.Where(uid => !exported.ContainsKey(uid) && Compared(fromAccount[uid])).ToArray();

        Console.WriteLine($"account  {fromAccount.Count:N0} message(s) read back");
        Console.WriteLine($"shared   {shared.Length:N0} message(s) have the same uid on both sides");
        Console.WriteLine($"agree    {shared.Length - differing.Length:N0}");
        Console.WriteLine($"differ   {differing.Length:N0}");
        Console.WriteLine($"export-only {onlyExported.Length:N0}   account-only {onlyAccount.Length:N0}");

        // Ids, never the words: a check run against real correspondence must not print any of it.
        Print("differ", differing);
        Print("export only", onlyExported);
        Print("account only", onlyAccount);

        Console.WriteLine();

        if (shared.Length == 0)
        {
            Console.WriteLine("no message matched by uid at all — the chat ids the two sides use do not line up.");
            return 1;
        }

        var clean = differing.Length == 0 && onlyExported.Length == 0 && onlyAccount.Length == 0;

        Console.WriteLine(clean
            ? "the account and the export agree on every message they share."
            : "they disagree — the uid, the text or the entities differ where they should not.");

        return clean ? 0 : 1;

        void Print(string label, string[] uids)
        {
            foreach (var uid in uids.Take(show))
            {
                Console.WriteLine($"  {label,-12} {uid}");
            }

            if (uids.Length > show)
            {
                Console.WriteLine($"  {label,-12} … and {uids.Length - show:N0} more");
            }
        }

        bool Compared(Claim claim) =>
            covered.TryGetValue(claim.ChatId, out var range) && claim.Id >= range.Oldest && claim.Id <= range.Newest;
    }

    /// <summary>What one message says, as both routes should describe it.</summary>
    private sealed record Claim(string ChatId, long Id, string Plaintext, string? Entities, string? Action)
    {
        internal static Claim Of(string chatId, NormalizedMessage message) =>
            new(chatId,
                long.Parse(message.Uid[(message.Uid.LastIndexOf('/') + 1)..], System.Globalization.CultureInfo.InvariantCulture),
                message.Plaintext,
                Canonical(message.EntitiesJson),
                message.ServiceAction);

        /// <summary>JSON with its whitespace gone: an export keeps Telegram's indentation.</summary>
        private static string? Canonical(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                return System.Text.Json.Nodes.JsonNode.Parse(json)?.ToJsonString();
            }
            catch (System.Text.Json.JsonException)
            {
                return json;
            }
        }

        internal bool SaysTheSame(Claim other) =>
            string.Equals(Plaintext, other.Plaintext, StringComparison.Ordinal)
            && string.Equals(Entities, other.Entities, StringComparison.Ordinal)
            && string.Equals(Action, other.Action, StringComparison.Ordinal);
    }

    /// <summary>Collects what an export says, without a save to write it into.</summary>
    private sealed class ClaimSink : IImportSink
    {
        internal Dictionary<string, Claim> Claims { get; } = new(StringComparer.Ordinal);

        public void OnOwner(NormalizedIdentity owner)
        {
        }

        public void OnThread(NormalizedThread thread)
        {
        }

        public void OnMessage(NormalizedThread thread, NormalizedMessage message) =>
            Claims[message.Uid] = Claim.Of(thread.SourceThreadId, message);
    }

    /// <summary>The session file — outside the save, so a save that is copied carries no account.</summary>
    private static SecretFile TelegramSession(SyncSettingsStore store) =>
        new(Path.Combine(store.Directory, "connectors", "telegram.session"));

    /// <summary>The source a connected Telegram account writes into, if there is one.</summary>
    private static string? TelegramSourceId(Database database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT c.source_id
            FROM sync_chat c
            JOIN import_source s ON s.id = c.source_id
            WHERE s.platform = 'telegram'
            LIMIT 1;
            """;

        return command.ExecuteScalar() as string;
    }

    /// <summary>Asks for whatever signing in needs next, hiding what should not be echoed.</summary>
    private static string? Ask(string needed)
    {
        switch (needed)
        {
            case "verification_code":
                Console.Write("code Telegram just sent you: ");
                return Console.ReadLine();

            case "password":
                Console.Write("two-step verification password: ");
                return ReadHidden();

            case "phone_number":
                Console.Write("phone number, with country code: ");
                return Console.ReadLine();

            default:
                Console.Write($"{needed.Replace('_', ' ')}: ");
                return Console.ReadLine();
        }
    }

    /// <summary>
    /// Reads a line without echoing it.
    /// </summary>
    /// <remarks>
    /// A two-step password typed into a terminal that echoes it stays on screen, and in a
    /// scrollback someone else may read. Falls back to a plain read where there is no console to
    /// control — a redirected stdin has nothing to hide it from.
    /// </remarks>
    private static string? ReadHidden()
    {
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine();
        }

        var typed = new System.Text.StringBuilder();

        while (true)
        {
            var key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return typed.ToString();

                case ConsoleKey.Backspace when typed.Length > 0:
                    typed.Length--;
                    break;

                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        typed.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }

    /// <summary>Every value given for a repeatable option.</summary>
    private static IEnumerable<string> Values(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == name)
            {
                yield return args[i + 1];
            }
        }
    }

    /// <summary>Opens a save the way every command here does: validate, migrate, hand back both.</summary>
    private static (Database Database, ArchiveOptions Options) Open(string path)
    {
        var options = new ArchiveOptions { DatabasePath = path };
        options.Validate();

        var database = new Database(options, _loggerFactory.CreateLogger<Database>());
        database.Migrate();

        return (database, options);
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

        // Reads columns that only exist at this schema, so the same check every other command
        // makes applies here: it reports what a save is made of, or says why it cannot.
        database.Migrate();

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

    /// <summary>
    /// Reports what the AI layer has read of an archive, and optionally reads more of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Segmentation needs no model, no key and no network, which is what makes it demonstrable
    /// here: the queue, the priority order, resumability and the coverage counts can all be seen
    /// working before anything costs a token.
    /// </para>
    /// <para>
    /// It uses the same runner the app uses rather than calling the segmenter directly, because a
    /// command-line drain and a drain nobody is watching must not be able to behave differently.
    /// </para>
    /// </remarks>
    private static int Ai(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: ahistory ai <path-to-save.db> [--segment]");
            return 2;
        }

        var options = new ArchiveOptions { DatabasePath = args[1] };
        options.Validate();

        var database = new Database(options);
        database.Migrate();

        var coverage = new AiCoverage(database);
        var jobs = new AiJobs(database);

        if (args.Contains("--segment"))
        {
            var segmenter = new SessionSegmenter(database);

            // Drains directly rather than starting the background loop: the loop is what the
            // enabled switch governs, and typing this command is the decision the switch stands
            // in for. Nothing here calls a model, so there is nothing else to consent to.
            using var runner = new AiRunner(
                jobs, new AiState(new AiSettingsStore()), [new SegmentJobHandler(segmenter)]);

            var queued = new AiWork(database, jobs, segmenter, runner).PlanSegmentation();

            Console.WriteLine($"queued   {queued:N0} thread(s)");

            var started = DateTime.UtcNow;
            var handled = runner.DrainAsync().GetAwaiter().GetResult();
            var elapsed = DateTime.UtcNow - started;

            Console.WriteLine($"read     {handled:N0} thread(s) in {elapsed.TotalSeconds:N1}s");
        }

        if (args.Contains("--extract"))
        {
            var code = Extract(database, jobs, args);

            if (code != 0)
            {
                return code;
            }
        }

        if (args.Contains("--all"))
        {
            var code = Everything(database, jobs, options, args);

            if (code != 0)
            {
                return code;
            }
        }

        var summary = coverage.Summary();
        var counts = jobs.Counts();

        Console.WriteLine($"threads  {summary.ThreadsSegmented:N0} of {summary.Threads:N0} split into sessions");
        Console.WriteLine($"messages {summary.MessagesInSessions:N0} of {summary.Messages:N0} in a session");
        Console.WriteLine($"sessions {summary.Sessions:N0}, of which {summary.Substantive:N0} look worth reading");
        Console.WriteLine($"queue    {counts.Pending:N0} waiting, {counts.Done:N0} done, {counts.Failed:N0} failed");

        return 0;
    }

    /// <summary>
    /// Reads sessions with the configured model, as the background runner would.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This one costs money and sends correspondence to whatever endpoint is configured, so unlike
    /// segmentation it refuses to run until AI is switched on and pointed at a model. The command
    /// is not a way around the consent step; it is a way to watch it work.
    /// </para>
    /// <para>
    /// <c>--limit</c> exists because reading a real archive is an evening's work and a
    /// demonstration should be a minute's. Stopping early is free — the sessions that were read
    /// stay read, and the rest are still queued.
    /// </para>
    /// </remarks>
    private static int Extract(Database database, AiJobs jobs, string[] args)
    {
        var state = new AiState(new AiSettingsStore());

        if (!state.Current.IsUsable)
        {
            Console.Error.WriteLine(
                "error: AI is not configured. Switch it on and choose a model in the app first.");
            return 1;
        }

        var factory = new LlmProviderFactory();

        if (!Agreed(state, factory, args))
        {
            return 1;
        }

        var limit = Limit(args);
        var client = new AiClient(factory, new AiInteractions(database));

        var extractor = new ExtractRunner(
            client, new ExtractionWindows(database), new FactWriter(database),
            _loggerFactory.CreateLogger<ExtractRunner>());

        using var runner = new AiRunner(jobs, state, [new ExtractJobHandler(extractor, state, factory)]);

        var segmenter = new SessionSegmenter(database);
        var queued = new AiWork(database, jobs, segmenter, runner).PlanExtraction();

        Console.WriteLine($"queued   {queued:N0} session(s) to read with {state.Current.ModelFor(AiWorkKind.Utility)}");

        var started = DateTime.UtcNow;
        var handled = runner.DrainAsync(limit).GetAwaiter().GetResult();
        var elapsed = DateTime.UtcNow - started;

        Console.WriteLine(
            $"read     {handled:N0} session(s) in {elapsed.TotalSeconds:N1}s"
            + (handled == 0 ? string.Empty : $" ({elapsed.TotalSeconds / handled:N1}s each)"));

        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT (SELECT count(*) FROM fact WHERE retracted_utc IS NULL),
                   (SELECT count(*) FROM fact_citation),
                   (SELECT count(*) FROM derived_artifact WHERE kind = 'session_extract');
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        Console.WriteLine($"facts    {reader.GetInt64(0):N0} from {reader.GetInt64(2):N0} session(s), "
            + $"{reader.GetInt64(1):N0} citation(s)");

        return 0;
    }

    /// <summary>
    /// The same agreement the window asks for, asked here by flag.
    /// </summary>
    /// <remarks>
    /// Typing a command says "read these", not "and send them wherever the settings happen to
    /// point" — so without a yes for this endpoint nothing is sent, and <c>--consent</c> is how to
    /// give one.
    /// </remarks>
    private static bool Agreed(AiState state, LlmProviderFactory factory, string[] args)
    {
        if (AiConsent.CoversExtraction(state.Current, factory))
        {
            return true;
        }

        var destination = AiConsent.Destination(state.Current, factory);

        if (!args.Contains("--consent"))
        {
            Console.Error.WriteLine(
                $"error: sending archive text to {destination} has not been agreed to. Press Start "
                + "on the AI activity page, or pass --consent to agree for this endpoint.");
            return false;
        }

        var agreed = state.Current.Clone();
        agreed.ExtractionConfirmedFor = destination;
        state.Update(agreed);

        Console.WriteLine($"agreed   archive text may be sent to {destination}");

        return true;
    }

    /// <summary>
    /// Runs the whole AI layer over an archive, as the app's background runner would.
    /// </summary>
    /// <remarks>
    /// Segmentation, reading, merging, the diary, the index and media — every kind of work, planned
    /// by the same invalidation the app uses and drained by the same runner, until nothing is left
    /// or <c>--limit</c> is reached. The daily budget holds here as it does in the window.
    /// </remarks>
    private static int Everything(Database database, AiJobs jobs, ArchiveOptions options, string[] args)
    {
        var state = new AiState(new AiSettingsStore());

        if (!state.Current.IsUsable)
        {
            Console.Error.WriteLine(
                "error: AI is not configured. Switch it on and choose a model in the app first.");
            return 1;
        }

        var factory = new LlmProviderFactory();

        if (!Agreed(state, factory, args))
        {
            return 1;
        }

        var interactions = new AiInteractions(database);
        var client = new AiClient(factory, interactions);
        var windows = new ExtractionWindows(database);
        var segmenter = new SessionSegmenter(database);
        var merger = new FactMerger(database, client);
        var inputs = new DiaryInputs(database);
        var store = new DiaryStore(database, inputs);
        var diary = new DiaryRunner(database, inputs, store, client);
        var embeddings = new EmbeddingStore(database);
        var media = new MediaReader(database, new FileSystemMediaStore(options), client);

        IAiJobHandler[] handlers =
        [
            new SegmentJobHandler(segmenter),
            new ExtractJobHandler(
                new ExtractRunner(client, windows, new FactWriter(database), _loggerFactory.CreateLogger<ExtractRunner>()),
                state, factory),
            new AdjudicateJobHandler(merger, state, factory),
            new DiaryJobHandler(diary, state, factory),
            new RollupJobHandler(diary, state, factory),
            new EmbedJobHandler(embeddings, new SessionText(windows), client, state, factory),
            new OcrJobHandler(media, state, factory),
            new TranscribeJobHandler(media, state, factory),
        ];

        using var runner = new AiRunner(jobs, state, handlers, budget: new AiBudget(interactions));

        var work = new AiWork(
            database, jobs, segmenter, runner, merger, new DiaryPlanner(database, inputs, store), embeddings, state, media);

        var limit = Limit(args);
        var total = 0;
        var started = DateTime.UtcNow;

        work.PlanSegmentation();

        // Each pass makes the next one's work: sessions to read, facts to merge, months to write.
        while (total < limit)
        {
            work.PlanAll();

            var handled = runner.DrainAsync(limit - total).GetAwaiter().GetResult();

            total += handled;

            if (handled == 0)
            {
                break;
            }

            Console.WriteLine($"pass     {handled:N0} job(s), {total:N0} so far");
        }

        if (runner.IsOverBudget)
        {
            Console.WriteLine("budget   the daily token budget is reached; the rest waits for tomorrow");
        }

        using var connection = database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT (SELECT count(*) FROM fact WHERE retracted_utc IS NULL AND merged_into IS NULL),
                   (SELECT count(*) FROM fact WHERE merged_into IS NOT NULL),
                   (SELECT count(*) FROM derived_artifact WHERE kind IN ('diary', 'rollup')),
                   (SELECT count(*) FROM embedding),
                   (SELECT count(*) FROM derived_artifact WHERE kind IN ('ocr', 'transcript'));
            """;

        using var reader = command.ExecuteReader();
        reader.Read();

        Console.WriteLine($"done     {total:N0} job(s) in {(DateTime.UtcNow - started).TotalSeconds:N1}s");
        Console.WriteLine($"facts    {reader.GetInt64(0):N0}, and {reader.GetInt64(1):N0} folded into them");
        Console.WriteLine($"diary    {reader.GetInt64(2):N0} text(s)");
        Console.WriteLine($"index    {reader.GetInt64(3):N0} vector(s)");
        Console.WriteLine($"media    {reader.GetInt64(4):N0} transcript(s) and image text(s)");

        return 0;
    }

    /// <summary>How many jobs a drain may take, from <c>--limit N</c>. Unbounded without it.</summary>
    private static int Limit(string[] args)
    {
        var index = Array.IndexOf(args, "--limit");

        return index >= 0 && index + 1 < args.Length && int.TryParse(args[index + 1], out var limit)
            ? limit
            : int.MaxValue;
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
              ahistory init <save.db> [--upgrade] [--no-backup]
                                    create a save, or carry an older one forward
              ahistory hash <file>              show the content address a file would take
              ahistory import <save.db> <folder> [--source <id>] [--me <id>] [--format <platform>] [--no-raw-json]
                                                import an export folder; the format is detected.
                                                --me names your own account for the formats that
                                                do not state one (VK, QIP)
              ahistory sources <save.db>        list the sources in a save
              ahistory watch <save.db> [list | add <folder> [--format <p>] [--source <id>] [--me <id>]
                                       | remove <folder> | check | follow]
                                                keep a folder in sync: re-import it whenever a
                                                scheduled export changes it. `check` looks once,
                                                `follow` keeps looking until Ctrl+C
              ahistory stats <save.db>          what the archive is made of
              ahistory connect <save.db> [--api-id <n>] [--api-hash <h>] [--phone <+number>] [--off]
                                                sign in to Telegram and list its chats. Needs an
                                                api_id and api_hash of your own from
                                                my.telegram.org. Nothing is contacted until you
                                                run this; --off switches it back off
              ahistory chats <save.db> [--include <id>…] [--ignore <id>…] [--include-kind <kind>]
                                                which of a connected account's chats belong in the
                                                archive. Nothing is read until it is included
              ahistory sync <save.db> [--live] [--no-raw-json]
                                                read the chats you kept, and with --live keep
                                                reading while this runs
              ahistory sync-check <save.db> <export-folder> [--show N]
                                                read the same messages from an export and from the
                                                account and report where they disagree. The one
                                                check a fixture cannot be
              ahistory ai <save.db> [--segment] [--extract] [--all] [--consent] [--limit N]
                                                what the AI layer has read of this archive.
                                                --segment splits it into sessions (no model);
                                                --extract reads them with the configured model;
                                                --all runs everything: reading, merging, the
                                                diary, the search index and media;
                                                --consent agrees to send text to that endpoint
              ahistory synth <folder> [--messages N] [--chats N]
                                                write a synthetic export (no real data;
                                                --messages is an upper bound)

            A save is the .db file plus a media folder beside it. Both are created by `init`.
            """);

        return 2;
    }
}
