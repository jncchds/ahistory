using Archive.Ai;
using Archive.Ai.Extraction;
using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

/// <summary>
/// The panel beside a conversation: what is known, why it might be empty, and how to correct it.
/// </summary>
public sealed class FactsPanelTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private AiState Enabled()
    {
        var state = new AiState(new AiSettingsStore(_directory));

        state.Update(new AiSettings
        {
            Enabled = true,
            MainModel = "m",
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        });

        return state;
    }

    /// <summary>
    /// One person, one conversation, one session, one message — and optionally a model's reading of it.
    /// </summary>
    /// <remarks>
    /// In SQL rather than through an import and a model, because what is under test is what the
    /// panel does with rows that are already there.
    /// </remarks>
    private static void Seed(TempSave save, bool read = true, bool withFact = true)
    {
        save.Execute("""
            INSERT INTO import_source (id, platform, created_utc)
                VALUES ('src', 'telegram', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO import (id, source_id, platform, source_path, source_fingerprint,
                                importer_version, status, started_utc)
                VALUES ('imp', 'src', 'telegram', '/tmp', 'fp', '1', 'completed', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO person (id, display_name, is_owner, created_utc)
                VALUES ('p_sam', 'Sam', 0, '2026-01-01T00:00:00.0000000Z');
            INSERT INTO identity (id, platform, source_identity_id, display_name, first_import_id, created_utc)
                VALUES ('i_sam', 'telegram', 'sam', 'Sam', 'imp', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
                VALUES ('i_sam', 'p_sam', 'seed', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO thread (id, platform, source_thread_id, kind, first_import_id, created_utc)
                VALUES ('t1', 'telegram', 't1', 'dm', 'imp', '2026-01-01T00:00:00.0000000Z');
            INSERT INTO thread_participant (thread_id, identity_id, first_seen_unix)
                VALUES ('t1', 'i_sam', 0);
            INSERT INTO session (id, thread_id, started_at_unix, ended_at_unix, message_count,
                                 member_hash, segmenter_version, is_substantive, filter_version)
                VALUES ('s1', 't1', 1700000000, 1700000060, 1, 'h', '1', 1, '2');
            INSERT INTO message (id, uid, thread_id, sender_identity_id, kind, sent_at_utc, sent_at_unix,
                                 plaintext, content_hash, session_id, first_import_id, importer_version)
                VALUES (42, 't1:0', 't1', 'i_sam', 'message', '2023-11-14T22:13:20.0000000Z', 1700000000,
                        'I started at Acme last week', 'c', 's1', 'imp', '1');
            """);

        if (!read)
        {
            return;
        }

        save.Execute("""
            INSERT INTO derived_artifact (id, kind, source_session_id, engine, model, model_version,
                                          prompt_version, language, payload_json, input_hash, created_utc)
                VALUES ('a1', 'session_extract', 's1', 'llm', 'm', 'unknown', '1', 'English', '{}', 'h',
                        '2026-01-01T00:00:00.0000000Z');
            """);

        if (!withFact)
        {
            return;
        }

        save.Execute("""
            INSERT INTO fact (id, subject_person_id, predicate, object_text, claim_text, evidence_kind,
                              origin_kind, confidence, asserted_utc, derived_artifact_id, source)
                VALUES ('f1', 'p_sam', 'works_at', 'Acme', 'Sam works at Acme.', 'self_report', 'dm',
                        0.8, '2026-01-01T00:00:00.0000000Z', 'a1', 'extracted');
            INSERT INTO fact_citation (fact_id, message_id, role) VALUES ('f1', 42, 'asserts');
            """);
    }

    private FactsPanelViewModel Panel(TempSave save, AiState? state = null) =>
        new(new FactStore(save.Database), new AiCoverage(save.Database), state ?? Enabled());

    [Fact]
    public async Task What_is_known_about_a_person_is_shown_with_where_it_came_from()
    {
        using var save = new TempSave();
        Seed(save);

        var panel = Panel(save);

        await panel.ShowAsync("p_sam");

        var item = Assert.Single(panel.Facts);

        Assert.Equal("Sam works at Acme.", item.Claim);
        Assert.Contains("in their own words", item.Detail, StringComparison.Ordinal);
        Assert.True(item.CanOpen);
        Assert.False(panel.IsEmpty);
        Assert.Contains("1 of 1", panel.CoverageLine, StringComparison.Ordinal);
    }

    /// <summary>
    /// "Not read yet" and "read, and nothing in it" look the same unless the panel says which.
    /// </summary>
    /// <remarks>
    /// §7 of the plan: an empty panel for March 2019 means two different things, and the
    /// difference is the whole question a reader has — whether to wait, or to conclude there was
    /// nothing to find.
    /// </remarks>
    [Fact]
    public async Task An_empty_panel_says_whether_anything_has_been_read()
    {
        using (var unread = new TempSave())
        {
            Seed(unread, read: false);

            var panel = Panel(unread);
            await panel.ShowAsync("p_sam");

            Assert.True(panel.IsEmpty);
            Assert.Contains("Nothing has been read", panel.EmptyText, StringComparison.Ordinal);
        }

        using (var nothing = new TempSave())
        {
            Seed(nothing, withFact: false);

            var panel = Panel(nothing);
            await panel.ShowAsync("p_sam");

            Assert.True(panel.IsEmpty);
            Assert.Contains("arrangements", panel.EmptyText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_correction_made_in_the_panel_replaces_the_fact_and_says_so()
    {
        using var save = new TempSave();
        Seed(save);

        var panel = Panel(save);
        await panel.ShowAsync("p_sam");

        var item = panel.Facts[0];
        item.BeginEditCommand.Execute(null);
        item.EditObject = "Acme Robotics";
        item.EditClaim = "Sam works at Acme Robotics.";

        await item.SaveEditCommand.ExecuteAsync(null);

        var corrected = Assert.Single(panel.Facts);

        Assert.Equal("Sam works at Acme Robotics.", corrected.Claim);
        Assert.Contains("corrected by you", corrected.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_correction_is_refused_and_changes_nothing()
    {
        using var save = new TempSave();
        Seed(save);

        var panel = Panel(save);
        await panel.ShowAsync("p_sam");

        var item = panel.Facts[0];
        item.EditClaim = "   ";

        await item.SaveEditCommand.ExecuteAsync(null);

        Assert.NotNull(panel.Error);
        Assert.Equal("Sam works at Acme.", Assert.Single(panel.Facts).Claim);
    }

    [Fact]
    public async Task Removing_a_fact_takes_it_out_of_the_panel()
    {
        using var save = new TempSave();
        Seed(save);

        var panel = Panel(save);
        await panel.ShowAsync("p_sam");

        await panel.Facts[0].DeleteCommand.ExecuteAsync(null);

        Assert.Empty(panel.Facts);
    }

    /// <summary>Every fact is one click from the message it came from.</summary>
    [Fact]
    public async Task Opening_a_fact_asks_for_the_message_it_was_read_from()
    {
        using var save = new TempSave();
        Seed(save);

        var panel = Panel(save);
        await panel.ShowAsync("p_sam");

        long? asked = null;
        panel.RevealRequested += (_, messageId) => asked = messageId;

        panel.Facts[0].OpenCommand.Execute(null);

        Assert.Equal(42, asked);
    }

    /// <summary>P1: switched off, the panel is not there, and holds nothing.</summary>
    [Fact]
    public async Task With_ai_off_the_panel_is_absent_and_empty()
    {
        using var save = new TempSave();
        Seed(save);

        var state = new AiState(new AiSettingsStore(_directory));
        var panel = Panel(save, state);

        await panel.ShowAsync("p_sam");

        Assert.False(panel.IsAvailable);
        Assert.Empty(panel.Facts);
    }

    /// <summary>Choosing someone to read shows what is known about them, beside it.</summary>
    [Fact]
    public async Task Opening_a_conversation_shows_what_is_known_about_that_person()
    {
        using var save = new TempSave();
        Seed(save);

        var panel = Panel(save);
        var page = new PersonViewModel(save.Queries, save.Conversation, facts: panel);

        await page.RefreshAsync();
        await panel.Pending;

        Assert.Equal("p_sam", page.SelectedPerson?.Id);
        Assert.Single(panel.Facts);
    }

    /// <summary>Leaving someone out is one button, and the panel says what it means.</summary>
    [Fact]
    public async Task A_person_can_be_left_out_and_let_back_in_from_the_panel()
    {
        using var save = new TempSave();
        Seed(save);

        var panel = new FactsPanelViewModel(
            new FactStore(save.Database), new AiCoverage(save.Database), Enabled(), new AiExclusions(save.Database));

        await panel.ShowAsync("p_sam");

        Assert.True(panel.CanExclude);
        Assert.Equal("Leave out of AI reading", panel.ExclusionLabel);

        await panel.ToggleExclusionCommand.ExecuteAsync(null);

        Assert.True(panel.IsExcluded);
        Assert.Contains("Left out", panel.CoverageLine, StringComparison.Ordinal);
        Assert.True(new AiExclusions(save.Database).IsExcluded("p_sam"));

        await panel.ToggleExclusionCommand.ExecuteAsync(null);

        Assert.False(panel.IsExcluded);
        Assert.False(new AiExclusions(save.Database).IsExcluded("p_sam"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
