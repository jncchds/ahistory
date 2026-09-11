using Archive.Import.Sms;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// SMS Backup &amp; Restore: phone numbers as identities, emoji as surrogate entities, pictures as
/// base64, and a backup that never says whose phone it was.
/// </summary>
public sealed class SmsImporterTests
{
    private static RecordingSink Read(string folder, ImportOptions? options = null)
    {
        var sink = new RecordingSink();
        new SmsBackupImporter().Read(folder, sink, options);

        return sink;
    }

    private static string Backup(string name, SmsBackupBuilder? backup = null) =>
        (backup ?? Exports.Sms()).Write(Fixtures.Temp(name));

    [Fact]
    public void A_backup_is_recognized_and_asks_whose_it_is()
    {
        var detection = new SmsBackupImporter().Detect(Backup("sms-detect"));

        Assert.Equal(ImportConfidence.Certain, detection.Confidence);
        Assert.Equal(1, detection.FileCount);
        Assert.True(detection.AccountIdIsGuess);
    }

    [Fact]
    public void A_call_log_alone_is_not_a_backup()
    {
        var folder = Fixtures.Temp("sms-calls");
        File.WriteAllText(Path.Combine(folder, "calls-20210314.xml"), "<?xml version='1.0' ?><calls count=\"0\"></calls>");

        Assert.Equal(ImportConfidence.None, new SmsBackupImporter().Detect(folder).Confidence);
    }

    [Fact]
    public void A_telegram_export_is_not_mistaken_for_one() =>
        Assert.Equal(
            ImportConfidence.None,
            new SmsBackupImporter().Detect(Exports.WriteGroupAndDm("sms-not-telegram")).Confidence);

    /// <summary>A sent picture message names its own sender, which is the owner's number.</summary>
    [Fact]
    public void The_owner_number_is_read_from_a_sent_mms()
    {
        var owner = Read(Backup("sms-owner")).Owner!;

        Assert.Equal(Exports.OwnNumber, owner.SourceIdentityId);
        Assert.False(owner.IsSynthetic);
    }

    [Fact]
    public void Without_an_mms_or_an_answer_the_owner_is_a_placeholder()
    {
        var folder = Backup("sms-placeholder", SmsBackupBuilder.New()
            .Sms(Exports.SamNumber, DateTimeOffset.FromUnixTimeSeconds(1615757463), 1, "hi"));

        Assert.True(Read(folder).Owner!.IsSynthetic);

        var told = Read(folder, new ImportOptions("+1 555 000 0000")).Owner!;

        Assert.Equal(Exports.OwnNumber, told.SourceIdentityId);
        Assert.False(told.IsSynthetic);
    }

    /// <summary>
    /// The same number written two ways is one person. Only formatting is removed — no country
    /// code is ever invented.
    /// </summary>
    [Theory]
    [InlineData("+1 (555) 123-4567", "+15551234567")]
    [InlineData("555.123.4567", "5551234567")]
    [InlineData("AMAZON", "AMAZON")]
    public void Numbers_lose_their_formatting_and_nothing_else(string written, string expected) =>
        Assert.Equal(expected, SmsBackupImporter.Normalize(written));

    [Fact]
    public void A_formatted_number_lands_in_the_same_conversation()
    {
        var sink = Read(Backup("sms-formatted"));

        var dm = sink.Messages.Where(m => m.Thread.Kind == "dm").Select(m => m.Thread.SourceThreadId).Distinct();

        Assert.Equal([$"dm/{Exports.SamNumber}"], dm);
    }

    /// <summary>Emoji are two surrogate references, which a strict XML parser refuses outright.</summary>
    [Fact]
    public void Emoji_written_as_surrogate_references_survive()
    {
        var sink = Read(Backup("sms-emoji"));

        Assert.Contains(sink.Messages, m => m.Message.Plaintext == "we should go back 🌊");
        Assert.Contains(sink.Messages, m => m.Message.Plaintext == "мы были в Праге весной");
    }

    [Fact]
    public void Drafts_are_not_imported() =>
        Assert.DoesNotContain(Read(Backup("sms-draft")).Messages, m => m.Message.Plaintext == "never sent");

