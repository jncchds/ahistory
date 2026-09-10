using Archive.Import.Synthetic;
using Archive.Import.Vk;

namespace Archive.Import.Tests;

/// <summary>
/// VK's archive is HTML, and its traps are its own.
/// </summary>
public sealed class VkImporterTests
{
    private static string VkArchive(string name) => Exports.Vk().Write(Fixtures.Temp(name));

    private static RecordingSink Read(string name)
    {
        var sink = new RecordingSink();
        new VkImporter().Read(VkArchive(name), sink);

        return sink;
    }

    [Fact]
    public void An_archive_is_recognized_by_its_messages_folder()
    {
        var detection = new VkImporter().Detect(VkArchive("vk-detect"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(2, detection.FileCount);
    }

    [Fact]
    public void A_telegram_export_is_not_mistaken_for_one()
    {
        Assert.Equal(
            ImportConfidence.None,
            new VkImporter().Detect(Exports.WriteGroupAndDm("vk-not-telegram")).Confidence);
    }

    [Fact]
    public void Conversations_are_read_with_their_names()
    {
        var sink = Read("vk-simple");

        Assert.Equal(2, sink.Threads.Count);
        Assert.Contains(sink.Threads, t => t.Title == "Sam Ruiz" && t.Kind == "dm");
        Assert.Contains(sink.Threads, t => t.Title == "Поездка в Прагу" && t.Kind == "group");
    }

    /// <summary>
    /// VK omits the sender link on your own messages rather than linking to you. Read as
    /// "unknown", half of every conversation ends up on the wrong side.
    /// </summary>
    [Fact]
    public void A_message_with_no_sender_link_is_yours()
    {
        var sink = Read("vk-simple");

        var mine = sink.Messages.Single(m => m.Message.Plaintext.Contains("we should go back", StringComparison.Ordinal));
        var theirs = sink.Messages.Single(m => m.Message.Plaintext.Contains("harbour", StringComparison.Ordinal));

        Assert.Equal(sink.Owner!.SourceIdentityId, mine.Message.Sender!.SourceIdentityId);
        Assert.Equal("222", theirs.Message.Sender!.SourceIdentityId);
        Assert.Equal("Sam Ruiz", theirs.Message.Sender.DisplayName);
    }

    /// <summary>
    /// An archive that will not name its account gets a placeholder marked as a guess.
    /// </summary>
    /// <remarks>
    /// It used to be the fixed identity <c>vk:self</c>, created as though the archive had stated
    /// it. That is wrong twice over: two archives from two different accounts collapse onto one
    /// person, and being non-synthetic hides it from the merge UI's "identified by name only"
    /// filter while making the link one <c>Unmerge</c> refuses to detach.
    /// </remarks>
    [Fact]
    public void An_archive_that_does_not_name_its_account_gets_a_placeholder_marked_as_a_guess()
    {
        var owner = Read("vk-placeholder").Owner;

        Assert.NotNull(owner);
        Assert.True(owner!.IsSynthetic);
        Assert.StartsWith("folder:", owner.SourceIdentityId!, StringComparison.Ordinal);
    }

    /// <summary>Two archives from two accounts must not collapse onto one identity.</summary>
    [Fact]
    public void Two_archives_get_two_placeholders()
    {
        var first = Read("vk-account-one").Owner!;
        var second = Read("vk-account-two").Owner!;

        Assert.NotEqual(first.SourceIdentityId, second.SourceIdentityId);
    }

    /// <summary>Told which account is yours, the owner stops being a guess.</summary>
    [Fact]
    public void An_account_the_user_supplies_is_the_owner_and_is_not_a_guess()
    {
        var sink = new RecordingSink();

        new VkImporter().Read(VkArchive("vk-stated"), sink, new ImportOptions(OwnerAccountId: "999"));

        Assert.Equal("999", sink.Owner!.SourceIdentityId);
        Assert.False(sink.Owner.IsSynthetic);

        var mine = sink.Messages.Single(
            m => m.Message.Plaintext.Contains("we should go back", StringComparison.Ordinal));

        Assert.Equal("999", mine.Message.Sender!.SourceIdentityId);
    }

    /// <summary>
    /// Dates are Russian text. The month names are mapped explicitly because this app runs with
    /// invariant globalization, where ru-RU collapses to the invariant culture and every one of
    /// these would fail to parse.
    /// </summary>
    [Fact]
    public void Russian_dates_are_read()
    {
        var message = Read("vk-simple").Messages
            .Single(m => m.Message.Plaintext.Contains("harbour", StringComparison.Ordinal));

        Assert.StartsWith("2020-01-01T12:34:56", message.Message.SentAtUtc, StringComparison.Ordinal);
    }

    /// <summary>May appears as both май and мая depending on the archive's vintage.</summary>
    [Fact]
    public void Both_spellings_of_may_are_understood()
    {
        var message = Read("vk-simple").Messages
            .Single(m => m.Message.Plaintext.Contains("Праге", StringComparison.Ordinal));

        Assert.StartsWith("2021-05-03T09:05:00", message.Message.SentAtUtc, StringComparison.Ordinal);
    }

    /// <summary>
    /// An edit marker sits between the name and the date, and would otherwise be read as part of
    /// it — turning a perfectly good date into a parse failure.
    /// </summary>
    [Fact]
    public void An_edit_marker_does_not_break_the_date()
    {
        var message = Read("vk-simple").Messages
            .Single(m => m.Message.Plaintext.Contains("поезд", StringComparison.Ordinal));

        Assert.StartsWith("2020-02-02T10:00:00", message.Message.SentAtUtc, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_are_recorded_as_described_but_absent()
    {
        var message = Read("vk-simple").Messages
            .Single(m => m.Message.Plaintext.Contains("Праге", StringComparison.Ordinal));

        var attachment = Assert.Single(message.Message.Media);

        Assert.NotNull(attachment.MissingReason);
        Assert.Contains("do not include the files", attachment.MissingReason!, StringComparison.Ordinal);
    }

    /// <summary>The attachment block must not end up inside the message's own words.</summary>
    [Fact]
    public void Attachment_markup_is_not_part_of_the_text()
    {
        var message = Read("vk-simple").Messages
            .Single(m => m.Message.Plaintext.Contains("Праге", StringComparison.Ordinal));

        Assert.DoesNotContain("Фотография", message.Message.Plaintext, StringComparison.Ordinal);
        Assert.Equal("мы были в Праге весной", message.Message.Plaintext);
    }

    [Fact]
    public void Negative_peer_ids_are_groups()
    {
        var group = Read("vk-simple").Threads.Single(t => t.SourceThreadId.StartsWith('-'));

        Assert.Equal("group", group.Kind);
    }

    [Fact]
    public void Uids_are_stable_across_reads() =>
        Assert.Equal(
            Read("vk-simple").Messages.Select(m => m.Message.Uid),
            Read("vk-simple").Messages.Select(m => m.Message.Uid));

    /// <summary>
    /// Without VK's own id there is no stable key, and generating one would make the import
    /// non-idempotent — the same archive would duplicate itself on every run.
    /// </summary>
    [Fact]
    public void A_message_without_an_id_stops_the_import()
    {
        var folder = VkExportBuilder.New()
            .Conversation("5", "Sam Ruiz", c => c
                .MessageWithNoId(new DateTimeOffset(2020, 1, 1, 12, 0, 0, TimeSpan.Zero), VkAuthor.You, "hello"))
            .Write(Fixtures.Temp("vk-no-id"));

        var error = Assert.Throws<InvalidDataException>(() => new VkImporter().Read(folder, new RecordingSink()));

        Assert.Contains("data-id", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A date this reader cannot parse stops the import rather than becoming 1970.</summary>
    [Fact]
    public void An_unreadable_date_stops_the_import()
    {
        var folder = VkExportBuilder.New()
            .Conversation("5", "Sam Ruiz", c => c
                .MessageWithHeader(1, "Вы, sometime last spring", "hello"))
            .Write(Fixtures.Temp("vk-bad-date"));

        Assert.Throws<InvalidDataException>(() => new VkImporter().Read(folder, new RecordingSink()));
    }
}
