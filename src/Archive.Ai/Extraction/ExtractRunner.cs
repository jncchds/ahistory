using System.Globalization;
using System.Text;
using Archive.Ai.Llm;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ai.Extraction;

/// <summary>How an extraction ended.</summary>
public enum ExtractionOutcome
{
    /// <summary>Facts were written.</summary>
    Written,

    /// <summary>The model said there was nothing here, which is the common and correct case.</summary>
    NothingToRecord,

    /// <summary>The session is not to be read: excluded, empty, or gone.</summary>
    Skipped,

    /// <summary>The model could not produce a usable call. Visible, not swallowed.</summary>
    NeedsReview,
}

/// <summary>Reading one session with a model, from the transcript to the committed rows.</summary>
public sealed record ExtractionReport(ExtractionOutcome Outcome, ExtractionResult? Result, string? Note);

/// <summary>
/// Reads one session and records what it says (§6.3).
/// </summary>
/// <remarks>
/// <para>
/// The loop is: show the transcript, take the tool calls, answer each one, and let the model fix
/// what was refused. It ends when the model stops calling tools, or when the correction rounds run
/// out — at which point the session is marked for review rather than discarded, because silently
/// dropping a session hides a prompt regression behind a coverage number that still says 100%.
/// </para>
/// <para>
/// Nothing is written until the conversation is over. See <see cref="FactWriter"/> for why.
/// </para>
/// </remarks>
public sealed class ExtractRunner(
    AiClient client,
    ExtractionWindows windows,
    FactWriter writer,
    ILogger<ExtractRunner>? logger = null)
{
    /// <summary>
    /// How many times the model may be told it got a call wrong.
    /// </summary>
    /// <remarks>
    /// Bounded because a model that cannot satisfy the validator will not start being able to on
    /// the ninth attempt, and every round is a paid call over the same transcript.
    /// </remarks>
    private const int CorrectionRounds = 2;

    private readonly AiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    private readonly ExtractionWindows _windows = windows ?? throw new ArgumentNullException(nameof(windows));

    private readonly FactWriter _writer = writer ?? throw new ArgumentNullException(nameof(writer));

    private readonly ILogger _log = logger ?? NullLogger<ExtractRunner>.Instance;

    public async Task<ExtractionReport> RunAsync(
        AiSettings settings, string sessionId, string inputHash, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var window = _windows.Load(sessionId);

        if (window is null)
        {
            return new ExtractionReport(ExtractionOutcome.Skipped, null, "Not readable.");
        }

        var model = settings.ModelFor(AiWorkKind.Utility);
        var promptVersion = PromptCatalog.VersionOf(PromptCatalog.ExtractSession);

        var staged = new ToolDispatcher(window);

        var conversation = new List<LlmChatMessage>
        {
            LlmChatMessage.System(PromptCatalog.TextOf(PromptCatalog.ExtractSession)),
            LlmChatMessage.User(Context(window, settings.OutputLanguage)),
        };

        var calledAnything = false;

        // What the endpoint says it ran, when it says anything at all. Most do not, and "unknown"
        // is the honest record: a made-up version would make "re-run what the old model did" a
        // query that cannot tell two models apart.
        var modelVersion = "unknown";

        for (var round = 0; round <= CorrectionRounds; round++)
        {
            var completion = await _client.ChatAsync(
                settings,
                new LlmChatRequest
                {
                    Model = model,
                    Messages = conversation,
                    Tools = FactTools.All,
                    ToolChoice = LlmToolChoice.Auto,
                    Temperature = settings.Temperature,
                    MaxTokens = settings.MaxTokens,
                },
                AiPurpose.Extract,
                AiSubject.Session(sessionId),
                cancellationToken).ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(completion.SystemFingerprint))
            {
                modelVersion = completion.SystemFingerprint;
            }

            if (completion.ToolCalls.Count == 0)
            {
                break;
            }

            calledAnything = true;
            conversation.Add(LlmChatMessage.Assistant(completion.Content, completion.ToolCalls));

            var refused = 0;

            foreach (var call in completion.ToolCalls)
            {
                var reply = staged.Dispatch(call.Name, call.ArgumentsJson);

                if (!reply.Accepted)
                {
                    refused++;
                }

                conversation.Add(LlmChatMessage.ToolResult(call.Id, reply.Message));
            }

            // Everything landed, or the model has said there is nothing here. Either way it has
            // finished, and another round would only invite it to invent something.
            if (refused == 0 || staged.NothingToRecord)
            {
                break;
            }

            conversation.Add(LlmChatMessage.User(
                "Some of those calls were refused, with the reason after each. Send corrected "
                + "calls for those, and nothing for the ones that were accepted."));
        }

        if (!calledAnything)
        {
            // Answering in prose when tools were offered is the failure the settings-page probe
            // exists to catch early; reaching it here means it slipped through, or that this
            // particular transcript confused the model.
            _log.LogWarning("Session {SessionId} produced no tool calls.", sessionId);

            return new ExtractionReport(
                ExtractionOutcome.NeedsReview, null, "The model answered in prose instead of calling a tool.");
        }

        if (staged.NothingToRecord && staged.Facts.Count == 0)
        {
            // Still written: "there is nothing here" is a conclusion, and recording it is what
            // stops the session being read again at the same prompt version.
            var empty = _writer.Write(
                window, staged, model, modelVersion, promptVersion, settings.OutputLanguage, inputHash);

            return new ExtractionReport(ExtractionOutcome.NothingToRecord, empty, staged.NothingReason);
        }

        if (staged.Facts.Count == 0
            && staged.Corroborations.Count == 0
            && staged.Contradictions.Count == 0)
        {
            return new ExtractionReport(
                ExtractionOutcome.NeedsReview, null, "Every call the model made was refused.");
        }

        var result = _writer.Write(
            window, staged, model, modelVersion, promptVersion, settings.OutputLanguage, inputHash);

        return new ExtractionReport(ExtractionOutcome.Written, result, null);
    }

    /// <summary>
    /// The context header (§6.3): who these people are, what is already known, and the transcript.
    /// </summary>
    /// <remarks>
    /// Without the roster, pronouns and nicknames do not resolve and the model attributes half the
    /// conversation to the wrong person. Without the known facts it restates the same thing from
    /// every session it appears in, and the merge step has to undo work that never needed doing.
    /// </remarks>
    private static string Context(ExtractionWindow window, string language)
    {
        var text = new StringBuilder();

        text.AppendLine(CultureInfo.InvariantCulture, $"Write claims in: {language}");
        text.AppendLine();
        text.AppendLine(window.IsGroup
            ? "This is a group conversation. Anything said here is weaker evidence than the same "
              + "thing said privately — people perform for a room."
            : "This is a private conversation between the people below.");
        text.AppendLine();
        text.AppendLine("People, by id:");

        foreach (var person in window.People)
        {
            text.AppendLine(CultureInfo.InvariantCulture,
                $"  {person.Id} — {person.Name}{(person.IsOwner ? " (the archive's owner)" : string.Empty)}");
        }

        if (window.Known.Count > 0)
        {
            text.AppendLine();
            text.AppendLine("Already known about them:");

            foreach (var fact in window.Known)
            {
                text.AppendLine(CultureInfo.InvariantCulture, $"  {fact.Id} — {fact.ClaimText}");
            }
        }

        text.AppendLine();
        text.AppendLine("Transcript:");
        text.AppendLine();
        text.Append(window.Transcript());

        return text.ToString();
    }
}
