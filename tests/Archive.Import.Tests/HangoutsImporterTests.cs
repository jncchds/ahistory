using Archive.Import.Hangouts;

namespace Archive.Import.Tests;

/// <summary>
/// Google Hangouts, as Takeout exported it. Every test here is a trap the format has and Telegram
/// does not.
/// </summary>
public sealed class HangoutsImporterTests
{
    private static string Fixture(string name) =>
        Path.Combine(Fixtures.Root, "hangouts", name);

    private static RecordingSink Read(string name)
    {
        var sink = new RecordingSink();
        new HangoutsImporter().Read(Fixture(name), sink);

        return sink;
    }

    [Fact]
    public void An_export_is_recognized_by_its_own_filename()
    {
        var detection = new HangoutsImporter().Detect(Fixture("simple"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(1, detection.FileCount);
    }

    [Fact]
    public void A_telegram_export_is_not_mistaken_for_one()
    {
        var detection = new HangoutsImporter().Detect(Fixtures.Directory("group-and-dm"));

        Assert.Equal(ImportConfidence.None, detection.Confidence);
    }

    [Fact]
    public void Conversations_and_their_messages_are_read()
    {
        var sink = Read("simple");

        Assert.Equal(2, sink.Threads.Count);
        Assert.Equal(4, sink.Messages.Count);
    }

    /// <summary>
    /// Timestamps are microseconds. Read as milliseconds — the obvious assumption — every message
    /// in the archive lands in 1970 in one indistinguishable heap.
    /// </summary>
    [Fact]
    public void Timestamps_are_microseconds_not_milliseconds()
    {
        var first = Read("simple").Messages[0].Message;

        Assert.Equal(1451651696, first.SentAtUnix);
        Assert.StartsWith("2016-01-01", first.SentAtUtc, StringComparison.Ordinal);
    }

    /// <summary>
    /// A LINE_BREAK segment carries no text, so concatenating only the text fields runs
    /// paragraphs together into one line.
    /// </summary>
    [Fact]
    public void Line_breaks_survive_the_segment_array()
    {
        var message = Read("simple").Messages.Single(m => m.Message.Uid.EndsWith("7-A-2", StringComparison.Ordinal));

        Assert.Equal("first line\nsecond line, see example.org", message.Message.Plaintext);
    }

    [Fact]
    public void Senders_are_resolved_to_names_from_the_participant_list()
    {
        var first = Read("simple").Messages[0].Message;

        Assert.Equal("222222222222222222222", first.Sender!.SourceIdentityId);
        Assert.Equal("Sam Ruiz", first.Sender.DisplayName);
    }

    /// <summary>
    /// Hangouts states the account nowhere, so it is inferred: the one participant in every
    /// conversation. Guessing wrong would put your own messages on the far side of all of them.
    /// </summary>
    [Fact]
    public void The_owner_is_inferred_from_being_in_every_conversation()
    {
        var sink = Read("simple");

        Assert.NotNull(sink.Owner);
        Assert.Equal("111111111111111111111", sink.Owner!.SourceIdentityId);
        Assert.Equal("Owner Person", sink.Owner.DisplayName);
    }

    /// <summary>
    /// Calls are events too. Dropping them would make §8's "four months, no contact" a lie in
    /// every conversation that was spoken rather than typed.
    /// </summary>
    [Fact]
    public void Calls_become_service_messages_rather_than_being_dropped()
    {
        var call = Read("simple").Messages.Single(m => m.Message.Kind == "service");

        Assert.Equal("call_started", call.Message.ServiceAction);
    }

    [Fact]
    public void A_direct_conversation_is_named_after_the_other_person()
    {
        var sink = Read("simple");

        var direct = sink.Threads.Single(t => t.Kind == "dm");
        var group = sink.Threads.Single(t => t.Kind == "group");

        Assert.NotNull(direct.Title);
        Assert.Equal("Prague trip", group.Title);
    }

    [Fact]
    public void Cyrillic_survives_the_round_trip()
    {
        var message = Read("simple").Messages.Single(m => m.Thread.Kind == "group");

        Assert.Equal("мы были в Праге весной", message.Message.Plaintext);
        Assert.Equal("Марина Коваль", message.Message.Sender!.DisplayName);
    }

    /// <summary>
    /// §1: uids must be stable, or a re-import duplicates the whole archive instead of
    /// recognizing it.
    /// </summary>
    [Fact]
    public void Uids_are_stable_across_reads()
    {
        Assert.Equal(
            Read("simple").Messages.Select(m => m.Message.Uid),
            Read("simple").Messages.Select(m => m.Message.Uid));
    }

    /// <summary>
    /// An event with no id has no stable key, and inventing one would make the import
    /// non-idempotent. It stops rather than guessing.
    /// </summary>
    [Fact]
    public void An_event_without_an_id_stops_the_import()
    {
        var folder = Fixtures.Temp("hangouts-no-id");

        File.WriteAllText(Path.Combine(folder, "Hangouts.json"), """
            { "conversations": [ {
              "conversation": { "conversation": { "id": { "id": "X" }, "type": "STICKY_ONE_TO_ONE",
                "participant_data": [ { "id": { "gaia_id": "1" } } ] } },
              "events": [ { "sender_id": { "gaia_id": "1" }, "timestamp": "1451651696123456",
                "chat_message": { "message_content": { "segment": [ { "type": "TEXT", "text": "x" } ] } } } ]
            } ] }
            """);

        var error = Assert.Throws<InvalidDataException>(
            () => new HangoutsImporter().Read(folder, new RecordingSink()));

        Assert.Contains("event_id", error.Message, StringComparison.Ordinal);
    }
}
