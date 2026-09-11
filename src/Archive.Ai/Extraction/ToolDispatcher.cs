using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Archive.Ai.Extraction;

/// <summary>One message a fact was found in, and how it bears on it.</summary>
public sealed record StagedCitation(long MessageId, string Role, string? Quote);

/// <summary>A fact about to be written, if the run finishes.</summary>
public sealed record StagedFact
{
    public required string Predicate { get; init; }

    public required string ObjectText { get; init; }

    public required string ClaimText { get; init; }

    public required double Confidence { get; init; }

    /// <summary>Exactly one of these two is set (§7: relational facts live on the edge).</summary>
    public string? SubjectPersonId { get; init; }

    public (string A, string B)? SubjectPair { get; init; }

    /// <summary>Derived by the runner from who sent the cited message, never asked of the model.</summary>
    public required string EvidenceKind { get; init; }

    /// <summary>Likewise, from the thread.</summary>
    public required string OriginKind { get; init; }

    public string? ValidFromUtc { get; init; }

    public string? ValidToUtc { get; init; }

    /// <summary>Set when this replaces an expired fact rather than adding a new one.</summary>
    public string? SupersedesFactId { get; init; }

    public required IReadOnlyList<StagedCitation> Citations { get; init; }
}

/// <summary>Citations added to a fact that already exists.</summary>
public sealed record StagedAnnotation(
    string FactId, IReadOnlyList<StagedCitation> Citations, string? Note);

/// <summary>What a tool call became. The message goes back to the model verbatim.</summary>
public sealed record ToolReply(bool Accepted, string Message);

/// <summary>
/// Turns tool calls into things that may be written, and refuses the rest.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here touches the database. Calls accumulate, and the whole run is committed at the end
/// in one short transaction — a model call is never inside one (P1), and a run that is cancelled
/// half way leaves no half-written fact behind it.
/// </para>
/// <para>
/// What is checkable is mechanical: that the cited ids exist and sit inside this window, that the
/// subject is somebody here, that the numbers are in range. Whether a sentence <i>follows</i> from
/// those messages is not, and pretending otherwise would be worse than admitting it — the citation
/// rule catches the lazy failure, and the confident one is caught by a person reading the panel.
/// </para>
/// </remarks>
public sealed partial class ToolDispatcher(ExtractionWindow window)
{
    /// <summary>Length caps, so one bad call cannot write a page into a claim.</summary>
    private const int MaxClaim = 400;
    private const int MaxObject = 200;
    private const int MaxPredicate = 60;

    /// <summary>
    /// How far into the future a validity date may reach.
    /// </summary>
    /// <remarks>
    /// "moving in June" is a real fact with a future start, so future dates are allowed — but a
    /// model that misreads a year produces 2190, and a diary that quietly holds a fact valid from
    /// the twenty-second century is harder to notice than one that rejected it.
    /// </remarks>
    private static readonly TimeSpan FutureLimit = TimeSpan.FromDays(365 * 5);

    private readonly ExtractionWindow _window = window ?? throw new ArgumentNullException(nameof(window));

    public List<StagedFact> Facts { get; } = [];

    public List<StagedAnnotation> Corroborations { get; } = [];

    public List<StagedAnnotation> Contradictions { get; } = [];

    /// <summary>True when the model said, in as many words, that there is nothing here.</summary>
    public bool NothingToRecord { get; private set; }

    public string? NothingReason { get; private set; }

    /// <summary>Handles one call, and answers it the way the model will read the answer.</summary>
    public ToolReply Dispatch(string name, string argumentsJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        try
        {
            using var document = JsonDocument.Parse(
                string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);

            var arguments = document.RootElement;

            return name switch
            {
                FactTools.RecordFact => Record(arguments),
                FactTools.CorroborateFact => Annotate(arguments, Corroborations, "corroborates"),
                FactTools.ContradictFact => Annotate(arguments, Contradictions, "contradicts"),
                FactTools.SupersedeFact => Supersede(arguments),
                FactTools.NothingToRecord => Nothing(arguments),
                _ => new ToolReply(false, $"There is no tool called {name}."),
            };
        }
        catch (JsonException)
        {
            return new ToolReply(false, "Those arguments were not valid JSON. Send the call again.");
        }
    }

