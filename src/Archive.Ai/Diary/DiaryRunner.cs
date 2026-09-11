using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Archive.Ai.Extraction;
using Archive.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ai.Diary;

/// <summary>How writing one diary text ended.</summary>
public enum DiaryOutcome
{
    Written,

    /// <summary>The model said there was nothing worth writing. Recorded, so it is not asked again.</summary>
    NothingToWrite,

    /// <summary>Nothing to write from: no facts in the window, or someone not to be written about.</summary>
    Skipped,

    /// <summary>The model could not produce a usable sentence. Visible, not swallowed.</summary>
    NeedsReview,
}

/// <summary>
/// Writes the diary: a month with someone, a year from its months, a person from their years (A5).
/// </summary>
/// <remarks>
/// <para>
/// All three are the same conversation with different material. Each is written by the main model —
/// synthesis is the half of the work the spec gives to the good model (§6.5) — and each lands as a
/// new row, so a regenerated month sits beside the one it replaced rather than over it.
/// </para>
/// <para>
/// Nothing is written until the conversation is over, in one statement; no transaction is open
/// while a call is in flight (P1).
/// </para>
/// </remarks>
public sealed class DiaryRunner(
    Database database,
    DiaryInputs inputs,
    DiaryStore store,
    AiClient client,
    ILogger<DiaryRunner>? logger = null)
{
    private const int MonthSentences = 8;
    private const int YearSentences = 8;
    private const int ProfileSentences = 10;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    private readonly DiaryInputs _inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));

    private readonly DiaryStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly AiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    private readonly ILogger _log = logger ?? NullLogger<DiaryRunner>.Instance;

    public Task<DiaryOutcome> MonthAsync(
        AiSettings settings, string personId, int year, int month, string inputHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var input = _inputs.Month(personId, year, month);

        return input is null
            ? Task.FromResult(DiaryOutcome.Skipped)
            : WriteAsync(
                settings,
                DiaryScope.Month,
                input.Person,
                input.Month.StartUnix,
                input.Month.EndUnix,
                PromptCatalog.DiaryWindow,
                DiaryMaterial.Month(input, settings.OutputLanguage),
                input.CitableIds,
                MonthSentences,
                input.QuietMonthsBefore,
                inputHash,
                cancellationToken);
    }

    public Task<DiaryOutcome> YearAsync(
        AiSettings settings, string personId, int year, string inputHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var input = _inputs.Year(personId, year, _store);

        if (input is null || input.Parts.Count == 0)
        {
            return Task.FromResult(DiaryOutcome.Skipped);
        }

        var start = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var end = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

        return WriteAsync(
            settings, DiaryScope.Year, input.Person, start, end, PromptCatalog.RollupYear,
            DiaryMaterial.Summary(input, settings.OutputLanguage), input.CitableIds, YearSentences,
            quietMonthsBefore: null, inputHash, cancellationToken);
    }

    public Task<DiaryOutcome> ProfileAsync(
        AiSettings settings, string personId, string inputHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var input = _inputs.Profile(personId, _store);

        if (input is null || (input.Parts.Count == 0 && input.Facts.Count == 0))
        {
            return Task.FromResult(DiaryOutcome.Skipped);
        }

        return WriteAsync(
            settings, DiaryScope.Profile, input.Person, null, null, PromptCatalog.RollupProfile,
            DiaryMaterial.Summary(input, settings.OutputLanguage), input.CitableIds, ProfileSentences,
            quietMonthsBefore: null, inputHash, cancellationToken);
    }

    private async Task<DiaryOutcome> WriteAsync(
        AiSettings settings,
        DiaryScope scope,
        DiaryPerson person,
        long? start,
        long? end,
        string prompt,
        string material,
        HashSet<long> citable,
        int maxSentences,
        int? quietMonthsBefore,
        string inputHash,
        CancellationToken cancellationToken)
    {
        var prose = new ProseDispatcher(citable, maxSentences);
        var model = settings.ModelFor(AiWorkKind.Main);

        var (calledAnything, modelVersion) = await ToolConversation.RunAsync(
            _client,
            settings,
            model,
            PromptCatalog.TextOf(prompt),
            material,
            ProseTools.All,
            prose.Dispatch,
            () => prose.NothingToWrite,
            scope == DiaryScope.Month ? AiPurpose.Diary : AiPurpose.Rollup,
            AiSubject.Person(person.Id),
            cancellationToken).ConfigureAwait(false);

        if (!calledAnything || (prose.Sentences.Count == 0 && !prose.NothingToWrite))
        {
            _log.LogWarning("A {Scope} entry for {PersonId} produced no usable sentence.", scope, person.Id);

            return DiaryOutcome.NeedsReview;
        }

        Write(scope, person.Id, start, end, prose, quietMonthsBefore, model, modelVersion,
            PromptCatalog.VersionOf(prompt), settings.OutputLanguage, inputHash);

        return prose.Sentences.Count == 0 ? DiaryOutcome.NothingToWrite : DiaryOutcome.Written;
    }

    /// <summary>
    /// One row, one statement. A new text is a new row; the one it replaces stays, and is shown as
    /// what it was replaced from.
    /// </summary>
    private void Write(
        DiaryScope scope,
        string personId,
        long? start,
        long? end,
        ProseDispatcher prose,
        int? quietMonthsBefore,
        string model,
        string modelVersion,
        string promptVersion,
        string language,
        string inputHash)
    {
        var id = "a_" + Hash($"{scope}|{personId}|{start}|{promptVersion}|{model}|{inputHash}")[..24];

        var payload = JsonSerializer.Serialize(new
        {
            scope = scope.ToString().ToLowerInvariant(),
            sentences = prose.Sentences.Select(s => new { text = s.Text, message_ids = s.MessageIds }),
            nothing = prose.NothingToWrite && prose.Sentences.Count == 0,
            quiet_months_before = quietMonthsBefore,
        });

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO derived_artifact (
                id, kind, source_person_id, engine, model, model_version, prompt_version, language,
                payload_json, input_hash, created_utc, window_start_unix, window_end_unix)
            VALUES ($id, $kind, $person, 'llm', $model, $modelVersion, $prompt, $language,
                    $payload, $hash, $now, $start, $end)
            ON CONFLICT (id) DO NOTHING;
            """;

        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", scope == DiaryScope.Month ? "diary" : "rollup");
        command.Parameters.AddWithValue("$person", personId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$modelVersion", modelVersion);
        command.Parameters.AddWithValue("$prompt", promptVersion);
        command.Parameters.AddWithValue("$language", language);
        command.Parameters.AddWithValue("$payload", payload);
        command.Parameters.AddWithValue("$hash", inputHash);
        command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$start", (object?)start ?? DBNull.Value);
        command.Parameters.AddWithValue("$end", (object?)end ?? DBNull.Value);

        command.ExecuteNonQuery();
    }

    private static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

/// <summary>The material each kind of diary text is written from, as the model reads it.</summary>
internal static class DiaryMaterial
{
    public static string Month(MonthInput input, string language)
    {
        var text = new StringBuilder();

        Header(text, input.Person, language);

        text.AppendLine(CultureInfo.InvariantCulture,
            $"Month: {input.Month.Start.ToString("MMMM yyyy", CultureInfo.InvariantCulture)}");

        // Silence is given, not left to be noticed (§8).
        text.AppendLine(input.QuietMonthsBefore switch
        {
            null => "This is the first month they appear in the archive.",
            0 => "They were in touch the month before as well.",
            1 => "Before this month: one month without a single message.",
            var n => $"Before this month: {n} months without a single message.",
        });

        text.AppendLine(CultureInfo.InvariantCulture,
            $"This month: {input.Month.Messages} message(s), on {input.Month.Days} day(s).");
        text.AppendLine();
        text.AppendLine("What was learned this month, with the message that says it:");

        foreach (var fact in input.Facts)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  [{fact.MessageId}] {fact.Claim}");
        }

        if (input.Messages.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("From their conversations this month (excerpts):");
            text.AppendLine();

            foreach (var message in input.Messages)
            {
                var when = DateTimeOffset.FromUnixTimeSeconds(message.SentAtUnix)
                    .UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);

                text.AppendLine(CultureInfo.InvariantCulture, $"[{message.Id}] {when} {message.SenderName}: {message.Text}");
            }
        }

        return text.ToString();
    }

    public static string Summary(SummaryInput input, string language)
    {
        var text = new StringBuilder();

        Header(text, input.Person, language);

        if (input.Year is { } year)
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Year: {year}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Months with any contact: {input.ActiveMonths} of 12.");
        }
        else
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"Months with any contact, ever: {input.ActiveMonths}.");
        }

        foreach (var (label, sentences) in input.Parts)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"{label}:");

            foreach (var sentence in sentences)
            {
                text.AppendLine(CultureInfo.InvariantCulture,
                    $"  [{string.Join(", ", sentence.MessageIds)}] {sentence.Text}");
            }
        }

        if (input.Facts.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("What is known about them, with the message that says it:");

            foreach (var fact in input.Facts)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  [{fact.MessageId}] {fact.Claim}");
            }
        }

        return text.ToString();
    }

    private static void Header(StringBuilder text, DiaryPerson person, string language)
    {
        text.AppendLine(CultureInfo.InvariantCulture, $"Write in: {language}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Person: {person.Name}");

        if (person.IsOwner)
        {
            text.AppendLine("This person is the archive's owner: write about them as \"you\".");
        }

        text.AppendLine();
    }
}
