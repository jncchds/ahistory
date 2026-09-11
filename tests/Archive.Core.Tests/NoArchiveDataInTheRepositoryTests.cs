namespace Archive.Core.Tests;

/// <summary>
/// Nothing shaped like a chat archive may live in this repository.
/// </summary>
/// <remarks>
/// <para>
/// An archive is people's private correspondence — the same thing P6 refuses to let into a log.
/// A file inside the source tree that <em>looks</em> like an export is one careless copy away from
/// being a real one: the tempting way to debug a parser is to drop the file that broke it next to
/// the ones already there, and the tempting way to fix a failing test is to overwrite a small
/// export with a large one that reproduces the bug. Both are a single `git add` from publishing
/// somebody's history.
/// </para>
/// <para>
/// So the rule is the blunt one rather than a judgement about whether a given file is real:
/// exports are described in code and written to a temporary folder when a test runs
/// (<c>Archive.Import.Synthetic</c>), and none of them is ever committed.
/// </para>
/// </remarks>
public sealed class NoArchiveDataInTheRepositoryTests
{
    /// <summary>Names and extensions that only an exported archive has.</summary>
    private static readonly string[] Forbidden =
    [
        // Telegram Desktop, JSON export.
        "result.json",
        // Google Takeout: Hangouts, and Google Chat's per-conversation and per-account files.
        "Hangouts.json",
        "group_info.json",
        "user_info.json",
        // Meta: Messenger and Instagram threads, and the profile files that name the owner.
        "message_1.json",
        "profile_information.json",
        "personal_information.json",
        // WhatsApp's iPhone export; the Android one is caught by name below.
        "_chat.txt",
        // Skype's export, Google Chat's per-conversation file, and Discord's newer package.
        "messages.json",
        // QIP history, and QIP's archived history.
        ".qhf",
        ".ahf",
        // A save itself, and its write-ahead log.
        ".db",
        ".db-wal",
        ".db-shm",
    ];

    /// <summary>Build output and version control, which are not part of the repository's content.</summary>
    private static readonly string[] SkippedDirectories =
    [
        "bin", "obj", ".git", ".vs", ".idea", "artifacts", "publish", "scratch",
    ];

    [Fact]
    public void No_file_in_the_repository_looks_like_an_exported_archive()
    {
        var offenders = Walk(RepoRoot.Find())
            .Where(file => Forbidden.Any(
                pattern => file.Name.Equals(pattern, StringComparison.OrdinalIgnoreCase)
                    || file.Name.EndsWith(pattern, StringComparison.OrdinalIgnoreCase)))
            .Select(file => file.FullName)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "These files look like exported chat archives and must not be in the repository:\n"
            + string.Join("\n", offenders)
            + "\n\nBuild the shape you need with Archive.Import.Synthetic instead, and write it to a "
            + "temporary folder. A real archive belongs in scratch/, which is ignored.");
    }

    /// <summary>
    /// VK's archive is HTML, so its pages cannot be recognized by extension alone.
    /// </summary>
    /// <remarks>
    /// Kept separate because the match has to be narrower: <c>messages0.html</c> is the page name
    /// VK writes, and a general ban on .html would catch documentation the moment any gets written.
    /// </remarks>
    [Fact]
    public void No_file_in_the_repository_looks_like_a_vk_message_page()
    {
        var offenders = Walk(RepoRoot.Find())
            .Where(file =>
                file.Name.StartsWith("messages", StringComparison.OrdinalIgnoreCase)
                && file.Name.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            .Select(file => file.FullName)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// SMS Backup &amp; Restore names its files <c>sms-&lt;timestamp&gt;.xml</c>, and WhatsApp on
    /// Android <c>WhatsApp Chat with &lt;name&gt;.txt</c>.
    /// </summary>
    [Fact]
    public void No_file_in_the_repository_looks_like_an_sms_backup_or_a_whatsapp_chat()
    {
        var offenders = Walk(RepoRoot.Find())
            .Where(file =>
                ((file.Name.StartsWith("sms-", StringComparison.OrdinalIgnoreCase)
                  || file.Name.StartsWith("calls-", StringComparison.OrdinalIgnoreCase))
                 && file.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                || (file.Name.StartsWith("WhatsApp Chat", StringComparison.OrdinalIgnoreCase)
                    && file.Name.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)))
            .Select(file => file.FullName)
            .ToArray();

        Assert.Empty(offenders);
    }

    /// <summary>
    /// The old fixtures folder stays gone.
    /// </summary>
    /// <remarks>
    /// Named explicitly because re-creating it is how the rule above would be worked around
    /// without anyone deciding to: the folder reappears with something innocuous in it, and then
    /// it is the obvious place to put the next file.
    /// </remarks>
    [Fact]
    public void There_is_no_fixtures_folder()
    {
        var fixtures = Path.Combine(RepoRoot.Find().FullName, "tests", "fixtures");

        Assert.False(
            Directory.Exists(fixtures),
            $"'{fixtures}' exists. Export shapes belong in Archive.Import.Synthetic, built at test time.");
    }

    private static IEnumerable<FileInfo> Walk(DirectoryInfo directory)
    {
        foreach (var file in directory.EnumerateFiles())
        {
            yield return file;
        }

        foreach (var child in directory.EnumerateDirectories())
        {
            if (SkippedDirectories.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var file in Walk(child))
            {
                yield return file;
            }
        }
    }
}
