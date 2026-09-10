using Archive.Data;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Who the archive is about, and what happens when the export will not say.
/// </summary>
/// <remarks>
/// §1 rests on the owner being definite: everything §7 derives is oriented around the distinction
/// between them and everybody else. An owner the importer invented looks exactly like one the
/// export stated, and the archive that results looks right — which is the failure this project
/// cares about most.
/// </remarks>
public sealed class OwnerTests
{
    /// <summary>
    /// A direct conversation is never keyed by one of the owner's own accounts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the phantom self-chat stated as a rule, and it is deliberately not "a dm whose only
    /// participant is the owner" — a conversation where the other person never replied is exactly
    /// that, and is perfectly real. What makes the phantom a phantom is the *thread*: every
    /// platform here keys a direct thread by the person on the other side, so a direct thread keyed
    /// by you is a conversation with yourself wearing a stranger's clothes.
    /// </para>
    /// <para>
    /// Telegram's Saved Messages are keyed by the owner too, and pass, because they are stored as
    /// 'saved' (D6) — which is the whole point: a conversation with yourself is representable, and
    /// has to be told apart from a conversation with somebody.
    /// </para>
    /// </remarks>
    [Fact]
    public void No_direct_thread_is_keyed_by_one_of_the_owners_own_accounts()
    {
        using var save = new TempSave();

        save.Import("telegram", Exports.GroupAndDm());
        save.Import("saved", Exports.SavedMessages());
        save.Import(Exports.Hangouts().Write(save.ExportFolder("hangouts")));
        save.Import(Exports.Vk().Write(save.ExportFolder("vk")));
        save.Import(WriteQipWithSelfFile(save.ExportFolder("qip")));

        var offender = save.Scalar<string>("""
            SELECT t.id
            FROM thread t
            JOIN identity i ON i.platform = t.platform AND i.source_identity_id = t.source_thread_id
            JOIN identity_person ip ON ip.identity_id = i.id
            JOIN person p ON p.id = ip.person_id AND p.is_owner = 1
            WHERE t.kind = 'dm'
            LIMIT 1;
            """);

        Assert.Null(offender);
    }

    /// <summary>The QIP self file becomes one Saved Messages thread, not a second you.</summary>
    [Fact]
    public void A_qip_history_for_your_own_account_lands_as_saved_messages()
    {
        using var save = new TempSave();

        save.Import(WriteQipWithSelfFile(save.ExportFolder("qip-self")));

        Assert.Equal(
            QipImporterTitle,
            save.Scalar<string>("SELECT title FROM thread WHERE platform = 'qip' AND kind = 'saved';"));

        // One identity for the account, not one for the owner and one for the file's nickname.
        Assert.Equal(
            1,
            save.Scalar<long>(
                $"SELECT count(*) FROM identity WHERE platform = 'qip' AND source_identity_id = '{OwnUin}';"));

        Assert.Equal(
            1, save.Scalar<long>("SELECT count(*) FROM person WHERE is_owner = 1;"));
    }

    /// <summary>
    /// An owner the export stated is a seed; one the importer worked out is a guess.
    /// </summary>
    /// <remarks>
    /// 'seed' is what <c>IdentityMerger.Unmerge</c> refuses to detach, on the grounds that the
    /// export itself said so. Applied to a placeholder, that left a save permanently owned by an
    /// account nobody has, unfixable from the UI.
    /// </remarks>
    [Fact]
    public void A_stated_owner_is_a_seed_and_an_inferred_one_is_not()
    {
        using var save = new TempSave();

        save.Import("telegram", Exports.GroupAndDm());

        Assert.Equal(
            "seed",
            save.Scalar<string>(
                $"SELECT confidence FROM identity_person WHERE identity_id = 'telegram:{Exports.OwnerId}';"));

        // VK never names its account, so its owner identity is attached as a guess and is visible
        // as one — is_synthetic is what the merge UI's "identified by name only" filter reads.
        save.Import(Exports.Vk().Write(save.ExportFolder("vk")));

        var placeholder = save.Scalar<string>(
            "SELECT id FROM identity WHERE platform = 'vk' AND source_identity_id LIKE 'folder:%';")!;

        Assert.Equal(
            "auto",
            save.Scalar<string>($"SELECT confidence FROM identity_person WHERE identity_id = '{placeholder}';"));

        Assert.Equal(
            1, save.Scalar<long>($"SELECT is_synthetic FROM identity WHERE id = '{placeholder}';"));

        // And it can be taken back off the owner, which a seed cannot.
        new IdentityMerger(save.Database).Unmerge(placeholder);

        Assert.Equal(
            0,
            save.Scalar<long>($"""
                SELECT count(*) FROM identity_person ip
                JOIN person p ON p.id = ip.person_id
                WHERE ip.identity_id = '{placeholder}' AND p.is_owner = 1;
                """));
    }