    private ToolReply Record(JsonElement arguments)
    {
        if (!Subject(arguments, out var personId, out var pair, out var subjectError))
        {
            return new ToolReply(false, subjectError);
        }

        if (!Text(arguments, "predicate", MaxPredicate, out var predicate, out var error)
            || !Text(arguments, "object", MaxObject, out var objectText, out error)
            || !Text(arguments, "claim", MaxClaim, out var claim, out error))
        {
            return new ToolReply(false, error);
        }

        if (!PredicatePattern().IsMatch(predicate))
        {
            return new ToolReply(
                false,
                $"'{predicate}' is not a usable predicate. Use lowercase English words joined by "
                + "underscores, like works_at.");
        }

        if (RestatesPredicate(predicate, objectText))
        {
            return new ToolReply(false, Restated(objectText));
        }

        if (!Confidence(arguments, out var confidence, out error)
            || !Citations(arguments, "asserts", out var citations, out error))
        {
            return new ToolReply(false, error);
        }

        if (!Validity(arguments, "valid_from", out var validFrom, out error)
            || !Validity(arguments, "valid_to", out var validTo, out error))
        {
            return new ToolReply(false, error);
        }

        if (validFrom is not null && validTo is not null
            && string.CompareOrdinal(validFrom.Value.Utc, validTo.Value.Utc) > 0)
        {
            return new ToolReply(false, "valid_from is after valid_to.");
        }

        var all = citations.ToList();

        if (validFrom is { } from)
        {
            all.Add(new StagedCitation(from.MessageId, "establishes_valid_from", null));
        }

        if (validTo is { } to)
        {
            all.Add(new StagedCitation(to.MessageId, "establishes_valid_to", null));
        }

        Facts.Add(new StagedFact
        {
            SubjectPersonId = personId,
            SubjectPair = pair,
            Predicate = predicate,
            ObjectText = objectText,
            ClaimText = claim,
            Confidence = confidence,
            EvidenceKind = EvidenceKind(personId, pair, citations),
            OriginKind = _window.IsGroup ? "group" : "dm",
            ValidFromUtc = validFrom?.Utc,
            ValidToUtc = validTo?.Utc,
            Citations = all,
        });

        return new ToolReply(true, "Recorded.");
    }

    private ToolReply Supersede(JsonElement arguments)
    {
        if (!Text(arguments, "fact_id", 80, out var factId, out var error)
            || !Text(arguments, "object", MaxObject, out var objectText, out error)
            || !Text(arguments, "claim", MaxClaim, out var claim, out error))
        {
            return new ToolReply(false, error);
        }

        var known = _window.Known.FirstOrDefault(f => f.Id == factId);

        if (known is null)
        {
            return new ToolReply(false, $"There is no known fact with id {factId}.");
        }

        if (RestatesPredicate(known.Predicate, objectText))
        {
            return new ToolReply(false, Restated(objectText));
        }

        if (!Confidence(arguments, out var confidence, out error)
            || !Citations(arguments, "asserts", out var citations, out error)
            || !Validity(arguments, "valid_from", out var validFrom, out error))
        {
            return new ToolReply(false, error);
        }

        var all = citations.ToList();

        if (validFrom is { } from)
        {
            all.Add(new StagedCitation(from.MessageId, "establishes_valid_from", null));
        }

        Facts.Add(new StagedFact
        {
            SubjectPersonId = known.Subject,
            Predicate = known.Predicate,
            ObjectText = objectText,
            ClaimText = claim,
            Confidence = confidence,
            EvidenceKind = EvidenceKind(known.Subject, null, citations),
            OriginKind = _window.IsGroup ? "group" : "dm",
            ValidFromUtc = validFrom?.Utc,
            SupersedesFactId = factId,
            Citations = all,
        });

        return new ToolReply(true, "Replaced.");
    }

