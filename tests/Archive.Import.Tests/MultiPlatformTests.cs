using System.Buffers.Binary;
using System.Text;
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
    private static string WriteQip(string name)
    {
        var history = Path.Combine(Fixtures.Temp(name), "12345678", "History");
        System.IO.Directory.CreateDirectory(history);

        var encoded = QipImporter.Encode("привет");
        var uin = Encoding.ASCII.GetBytes("87654321");
        var nick = Encoding.UTF8.GetBytes("Марина");

        var headerLength = 0x2E + uin.Length + 2 + nick.Length;
        var block = new byte[0x23 + encoded.Length];
        var file = new byte[headerLength + block.Length];

        file[0] = (byte)'Q';
        file[1] = (byte)'H';
        file[2] = (byte)'F';
        // Both size fields measure what follows them — see QipImporterTests for the full layout.
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0x04), file.Length - 8);
        BinaryPrimitives.WriteInt32BigEndian(file.AsSpan(0x22), 1);
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(0x2C), (short)uin.Length);
        uin.CopyTo(file.AsSpan(0x2E));
        BinaryPrimitives.WriteInt16BigEndian(file.AsSpan(0x2E + uin.Length), (short)nick.Length);
        nick.CopyTo(file.AsSpan(0x2E + uin.Length + 2));

        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x00), 1);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x02), block.Length - 6);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x06), 1);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x08), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x0A), 1);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x0E), 2);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x10), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x12),
            (int)new DateTimeOffset(2008, 5, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds());
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x16), 3);
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x18), 3);
        block[0x1C] = 1;
        BinaryPrimitives.WriteInt16BigEndian(block.AsSpan(0x1D), 4);
        BinaryPrimitives.WriteInt32BigEndian(block.AsSpan(0x1F), encoded.Length);
        encoded.CopyTo(block.AsSpan(0x23));
        block.CopyTo(file.AsSpan(headerLength));

        File.WriteAllBytes(Path.Combine(history, "87654321.qhf"), file);

        return history;
    }

    /// <summary>Each importer recognizes only its own format.</summary>
    [Theory]
    [InlineData("telegram", typeof(TelegramImporter))]
    [InlineData("hangouts", typeof(HangoutsImporter))]
    [InlineData("vk", typeof(VkImporter))]
    public void Detection_picks_the_right_importer(string kind, Type expected)
    {
        var folder = kind switch
        {
            "telegram" => Fixtures.Directory("group-and-dm"),
            "hangouts" => Path.Combine(Fixtures.Root, "hangouts", "simple"),
            _ => Path.Combine(Fixtures.Root, "vk", "simple"),
        };

        var match = new ImporterRegistry().Detect(folder);

        Assert.NotNull(match);
        Assert.IsType(expected, match!.Importer);
    }

    [Fact]
    public void Qip_is_detected_from_its_binary_signature()
    {
        var match = new ImporterRegistry().Detect(WriteQip("multi-qip-detect"));

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

        save.Import(Fixtures.Directory("group-and-dm"));
        save.Import(Path.Combine(Fixtures.Root, "hangouts", "simple"));
        save.Import(Path.Combine(Fixtures.Root, "vk", "simple"));
        save.Import(WriteQip("multi-all"));

        Assert.Equal(4, save.Scalar<long>("SELECT count(DISTINCT platform) FROM thread;"));
        Assert.Equal(4, save.Scalar<long>("SELECT count(DISTINCT platform) FROM import_source;"));

        // Telegram 5, Hangouts 4, VK 4, QIP 1.
        Assert.Equal(14, save.Scalar<long>("SELECT count(*) FROM message;"));

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
            "hangouts" => Path.Combine(Fixtures.Root, "hangouts", "simple"),
            "vk" => Path.Combine(Fixtures.Root, "vk", "simple"),
            _ => WriteQip($"multi-reimport-{kind}"),
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

        save.Import(Fixtures.Directory("group-and-dm"));
        save.Import(Path.Combine(Fixtures.Root, "hangouts", "simple"));

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