    /// <summary>Both accounts still belong to one owner, which is what P5 promises.</summary>
    [Fact]
    public void Accounts_from_several_platforms_attach_to_the_one_owner()
    {
        using var save = new TempSave();

        save.Import("telegram", Exports.GroupAndDm());
        save.Import(Exports.Vk().Write(save.ExportFolder("vk")));
        save.Import(WriteQipWithSelfFile(save.ExportFolder("qip")));

        Assert.Equal(1, save.Scalar<long>("SELECT count(*) FROM person WHERE is_owner = 1;"));

        Assert.Equal(
            3,
            save.Scalar<long>("""
                SELECT count(*) FROM identity_person ip
                JOIN person p ON p.id = ip.person_id
                WHERE p.is_owner = 1;
                """));
    }

    /// <summary>
    /// The preview says the account is a guess, so the caller knows to ask.
    /// </summary>
    /// <remarks>
    /// The whole point of the flag: a Telegram export states its account and nothing should be
    /// asked, while a VK archive states nothing and inventing one is the bug.
    /// </remarks>
    [Fact]
    public void A_format_that_does_not_state_its_account_says_so_in_the_preview()
    {
        using var save = new TempSave();

        var telegram = save.Runner.Preview(save.Export("telegram", Exports.GroupAndDm()));

        Assert.False(telegram.DetectedAccountIsGuess);
        Assert.Equal(Exports.OwnerId.ToString(System.Globalization.CultureInfo.InvariantCulture), telegram.DetectedAccountId);

        var vk = save.Runner.Preview(Exports.Vk().Write(save.ExportFolder("vk")));

        Assert.True(vk.DetectedAccountIsGuess);
    }

    /// <summary>A QIP profile offers the UIN it found, as a candidate rather than a conclusion.</summary>
    [Fact]
    public void A_qip_profile_offers_the_uin_it_found_as_a_candidate()
    {
        using var save = new TempSave();

        var preview = save.Runner.Preview(WriteQipWithSelfFile(save.ExportFolder("qip")));

        Assert.True(preview.DetectedAccountIsGuess);
        Assert.Equal(OwnUin, preview.DetectedAccountId);
        Assert.Equal([OwnUin], preview.AccountCandidates);
    }

    /// <summary>
    /// The account the user names is the one the import uses.
    /// </summary>
    /// <remarks>
    /// This is the fix for the phantom in the case that produces it: a bare pile of .qhf files,
    /// where the folder says nothing and one of the files is the user's own account.
    /// </remarks>
    [Fact]
    public void The_account_the_user_names_becomes_the_owner()
    {
        using var save = new TempSave();

        var folder = save.ExportFolder("qip-bare");
        var at = new DateTimeOffset(2008, 5, 1, 12, 0, 0, TimeSpan.Zero);

        QipHistoryBuilder.Write(folder, ContactUin, "Марина",
            new QipMessage(1, "привет", Outgoing: false, at));
        QipHistoryBuilder.Write(folder, OwnUin, "Я",
            new QipMessage(1, "не забыть про билеты", Outgoing: true, at));

        save.Runner.Run(folder, ownerAccountId: OwnUin);

        Assert.Equal(
            $"qip:{OwnUin}",
            save.Scalar<string>(
                "SELECT ip.identity_id FROM identity_person ip JOIN person p ON p.id = ip.person_id "
                + "WHERE p.is_owner = 1 AND ip.identity_id LIKE 'qip:%';"));

        // The user's own file is Saved Messages, and nothing is a conversation with themselves.
        Assert.Equal("saved", save.Scalar<string>($"SELECT kind FROM thread WHERE source_thread_id = '{OwnUin}';"));
        Assert.Equal("dm", save.Scalar<string>($"SELECT kind FROM thread WHERE source_thread_id = '{ContactUin}';"));
    }

    private const string OwnUin = "12345678";
    private const string ContactUin = "87654321";
    private const string QipImporterTitle = "Saved messages";

    /// <summary>
    /// A QIP profile holding a contact's history and one for the owner's own account.
    /// </summary>
    /// <remarks>
    /// The second file is the one that used to produce a chat with yourself: QIP writes it for
    /// messages sent to your own UIN and for authorization traffic, and it names the owner exactly
    /// as it names any contact.
    /// </remarks>
    private static string WriteQipWithSelfFile(string folder)
    {
        var history = QipHistoryBuilder.HistoryFolder(folder, OwnUin);
        var at = new DateTimeOffset(2008, 5, 1, 12, 0, 0, TimeSpan.Zero);

        QipHistoryBuilder.Write(
            history, ContactUin, "Марина",
            new QipMessage(1, "привет", Outgoing: false, at),
            new QipMessage(2, "как дела", Outgoing: true, at.AddMinutes(1)));

        QipHistoryBuilder.Write(
            history, OwnUin, "Я",
            new QipMessage(1, "не забыть про билеты", Outgoing: true, at.AddHours(1)));

        return folder;
    }
}
