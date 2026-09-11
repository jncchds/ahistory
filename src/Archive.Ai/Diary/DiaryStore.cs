using System.Globalization;
using System.Text.Json;
using Archive.Data;

namespace Archive.Ai.Diary;

/// <summary>What a diary text covers.</summary>
public enum DiaryScope
{
    /// <summary>One calendar month with one person (spec §8's fixed window).</summary>
    Month,

    /// <summary>A year, summarised from its months — never from raw messages (§6.4).</summary>
    Year,

    /// <summary>The person as a whole, from the years and what is known about them.</summary>
    Profile,
}

/// <summary>One sentence and the messages it rests on.</summary>
/// <remarks>
/// Per sentence rather than per entry, because §8's rule is per sentence: anything emitted without
/// a citation is dropped rather than shown, and "the entry cites these twelve messages" does not
/// say which sentence came from which.
/// </remarks>
public sealed record DiarySentence(string Text, IReadOnlyList<long> MessageIds);

/// <summary>One written diary text, as stored.</summary>
public sealed record DiaryEntry(
    string Id,
    DiaryScope Scope,
    long? StartUnix,
    long? EndUnix,
    IReadOnlyList<DiarySentence> Sentences,
    bool NothingToWrite,
    int? QuietMonthsBefore,
    string CreatedUtc,
    string? Language,
    string Model);

/// <summary>The current text for one window, and whether it has been rewritten.</summary>
/// <param name="Previous">The text it replaced, when there is one — revisions are shown as revisions.</param>
public sealed record DiaryEntryView(DiaryEntry Latest, DiaryEntry? Previous, int Revisions);

/// <summary>A person's diary, as a page shows it.</summary>
/// <param name="Activity">Every month they were in touch, for drawing the silences between entries.</param>
public sealed record DiaryView(
    DiaryEntryView? Profile,
    IReadOnlyList<DiaryEntryView> Years,
    IReadOnlyList<DiaryEntryView> Months,
    IReadOnlyList<MonthActivity> Activity)
{
    public static DiaryView Empty { get; } = new(null, [], [], []);

    public bool IsEmpty => Profile is null && Years.Count == 0 && Months.Count == 0;
}

/// <summary>
/// Reads the diary back: entries, their revisions, and the months in between.
/// </summary>
/// <remarks>
/// <para>
/// Every regeneration is a new row, never an overwrite (spec §3, §8). What is shown is the newest
/// text for each window, and the one before it is kept one click away — "a diary that silently
/// rewrites your past every time you open it is unsettling", and one that says it was rewritten,
/// and how, is not.
/// </para>
/// <para>
/// Nothing here calls a model; it is a read over rows, and it works on a save opened with AI
/// switched off just as well — which is how a page can decide to show nothing at all.
/// </para>
/// </remarks>
public sealed class DiaryStore(Database database, DiaryInputs inputs)
{
    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    private readonly DiaryInputs _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));

    /// <summary>Every diary text written about a person, oldest window first, newest text first within it.</summary>
    public IReadOnlyList<DiaryEntry> Entries(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT id, kind, window_start_unix, window_end_unix, payload_json, created_utc, language, model
            FROM derived_artifact
            WHERE source_person_id = $person AND kind IN ('diary', 'rollup')
            ORDER BY coalesce(window_start_unix, -1), kind, created_utc DESC, rowid DESC;
            """;

        command.Parameters.AddWithValue("$person", personId);

        var entries = new List<DiaryEntry>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var start = reader.IsDBNull(2) ? (long?)null : reader.GetInt64(2);

            var scope = reader.GetString(1) == "diary"
                ? DiaryScope.Month
                : start is null ? DiaryScope.Profile : DiaryScope.Year;

            var (sentences, nothing, quiet) = ReadPayload(reader.GetString(4));

            entries.Add(new DiaryEntry(
                Id: reader.GetString(0),
                Scope: scope,
                StartUnix: start,
                EndUnix: reader.IsDBNull(3) ? null : reader.GetInt64(3),
                Sentences: sentences,
                NothingToWrite: nothing,
                QuietMonthsBefore: quiet,
                CreatedUtc: reader.GetString(5),
                Language: reader.IsDBNull(6) ? null : reader.GetString(6),
                Model: reader.GetString(7)));
        }

        return entries;
    }

    /// <summary>The newest text for each window, with the one it replaced.</summary>
    public IReadOnlyList<DiaryEntryView> Latest(string personId, DiaryScope scope) =>
        Newest(Entries(personId), scope);

    /// <summary>Everything the diary page shows for one person.</summary>
    public DiaryView ForPerson(string personId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(personId);

        var entries = Entries(personId);

        return new DiaryView(
            Profile: Newest(entries, DiaryScope.Profile).FirstOrDefault(),
            Years: Newest(entries, DiaryScope.Year),
            Months: Newest(entries, DiaryScope.Month),
            Activity: _inputs.Months(personId));
    }

    /// <summary>Groups texts by window; the first of each group is the newest, the second what it replaced.</summary>
    private static List<DiaryEntryView> Newest(IReadOnlyList<DiaryEntry> entries, DiaryScope scope) =>
    [
        .. entries
            .Where(e => e.Scope == scope)
            .GroupBy(e => e.StartUnix)
            .Select(group =>
            {
                var texts = group.ToList();

                return new DiaryEntryView(texts[0], texts.Count > 1 ? texts[1] : null, texts.Count);
            }),
    ];

    /// <summary>How many month entries exist — for the forget confirmation and the activity page.</summary>
    public long Count()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM derived_artifact WHERE kind IN ('diary', 'rollup');";

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static (IReadOnlyList<DiarySentence> Sentences, bool Nothing, int? QuietMonthsBefore) ReadPayload(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        var sentences = new List<DiarySentence>();

        if (root.TryGetProperty("sentences", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var text = item.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;

                var ids = item.TryGetProperty("message_ids", out var m) && m.ValueKind == JsonValueKind.Array
                    ? m.EnumerateArray().Select(id => id.GetInt64()).ToArray()
                    : [];

                sentences.Add(new DiarySentence(text, ids));
            }
        }

        var nothing = root.TryGetProperty("nothing", out var n) && n.ValueKind == JsonValueKind.True;

        var quiet = root.TryGetProperty("quiet_months_before", out var q) && q.ValueKind == JsonValueKind.Number
            ? q.GetInt32()
            : (int?)null;

        return (sentences, nothing, quiet);
    }
}
