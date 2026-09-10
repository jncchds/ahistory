using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// Who was in a conversation, as opposed to who spoke in it.
/// </summary>
/// <remarks>
/// Participants used to come from senders alone, which answers the second question and quietly
/// discards the first — along with the roster Hangouts states outright. A group of eleven where
/// three people ever typed was a group of three, and a direct thread you wrote into and got no
/// reply from had nobody in it at all.
/// </remarks>
public sealed class ThreadParticipantTests
{
    private static long Participants(TempSave save, string threadId) =>
        save.Scalar<long>($"SELECT count(*) FROM thread_participant WHERE thread_id = '{threadId}';");

    /// <summary>A stated member who never typed is still in the room.</summary>
    [Fact]
    public void A_hangouts_group_member_who_never_spoke_is_a_participant()
    {
        using var save = new TempSave();

        save.Import(Exports.Hangouts().Write(save.ExportFolder("hangouts")));

        // Three in the roster; one of them ever sent anything.
        Assert.Equal(3, Participants(save, "hangouts:UgxGROUP9"));

        Assert.Equal(
            1,
            save.Scalar<long>(
                "SELECT count(DISTINCT sender_identity_id) FROM message WHERE thread_id = 'hangouts:UgxGROUP9';"));
    }

    /// <summary>
    /// You are in your own conversations even when you did not say anything in one.
    /// </summary>
    /// <remarks>
    /// §4's per-person view finds a person's direct threads through <c>thread_participant</c>. An
    /// unanswered conversation with no participants at all is one that never appears in it.
    /// </remarks>
    [Fact]
    public void The_owner_is_a_participant_of_a_direct_thread_they_never_wrote_in()
    {
        using var save = new TempSave();

        var export = TelegramExportBuilder.Full()
            .Owner(Exports.OwnerId, "Owner", "Synthetic")
            .Chat("Sam Ruiz", "personal_chat", 100, c => c
                .Message(1, DateTimeOffset.FromUnixTimeSeconds(1554221523), Exports.Sam, "anyone there?"))
            .Write(save.ExportFolder("one-sided"));

        save.Import(export);

        Assert.Equal(2, Participants(save, "telegram:100"));

        Assert.Equal(
            1,
            save.Scalar<long>($"""
                SELECT count(*) FROM thread_participant
                WHERE thread_id = 'telegram:100' AND identity_id = 'telegram:{Exports.OwnerId}';
                """));
    }

    /// <summary>A group is not padded with the owner: being in one is something the export says.</summary>
    [Fact]
    public void The_owner_is_not_added_to_a_group_they_are_not_stated_in()
    {
        using var save = new TempSave();

        save.Import(Exports.Hangouts().Write(save.ExportFolder("hangouts")));

        // The Hangouts group does state the owner, so use a Telegram group, which states nobody:
        // its participants are exactly the senders.
        save.Import("telegram", Exports.GroupAndDm());

        Assert.Equal(1, Participants(save, "telegram:300"));
    }

    /// <summary>
    /// P3: a roster must not make re-import write rows.
    /// </summary>
    /// <remarks>
    /// The obvious mistake is to insert participants once per message rather than once per thread,
    /// which costs nothing on a fixture and is half a million writes on a real archive.
    /// </remarks>
    [Fact]
    public void Re_importing_writes_no_new_participants()
    {
        using var save = new TempSave();

        var folder = Exports.Hangouts().Write(save.ExportFolder("hangouts"));

        save.Import(folder);
        var before = save.Digest();

        save.Import(folder);

        Assert.Equal(before, save.Digest());
    }

    /// <summary>
    /// A direct conversation is named after the other person, never after you.
    /// </summary>
    /// <remarks>
    /// Hangouts gives direct conversations no name, so one is made from the participant list. The
    /// first non-blank name in it is yours whenever the export happens to list you first, which
    /// filled the thread list with conversations that appear to be with yourself.
    /// </remarks>
    [Fact]
    public void A_direct_conversation_is_named_after_the_other_person()
    {
        using var save = new TempSave();

        save.Import(Exports.Hangouts().Write(save.ExportFolder("hangouts")));

        Assert.Equal(
            "Sam Ruiz",
            save.Scalar<string>("SELECT title FROM thread WHERE id = 'hangouts:UgxABC123';"));
    }
}
