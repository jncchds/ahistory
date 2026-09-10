using Archive.Import.Hangouts;
using Archive.Import.Qip;
using Archive.Import.Telegram;
using Archive.Import.Vk;

namespace Archive.Import.Tests;

/// <summary>
/// §2's build order calls the second importer the thing that proves the schema was right. This is
/// where that gets cashed: four formats, one archive, one set of rules.
/// </summary>
public sealed class MultiPlatformTests
{
    /// <summary>Each importer recognizes only its own format.</summary>
    [Theory]
    [InlineData("telegram", typeof(TelegramImporter))]
    [InlineData("hangouts", typeof(HangoutsImporter))]
    [InlineData("vk", typeof(VkImporter))]
    public void Detection_picks_the_right_importer(string kind, Type expected)
    {
        var folder = kind switch
        {
            "telegram" => Exports.WriteGroupAndDm("multi-detect-telegram"),
            "hangouts" => Exports.Hangouts().Write(Fixtures.Temp("multi-detect-hangouts")),
            _ => Exports.Vk().Write(Fixtures.Temp("multi-detect-vk")),
        };

        var match = new ImporterRegistry().Detect(folder);

        Assert.NotNull(match);
        Assert.IsType(expected, match!.Importer);
    }

    [Fact]
    public void Qip_is_detected_from_its_binary_signature()
    {
        var match = new ImporterRegistry().Detect(Exports.WriteQip(Fixtures.Temp("multi-qip-detect")));

        Assert.NotNull(match);
        Assert.IsType<QipImporter>(match!.Importer);
    }

    /// <summary>
    /// A folder nothing recognizes is refused by name rather than guessed at. Picking the
    /// least-wrong importer would fill an archive with nonsense.
    /// </summary>
    [Fact]
    public void A_folder_nothing_recognizes_matches_nothing()
    {
        var folder = Fixtures.Temp("multi-unknown");
        File.WriteAllText(Path.Combine(folder, "notes.txt"), "not an export");

        Assert.Null(new ImporterRegistry().Detect(folder));
    }

    /// <summary>
    /// Four platforms in one archive, which is the whole point: one person's history is not
    /// one product's history.
    /// </summary>
    [Fact]
    public void Every_format_lands_in_the_same_archive()
    {
        using var save = new TempSave();

        save.Import(Exports.WriteGroupAndDm("multi-telegram"));
        save.Import(Exports.Hangouts().Write(Fixtures.Temp("multi-hangouts")));
        save.Import(Exports.Vk().Write(Fixtures.Temp("multi-vk")));
        save.Import(Exports.WriteQip(Fixtures.Temp("multi-all")));

        Assert.Equal(4, save.Scalar<long>("SELECT count(DISTINCT platform) FROM thread;"));
        Assert.Equal(4, save.Scalar<long>("SELECT count(DISTINCT platform) FROM import_source;"));

        // Telegram 5, Hangouts 4, VK 4, QIP 3.
        Assert.Equal(16, save.Scalar<long>("SELECT count(*) FROM message;"));

        // Everything is searchable regardless of which importer produced it.
        Assert.Equal(
            save.Scalar<long>("SELECT count(*) FROM message;"),
            save.Scalar<long>("SELECT count(*) FROM search_document;"));
    }

    /// <summary>
    /// P3 is a property of the app, not of one importer: re-importing anything changes nothing.
    /// </summary>
    [Theory]
    [InlineData("hangouts")]
    [InlineData("vk")]
    [InlineData("qip")]
    public void Re_importing_changes_nothing_whatever_the_format(string kind)
    {
        using var save = new TempSave();

        var folder = kind switch
        {
            "hangouts" => Exports.Hangouts().Write(Fixtures.Temp("multi-detect-hangouts")),
            "vk" => Exports.Vk().Write(Fixtures.Temp($"multi-reimport-{kind}")),
            _ => Exports.WriteQip(Fixtures.Temp($"multi-reimport-{kind}")),
        };

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
        Assert.Equal(second.MessagesSeen, second.MessagesSkipped);
    }

    /// <summary>
    /// One person across platforms is the reason identities are separate from people (§1).
    /// Merging a Hangouts account into a Telegram contact makes one continuous conversation out
    /// of two products.
    /// </summary>
    [Fact]
    public void Accounts_from_different_platforms_can_be_merged_into_one_person()
    {
        using var save = new TempSave();

        save.Import(Exports.WriteGroupAndDm("multi-telegram"));
        save.Import(Exports.Hangouts().Write(Fixtures.Temp("multi-hangouts")));

        var telegramSam = save.Scalar<string>(
            "SELECT id FROM identity WHERE platform = 'telegram' AND display_name = 'Sam Ruiz';")!;
        var hangoutsSam = save.Scalar<string>(
            "SELECT id FROM identity WHERE platform = 'hangouts' AND display_name = 'Sam Ruiz';")!;

        var person = save.Scalar<string>(
            $"SELECT person_id FROM identity_person WHERE identity_id = '{telegramSam}';")!;

        new Archive.Data.IdentityMerger(save.Database).MergeInto(hangoutsSam, person);

        Assert.Equal(2, save.Scalar<long>(
            $"SELECT count(*) FROM identity_person WHERE person_id = '{person}';"));

        // Their conversation now spans both platforms.
        var conversation = new Archive.Data.PersonConversation(save.Database).Page(person);

        Assert.Contains(conversation.Messages, m => m.Uid.StartsWith("tg/", StringComparison.Ordinal));
        Assert.Contains(conversation.Messages, m => m.Uid.StartsWith("gh/", StringComparison.Ordinal));
    }
}