    private ToolReply Annotate(JsonElement arguments, List<StagedAnnotation> into, string role)
    {
        if (!Text(arguments, "fact_id", 80, out var factId, out var error))
        {
            return new ToolReply(false, error);
        }

        if (_window.Known.All(f => f.Id != factId))
        {
            return new ToolReply(false, $"There is no known fact with id {factId}.");
        }

        if (!Citations(arguments, role, out var citations, out error))
        {
            return new ToolReply(false, error);
        }

        into.Add(new StagedAnnotation(
            factId,
            citations,
            arguments.TryGetProperty("note", out var note) ? note.GetString() : null));

        return new ToolReply(true, "Noted.");
    }

    private ToolReply Nothing(JsonElement arguments)
    {
        NothingToRecord = true;
        NothingReason = arguments.TryGetProperty("reason", out var reason) ? reason.GetString() : null;

        return new ToolReply(true, "Understood.");
    }

    /// <summary>
    /// Self-reported or reflected, from who sent the message it was found in.
    /// </summary>
    /// <remarks>
    /// §7: reflected evidence — someone else saying it to them — captures what people never say
    /// about themselves and is also the easiest to get wrong, so it has to be distinguishable
    /// later. Derived rather than asked, because it is a fact about the transcript.
    /// </remarks>
    private string EvidenceKind(
        string? personId, (string A, string B)? pair, IReadOnlyList<StagedCitation> citations)
    {
        var sender = _window.SenderOf(citations[0].MessageId);

        if (sender is null)
        {
            return "reflected";
        }

        var subjectSpoke = personId is not null
            ? sender == personId
            : pair is { } p && (sender == p.A || sender == p.B);

        return subjectSpoke ? "self_report" : "reflected";
    }

    private bool Subject(
        JsonElement arguments,
        out string? personId,
        out (string A, string B)? pair,
        out string error)
    {
        personId = null;
        pair = null;
        error = string.Empty;

        if (!arguments.TryGetProperty("subject_person", out var first)
            || first.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(first.GetString()))
        {
            error = "subject_person is required: the person id this is about.";
            return false;
        }

        var a = first.GetString()!.Trim();

        if (!Known(a))
        {
            error = $"{a} is not one of the people in this conversation.";
            return false;
        }

        var hasSecond = arguments.TryGetProperty("subject_person_b", out var second)
            && second.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(second.GetString());

        if (!hasSecond)
        {
            personId = a;

            return true;
        }

        var b = second.GetString()!.Trim();

        if (!Known(b))
        {
            error = $"{b} is not one of the people in this conversation.";
            return false;
        }

        if (a == b)
        {
            error = "A fact about a relationship needs two different people.";
            return false;
        }

        // Canonical order, so an unordered pair cannot end up stored twice with two different
        // stories — which is the whole reason edges have a CHECK on their column order.
        pair = string.CompareOrdinal(a, b) < 0 ? (a, b) : (b, a);

        return true;
    }

    private bool Known(string personId) => _window.People.Any(p => p.Id == personId);

    private static bool Text(
        JsonElement arguments, string name, int limit, out string value, out string error)
    {
        value = string.Empty;
        error = string.Empty;

        if (!arguments.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            error = $"{name} is required.";
            return false;
        }

        value = element.GetString()!.Trim();

        if (value.Length > limit)
        {
            error = $"{name} is too long — keep it under {limit} characters.";
            return false;
        }

        return true;
    }

