using Archive.Import.Vk;

namespace Archive.Import.Tests;

/// <summary>
/// VK's archive is HTML, and its traps are its own.
/// </summary>
public sealed class VkImporterTests
{
    private static string Fixture(string name) => Path.Combine(Fixtures.Root, "vk", name);

    private static RecordingSink Read(string name)
    {
        var sink = new RecordingSink();
        new VkImporter().Read(Fixture(name), sink);

        return sink;
    }

    [Fact]
    public void An_archive_is_recognized_by_its_messages_folder()
    {
        var detection = new VkImporter().Detect(Fixture("simple"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(2, detection.FileCount);
    }

    [Fact]
    public void A_telegram_export_is_not_mistaken_for_one()
    {
        Assert.Equal(
            ImportConfidence.None,
            new VkImporter().Detect(Fixtures.Directory("group-and-dm")).Confidence);
    }

    [Fact]
    public void Conversations_are_read_with_their_names()
    {
        var sink = Read("simple");

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
        var sink = Read("simple");

        var mine = sink.Messages.Single(m => m.Message.Plaintext.Contains("we should go back", StringComparison.Ordinal));
        var theirs = sink.Messages.Single(m => m.Message.Plaintext.Contains("harbour", StringComparison.Ordinal));

        Assert.Equal("self", mine.Message.Sender!.SourceIdentityId);
        Assert.Equal("222", theirs.Message.Sender!.SourceIdentityId);
        Assert.Equal("Sam Ruiz", theirs.Message.Sender.DisplayName);
    }

    /// <summary>
    /// Dates are Russian text. The month names are mapped explicitly because this app runs with
    /// invariant globalization, where ru-RU collapses to the invariant culture and every one of
    /// these would fail to parse.
    /// </summary>
    [Fact]
    public void Russian_dates_are_read()
    {
        var message = Read("simple").Messages
            .Single(m => m.Message.Plaintext.Contains("harbour", StringComparison.Ordinal));

        Assert.StartsWith("2020-01-01T12:34:56", message.Message.SentAtUtc, StringComparison.Ordinal);
    }

    /// <summary>May appears as both май and мая depending on the archive's vintage.</summary>
    [Fact]
    public void Both_spellings_of_may_are_understood()
    {
        var message = Read("simple").Messages
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
        var message = Read("simple").Messages
            .Single(m => m.Message.Plaintext.Contains("поезд", StringComparison.Ordinal));

        Assert.StartsWith("2020-02-02T10:00:00", message.Message.SentAtUtc, StringComparison.Ordinal);
    }

    [Fact]
    public void Attachments_are_recorded_as_described_but_absent()
    {
        var message = Read("simple").Messages
            .Single(m => m.Message.Plaintext.Contains("Праге", StringComparison.Ordinal));

        var attachment = Assert.Single(message.Message.Media);

        Assert.NotNull(attachment.MissingReason);
        Assert.Contains("do not include the files", attachment.MissingReason!, StringComparison.Ordinal);
    }

    /// <summary>The attachment block must not end up inside the message's own words.</summary>
    [Fact]
    public void Attachment_markup_is_not_part_of_the_text()
    {
        var message = Read("simple").Messages
            .Single(m => m.Message.Plaintext.Contains("Праге", StringComparison.Ordinal));

        Assert.DoesNotContain("Фотография", message.Message.Plaintext, StringComparison.Ordinal);
        Assert.Equal("мы были в Праге весной", message.Message.Plaintext);
    }

    [Fact]
    public void Negative_peer_ids_are_groups()
    {
        var group = Read("simple").Threads.Single(t => t.SourceThreadId.StartsWith('-'));

        Assert.Equal("group", group.Kind);
    }

    [Fact]
    public void Uids_are_stable_across_reads() =>
        Assert.Equal(
            Read("simple").Messages.Select(m => m.Message.Uid),
            Read("simple").Messages.Select(m => m.Message.Uid));

    /// <summary>
    /// Without VK's own id there is no stable key, and generating one would make the import
    /// non-idempotent — the same archive would duplicate itself on every run.
    /// </summary>
    [Fact]
    public void A_message_without_an_id_stops_the_import()
    {
        var folder = Fixtures.Temp("vk-no-id");
        var peer = Path.Combine(folder, "messages", "5");
        System.IO.Directory.CreateDirectory(peer);

        File.WriteAllText(Path.Combine(peer, "messages0.html"), """
            <html><body><div class="message">
              <div class="message__header">Вы, 1 янв 2020 в 12:00:00</div>hello
            </div></body></html>
            """);

        var error = Assert.Throws<InvalidDataException>(() => new VkImporter().Read(folder, new RecordingSink()));

        Assert.Contains("data-id", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A date this reader cannot parse stops the import rather than becoming 1970.</summary>
    [Fact]
    public void An_unreadable_date_stops_the_import()
    {
        var folder = Fixtures.Temp("vk-bad-date");
        var peer = Path.Combine(folder, "messages", "5");
        System.IO.Directory.CreateDirectory(peer);

        File.WriteAllText(Path.Combine(peer, "messages0.html"), """
            <html><body><div class="message" data-id="1">
              <div class="message__header">Вы, sometime last spring</div>hello
            </div></body></html>
            """);

        Assert.Throws<InvalidDataException>(() => new VkImporter().Read(folder, new RecordingSink()));
    }
}
