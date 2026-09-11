using Archive.Ai;
using Archive.Ai.Diary;
using Archive.Ui.ViewModels;

namespace Archive.Ui.Tests;

/// <summary>
/// The diary page: silences drawn, revisions shown as revisions, and absent while AI is off.
/// </summary>
public sealed class DiaryPageTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private static DiaryEntry Entry(int year, int month, string text, string language = "English") => new(
        Id: $"a{year}{month}{text.Length}",
        Scope: DiaryScope.Month,
        StartUnix: new DateTimeOffset(year, month, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds(),
        EndUnix: null,
        Sentences: [new DiarySentence(text, [42])],
        NothingToWrite: false,
        QuietMonthsBefore: null,
        CreatedUtc: "2026-01-01T00:00:00Z",
        Language: language,
        Model: "m");

    /// <summary>§8: "four months, no contact" is drawn, not papered over.</summary>
    [Fact]
    public void The_months_without_contact_are_drawn_between_entries()
    {
        var view = new DiaryView(
            Profile: null,
            Years: [],
            Months: [new DiaryEntryView(Entry(2023, 1, "They started a new job."), null, 1)],
            Activity:
            [
                new MonthActivity(2023, 1, 30, 9),
                new MonthActivity(2023, 6, 4, 2),
            ]);

        var blocks = DiaryViewModel.Build(view, "English", _ => { });

        var quiet = Assert.Single(blocks.OfType<DiaryQuietBlock>());
        Assert.Equal("4 months, no contact", quiet.Text);

        // Every month of contact is drawn — with its entry, or as a quiet line without one.
        var months = blocks.OfType<DiaryMonthBlock>().ToList();
        Assert.Equal(2, months.Count);
        Assert.True(months[0].HasEntry);
        Assert.False(months[1].HasEntry);
    }

    /// <summary>A rewritten month says so, and keeps what it said before.</summary>
    [Fact]
    public void A_rewritten_month_is_shown_as_revised_with_the_earlier_text_kept()
    {
        var view = new DiaryView(
            Profile: null,
            Years: [],
            Months: [new DiaryEntryView(Entry(2023, 1, "New text."), Entry(2023, 1, "Old text!"), 2)],
            Activity: [new MonthActivity(2023, 1, 30, 9)]);

        var month = DiaryViewModel.Build(view, "English", _ => { }).OfType<DiaryMonthBlock>().Single();

        Assert.True(month.IsRevised);
        Assert.Equal("Old text!", Assert.Single(month.Previous).Text);

        month.TogglePreviousCommand.Execute(null);

        Assert.True(month.ShowsPrevious);
    }

    /// <summary>§8: changing the language marks what was written before rather than rewriting a decade.</summary>
    [Fact]
    public void An_entry_in_another_language_says_so()
    {
        var view = new DiaryView(
            Profile: null,
            Years: [],
            Months: [new DiaryEntryView(Entry(2023, 1, "Они переехали.", "Russian"), null, 1)],
            Activity: [new MonthActivity(2023, 1, 30, 9)]);

        var month = DiaryViewModel.Build(view, "English", _ => { }).OfType<DiaryMonthBlock>().Single();

        Assert.Equal("written in Russian", month.Note);
    }

    /// <summary>Every sentence opens the message it rests on.</summary>
    [Fact]
    public void A_sentence_opens_its_source()
    {
        long? opened = null;

        var view = new DiaryView(
            Profile: null,
            Years: [],
            Months: [new DiaryEntryView(Entry(2023, 1, "They started a new job."), null, 1)],
            Activity: [new MonthActivity(2023, 1, 30, 9)]);

        var month = DiaryViewModel.Build(view, "English", id => opened = id).OfType<DiaryMonthBlock>().Single();

        month.Sentences[0].OpenCommand.Execute(null);

        Assert.Equal(42, opened);
    }

    /// <summary>P1: switched off, there is no diary in the rail.</summary>
    [Fact]
    public void With_ai_off_the_diary_is_not_in_the_rail()
    {
        using var save = new TempSave();

        var state = new AiState(new AiSettingsStore(_directory));
        var inputs = new DiaryInputs(save.Database);
        var page = new DiaryViewModel(save.Queries, save.Conversation, new DiaryStore(save.Database, inputs), inputs, state);

        Assert.False(page.IsAvailable);

        state.Update(new AiSettings
        {
            Enabled = true,
            MainModel = "m",
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        });

        Assert.True(page.IsAvailable);
    }

    /// <summary>P1: with AI off the Threads page offers no AI control at all.</summary>
    [Fact]
    public async Task With_ai_off_a_conversation_offers_no_ai_control()
    {
        using var save = new TempSave();
        save.Execute("""
            INSERT INTO import_source (id, platform, created_utc) VALUES ('src', 'telegram', '2026-01-01T00:00:00Z');
            INSERT INTO import (id, source_id, platform, source_path, source_fingerprint, importer_version, status, started_utc)
                VALUES ('imp', 'src', 'telegram', '/tmp', 'fp', '1', 'completed', '2026-01-01T00:00:00Z');
            INSERT INTO thread (id, platform, source_thread_id, kind, first_import_id, created_utc)
                VALUES ('t1', 'telegram', 't1', 'group', 'imp', '2026-01-01T00:00:00Z');
            INSERT INTO message (uid, thread_id, kind, sent_at_utc, sent_at_unix, plaintext, content_hash, first_import_id, importer_version)
                VALUES ('t1:0', 't1', 'message', '2023-01-01T00:00:00Z', 1672531200, 'hello', 'c', 'imp', '1');
            """);

        var state = new AiState(new AiSettingsStore(_directory));
        var page = new ThreadsViewModel(save.Queries, exclusions: new AiExclusions(save.Database), state: state);

        await page.RefreshAsync();

        Assert.False(page.CanExcludeThread);

        state.Update(new AiSettings
        {
            Enabled = true,
            MainModel = "m",
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        });

        await page.RefreshAsync();
        page.SelectedThread = null;
        page.SelectedThread = page.Threads[0];
        await page.RefreshAsync();

        Assert.True(page.CanExcludeThread);

        await page.ToggleThreadExclusionCommand.ExecuteAsync(null);

        Assert.True(page.IsThreadExcluded);
        Assert.True(new AiExclusions(save.Database).IsThreadExcluded("t1"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