    private static bool Confidence(JsonElement arguments, out double value, out string error)
    {
        value = 0;
        error = string.Empty;

        if (!arguments.TryGetProperty("confidence", out var element)
            || !element.TryGetDouble(out value))
        {
            error = "confidence is required, as a number between 0 and 1.";
            return false;
        }

        if (value is < 0 or > 1)
        {
            error = "confidence must be between 0 and 1.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// Reads <c>message_ids</c>, and the one optional quote that goes with the first of them.
    /// </summary>
    /// <remarks>
    /// A flat list of integers rather than a list of objects, because a nested citation shape is
    /// what made a local runtime abort compiling the grammar. The quote is per call rather than
    /// per message, which is what it almost always was anyway.
    /// </remarks>
    private bool Citations(
        JsonElement arguments,
        string role,
        out IReadOnlyList<StagedCitation> citations,
        out string error)
    {
        citations = [];
        error = string.Empty;

        if (!arguments.TryGetProperty("message_ids", out var element)
            || element.ValueKind != JsonValueKind.Array)
        {
            error = "message_ids is required: the ids of the messages this comes from.";
            return false;
        }

        var quote = arguments.TryGetProperty("quote", out var quoteElement)
            ? quoteElement.GetString()
            : null;

        var found = new List<StagedCitation>();

        foreach (var item in element.EnumerateArray())
        {
            if (!item.TryGetInt64(out var id))
            {
                error = "message_ids must be a list of message ids, as numbers.";
                return false;
            }

            if (!_window.CitableIds.Contains(id))
            {
                error = $"Message {id} is not in this conversation. Cite only the ids shown above.";
                return false;
            }

            found.Add(new StagedCitation(id, role, found.Count == 0 ? quote : null));
        }

        if (found.Count == 0)
        {
            // §8: anything without a citation is dropped rather than shown. Refusing here means
            // the model is told while it can still fix it.
            error = "message_ids cannot be empty — a claim with no message behind it is not recorded.";
            return false;
        }

        citations = found;

        return true;
    }

    private bool Validity(
        JsonElement arguments, string name, out (string Utc, long MessageId)? value, out string error)
    {
        value = null;
        error = string.Empty;

        if (!arguments.TryGetProperty(name, out var element)
            || element.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(element.GetString()))
        {
            return true;
        }

        if (!DateTime.TryParse(
                element.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var date))
        {
            error = $"{name} must be a date like 2019-04-01.";
            return false;
        }

        if (date > DateTime.UtcNow + FutureLimit)
        {
            error = $"{name} is too far in the future to be believable.";
            return false;
        }

        if (!arguments.TryGetProperty($"{name}_message_id", out var idElement)
            || !idElement.TryGetInt64(out var messageId)
            || !_window.CitableIds.Contains(messageId))
        {
            error = $"{name}_message_id must be a message from this conversation.";
            return false;
        }

        value = (date.ToString("O", CultureInfo.InvariantCulture), messageId);

        return true;
    }

    /// <summary>
    /// True when the value only repeats the key: <c>landed</c> for <c>landed</c>.
    /// </summary>
    /// <remarks>
    /// Found on the first run against a real model, which recorded "just landed" as
    /// <c>landed = landed</c> at 0.9. A fact is a key and a value that says something about it;
    /// one with nothing to put in the value is almost always a moment — arriving, running late —
    /// rather than something true of a person, which the prompt already asks not to be recorded.
    /// This check is mechanical, so it holds whatever the archive and whatever the model.
    /// </remarks>
    private static bool RestatesPredicate(string predicate, string objectText)
    {
        var value = objectText.Trim().ToLower(CultureInfo.InvariantCulture).Replace(' ', '_');

        return value.Length > 0 && (value == predicate || predicate.Split('_').Contains(value));
    }

    private static string Restated(string objectText) =>
        $"'{objectText}' only repeats the predicate. A fact needs a value that says something, "
        + "like works_at: 'Acme'. If there is no such value, this is a moment rather than a fact, "
        + "and is better not recorded.";

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex PredicatePattern();
}