    [Fact]
    public void Sent_texts_are_the_owners_and_received_ones_the_contacts()
    {
        var messages = Read(Backup("sms-sides")).Messages.Select(m => m.Message).ToArray();

        Assert.Equal("Sam Ruiz", messages[0].Sender!.DisplayName);
        Assert.Equal(Exports.OwnNumber, messages[1].Sender!.SourceIdentityId);
    }

    [Fact]
    public void A_group_picture_message_is_a_group_with_everyone_named()
    {
        var group = Read(Backup("sms-group")).Messages.Single(m => m.Thread.Kind == "group");

        Assert.Equal($"group/{Exports.SamNumber},{Exports.AlexNumber}", group.Thread.SourceThreadId);
        Assert.Equal(["Sam Ruiz", "Alex Novak"], group.Thread.Members.Select(p => p.DisplayName));
        Assert.Equal("mms/mms-0001", group.Message.Uid);
        Assert.Equal("look", group.Message.Plaintext);
    }

    [Fact]
    public void Pictures_inside_the_xml_are_stored()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.Sms().Write(save.ExportFolder("sms")));

        Assert.Equal(4, stats.MessagesInserted);
        Assert.Equal(1, stats.MediaStored);
        Assert.Equal("photo", save.Scalar<string>("SELECT media_kind FROM media;"));
        Assert.Equal(".jpg", save.Scalar<string>("SELECT extension FROM media;"));
    }

    [Fact]
    public void A_backup_made_without_pictures_records_them_as_missing()
    {
        using var save = new TempSave();

        var stats = save.Import(Exports.Sms(withPicture: false).Write(save.ExportFolder("sms")));

        Assert.Equal(0, stats.MediaStored);
        Assert.Equal(1, stats.MediaMissing);
        Assert.Equal(
            "the backup was made without MMS attachments",
            save.Scalar<string>("SELECT missing_reason FROM message_media;"));
    }

    /// <summary>Two backups that overlap are read together, and what they share is stored once.</summary>
    [Fact]
    public void Overlapping_backups_store_each_message_once()
    {
        using var save = new TempSave();
        var folder = save.ExportFolder("sms");

        var at = DateTimeOffset.FromUnixTimeSeconds(1615757463);

        SmsBackupBuilder.New()
            .Sms(Exports.SamNumber, at, 1, "one")
            .Sms(Exports.SamNumber, at.AddSeconds(1), 1, "ok")
            .Write(folder, "sms-1.xml");

        SmsBackupBuilder.New()
            .Sms(Exports.SamNumber, at, 1, "one")
            .Sms(Exports.SamNumber, at.AddSeconds(1), 1, "ok")
            .Sms(Exports.SamNumber, at.AddSeconds(2), 1, "three")
            .Write(folder, "sms-2.xml");

        Assert.Equal(3, save.Import(folder).MessagesInserted);
    }

    [Fact]
    public void Two_identical_texts_in_one_backup_stay_two()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1615757463);

        var folder = Backup("sms-repeat", SmsBackupBuilder.New()
            .Sms(Exports.SamNumber, at, 1, "ok")
            .Sms(Exports.SamNumber, at, 1, "ok"));

        Assert.Equal(2, Read(folder).Messages.Select(m => m.Message.Uid).Distinct().Count());
    }

    [Fact]
    public void An_sms_type_nobody_knows_stops_the_import()
    {
        var folder = Backup("sms-unknown", SmsBackupBuilder.New()
            .Sms(Exports.SamNumber, DateTimeOffset.FromUnixTimeSeconds(1615757463), 9, "?"));

        var error = Assert.Throws<InvalidDataException>(() => Read(folder));

        Assert.Contains("type 9", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Re_importing_changes_nothing()
    {
        using var save = new TempSave();
        var folder = Exports.Sms().Write(save.ExportFolder("sms"));

        save.Import(folder);
        var before = save.Digest();

        var second = save.Import(folder);

        Assert.Equal(before, save.Digest());
        Assert.Equal(0, second.MessagesInserted);
    }
}
