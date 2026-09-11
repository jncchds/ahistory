using Archive.Import.GoogleChat;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Google Chat, as Takeout exports it. The traps are the date prose and the email-keyed people.
/// </summary>
public sealed class GoogleChatImporterTests
{
    private static RecordingSink Read(string folder)
    {
        var sink = new RecordingSink();
        new GoogleChatImporter().Read(folder, sink);

        return sink;
    }

    private static string Takeout(string name, GoogleChatDateStyle style = GoogleChatDateStyle.MonthFirst) =>
        Exports.GoogleChat(style).Write(Fixtures.Temp(name));

    [Fact]
    public void An_export_is_recognized_and_names_its_account()
    {
        var detection = new GoogleChatImporter().Detect(Takeout("gc-detect"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(2, detection.FileCount);
        Assert.Equal("owner@example.com", detection.AccountId);
        Assert.Equal("Owner Synthetic", detection.AccountName);
        Assert.False(detection.AccountIdIsGuess);
    }

    [Fact]
    public void A_telegram_export_is_not_mistaken_for_one()
    {
        Assert.Equal(
            ImportConfidence.None,
            new GoogleChatImporter().Detect(Exports.WriteGroupAndDm("gc-not-telegram")).Confidence);
    }

    [Fact]
    public void The_owner_is_stated_by_the_users_folder()
    {
        var owner = Read(Takeout("gc-owner")).Owner;

        Assert.NotNull(owner);
        Assert.Equal("owner@example.com", owner!.SourceIdentityId);
        Assert.False(owner.IsSynthetic);
    }

    /// <summary>
    /// Both of Takeout's English orders, and the narrow no-break space before PM, land on the same
    /// instant. Parsed through a culture instead, one of them silently swaps day and month.
    /// </summary>
    [Fact]
    public void Both_date_orders_read_as_the_same_instant()
    {
        var monthFirst = Read(Takeout("gc-us")).Messages.Select(m => m.Message.SentAtUnix);
        var dayFirst = Read(Takeout("gc-uk", GoogleChatDateStyle.DayFirst)).Messages.Select(m => m.Message.SentAtUnix);

        Assert.Equal([1615757463L, 1615757563L, 1615843863L], monthFirst);
        Assert.Equal(monthFirst, dayFirst);
    }

    [Theory]
    [InlineData("Tuesday, March 14, 2021 at 10:41:03 PM UTC", 2021, 3, 14, 22)]
    [InlineData("Tuesday, March 14, 2021 at 12:05:00 AM UTC", 2021, 3, 14, 0)]
    [InlineData("14 March 2021 at 22:41:03 UTC", 2021, 3, 14, 22)]
    public void Dates_are_read_exactly(string value, int year, int month, int day, int hour)
    {
        var at = GoogleChatImporter.ParseDate(value, "test");

        Assert.Equal(new DateTimeOffset(year, month, day, hour, at.Minute, at.Second, TimeSpan.Zero), at);
    }

    /// <summary>A date in another language is refused by name rather than guessed at.</summary>
    [Fact]
    public void A_date_in_another_language_stops_the_import()
    {
        var folder = GoogleChatExportBuilder.New(Exports.ChatOwner)
            .Dm("d1", [Exports.ChatOwner, Exports.ChatSam], g => g
                .MessageDated("d1/t/m", "Dienstag, 14. März 2021 um 22:41:03 UTC", Exports.ChatSam, "hallo"))
            .Write(Fixtures.Temp("gc-german"));

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("English", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_dm_is_named_after_the_other_person_and_a_space_by_its_name()
    {
        var threads = Read(Takeout("gc-titles")).Threads;

        var dm = threads.Single(t => t.Kind == "dm");
        var space = threads.Single(t => t.Kind == "group");

        Assert.Equal("Sam Ruiz", dm.Title);
        Assert.Equal("Prague trip", space.Title);
        Assert.Equal(3, space.Members.Count);
    }

    /// <summary>
    /// Addresses are the key, and Takeout is not consistent about their case. The same person
    /// written two ways must be one identity.
    /// </summary>
    [Fact]
    public void People_are_keyed_by_email_whatever_its_case()
    {
        var sink = Read(Takeout("gc-email"));

        var sam = sink.Messages[0].Message.Sender!;

        Assert.Equal("sam@example.com", sam.SourceIdentityId);
        Assert.Equal("Sam Ruiz", sam.DisplayName);

        var reactors = sink.Messages[2].Message.Reactions.Select(r => r.ActorIdentity!.DisplayName);

        Assert.Equal(["Owner Synthetic", "Sam Ruiz"], reactors);
    }

    [Fact]
    public void Message_ids_become_uids_and_quotes_become_replies()
    {
        var messages = Read(Takeout("gc-uids")).Messages.Select(m => m.Message).ToArray();

        Assert.Equal("gc/abc123/t1/m1", messages[0].Uid);
        Assert.Equal("gc/abc123/t1/m1", messages[1].ReplyToUid);
        Assert.NotNull(messages[2].EditedAtUtc);
    }

    /// <summary>
    /// An export too old to carry message ids still has to re-import onto the same rows, and two
    /// identical messages in the same second are still two messages.
    /// </summary>
    [Fact]
    public void Without_message_ids_uids_are_derived_and_stable()
    {
        string Write(string name) => GoogleChatExportBuilder.New(Exports.ChatOwner)
            .Dm("old", [Exports.ChatOwner, Exports.ChatSam], g => g
                .Message(null, At(1400000000), Exports.ChatSam, "ok")
                .Message(null, At(1400000000), Exports.ChatSam, "ok"))
            .Write(Fixtures.Temp(name));

        var first = Read(Write("gc-derived-1")).Messages.Select(m => m.Message.Uid).ToArray();
        var second = Read(Write("gc-derived-2")).Messages.Select(m => m.Message.Uid).ToArray();

        Assert.Equal(2, first.Distinct().Count());
        Assert.Equal(first, second);
    }

    [Fact]
    public void Attachments_are_found_beside_the_messages_and_stored()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.GoogleChat().Write(save.ExportFolder("gc")));

        Assert.Equal(3, stats.MessagesInserted);
        Assert.Equal(1, stats.MediaStored);
        Assert.Equal(0, stats.MediaNotFound);
        Assert.Equal("photo", save.Scalar<string>("SELECT media_kind FROM media;"));
    }

    /// <summary>A Takeout root holds Hangouts and Google Chat together, and both are found.</summary>
    [Fact]
    public void A_takeout_root_with_hangouts_beside_it_is_both()
    {
        var root = Fixtures.Temp("gc-takeout");
        Exports.GoogleChat().Write(root);
        Exports.Hangouts().Write(Path.Combine(root, "Hangouts"));

        var platforms = new ImporterRegistry().DetectAll(root).Select(m => m.Platform);

        Assert.Equal(["hangouts", "googlechat"], platforms);
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Exports.GoogleChat().Write(save.ExportFolder("gc"));

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }

    [Fact]
    public void The_owner_lands_as_the_archive_owner()
    {
        using var save = new TempSave();

        save.Import(Exports.GoogleChat().Write(save.ExportFolder("gc")));

        Assert.Equal("owner", save.Scalar<string>(
            "SELECT person_id FROM identity_person WHERE identity_id = 'googlechat:owner@example.com';"));
    }

    private static DateTimeOffset At(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix);
}
