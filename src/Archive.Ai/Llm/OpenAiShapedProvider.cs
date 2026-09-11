using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Archive.Ai.Llm;

/// <summary>
/// Every provider this app speaks to, because all four families are OpenAI-shaped.
/// </summary>
/// <remarks>
/// <para>
/// Plain <see cref="HttpClient"/> and <c>System.Text.Json</c>, no SDK. That is not asceticism: an
/// SDK per family would be four packages in a build that ships to people who may never enable any
/// of this, and the wire format here is three endpoints wide.
/// </para>
/// <para>
/// The families differ only in their default base URL and whether a key is expected. A configured
/// endpoint always wins, so any of them can be pointed at a local server or a gateway.
/// </para>
/// </remarks>
public abstract class OpenAiShapedProvider : ILlmProvider
{
    /// <summary>
    /// One handler for the whole process.
    /// </summary>
    /// <remarks>
    /// The obvious alternative — a fresh <see cref="HttpClient"/> per call — exhausts ephemeral
    /// ports under exactly the workload this app has, which is tens of thousands of small calls
    /// in a row against one host. Sockets are pooled here; the per-provider client below is a thin
    /// wrapper that owns only the timeout.
    /// </remarks>
    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    };

    /// <summary>
    /// One client for the process, with no timeout of its own.
    /// </summary>
    /// <remarks>
    /// The timeout is applied per call through a linked cancellation token instead, so that a
    /// provider carries no disposable state and the setting can change without rebuilding
    /// anything. <see cref="HttpClient.Timeout"/> would also be indistinguishable from a user
    /// cancellation at the catch site, which is the one distinction the retry policy turns on.
    /// </remarks>
    private static readonly HttpClient Shared = new(SharedHandler, disposeHandler: false)
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;

    /// <param name="handler">
    /// A transport to use instead of the shared one. Only tests pass this: what is worth checking
    /// about a provider is the shape of the request it builds and what it makes of the answer,
    /// and neither can be seen without standing in for the network.
    /// </param>
    protected OpenAiShapedProvider(AiSettings settings, HttpMessageHandler? handler = null)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Settings = settings;

        var configured = new Uri(
            string.IsNullOrWhiteSpace(settings.Endpoint) ? DefaultBaseUrl : settings.Endpoint.Trim());

        // Scheme, host and path, with a trailing slash so that resolving "chat/completions"
        // against it appends rather than replaces the last segment — without it, a base of
        // ".../v1" produces ".../chat/completions" and every call 404s.
        //
        // A query string is dropped rather than carried: relative resolution discards it anyway,
        // so keeping it would only put a key that some gateways pass that way into the statistics.
        BaseUrl = new Uri(configured.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");

        _http = handler is null
            ? Shared
            : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public abstract LlmProviderKind Kind { get; }

    public Uri BaseUrl { get; }

    protected AiSettings Settings { get; }

    /// <summary>Used when the configured endpoint is blank.</summary>
    protected abstract string DefaultBaseUrl { get; }

    public async Task<LlmCompletion> ChatAsync(
        LlmChatRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = request.Model,
            ["messages"] = request.Messages.Select(ToWire).ToArray(),
            ["temperature"] = request.Temperature,
            ["max_tokens"] = request.MaxTokens,
        };

        if (request.Tools is { Count: > 0 })
        {
            payload["tools"] = request.Tools.Select(ToWire).ToArray();
            payload["tool_choice"] = request.ToolChoice switch
            {
                LlmToolChoice.None => "none",
                LlmToolChoice.Required => "required",
                _ => "auto",
            };
        }

        var body = await PostAsync("chat/completions", payload, cancellationToken).ConfigureAwait(false);

        return ReadCompletion(body);
    }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        string model, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(texts);

        if (texts.Count == 0)
        {
            return [];
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["model"] = model,
            ["input"] = texts,
        };

        var body = await PostAsync("embeddings", payload, cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            throw new LlmProviderException("The provider returned no embeddings.", errorPayload: body);
        }

        // Ordered by the index the provider reports rather than by arrival: a batch that comes
        // back shuffled would attach every vector to the wrong session, silently.
        return [.. data.EnumerateArray()
            .OrderBy(item => item.TryGetProperty("index", out var index) ? index.GetInt32() : 0)
            .Select(item => item.GetProperty("embedding").EnumerateArray().Select(v => v.GetSingle()).ToArray())];
    }

    public async Task<IReadOnlyList<LlmModelInfo>> ListModelsAsync(
        CancellationToken cancellationToken = default)
    {
        var body = await SendAsync(
            () => Request(HttpMethod.Get, "models", body: null), cancellationToken).ConfigureAwait(false);

        using var document = JsonDocument.Parse(body);

        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return [.. data.EnumerateArray()
            .Select(item => item.TryGetProperty("id", out var id) ? id.GetString() : null)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => new LlmModelInfo(id!))
            .OrderBy(model => model.Id, StringComparer.OrdinalIgnoreCase)];
    }

    private static object ToWire(LlmChatMessage message)
    {
        var wire = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["role"] = message.Role switch
            {
                LlmRole.System => "system",
                LlmRole.User => "user",
                LlmRole.Assistant => "assistant",
                _ => "tool",
            },
            ["content"] = message.Content,
        };

        if (message.ToolCallId is not null)
        {
            wire["tool_call_id"] = message.ToolCallId;
        }

        if (message.ToolCalls is { Count: > 0 })
        {
            wire["tool_calls"] = message.ToolCalls.Select(call => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = call.Id,
                ["type"] = "function",
                ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["name"] = call.Name,
                    ["arguments"] = call.ArgumentsJson,
                },
            }).ToArray();
        }

        return wire;
    }

    private static object ToWire(LlmToolDefinition tool) =>
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["type"] = "function",
            ["function"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                // Already JSON: parsed rather than re-serialized so the schema in the source file
                // is the schema on the wire.
                ["parameters"] = JsonSerializer.Deserialize<JsonElement>(tool.ParametersJsonSchema),
            },
        };

    private static LlmCompletion ReadCompletion(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (!root.TryGetProperty("choices", out var choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
        {
            throw new LlmProviderException("The provider returned no choices.", errorPayload: body);
        }

        var choice = choices[0];
        var message = choice.TryGetProperty("message", out var m) ? m : default;

        var calls = new List<LlmToolCall>();

        if (message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.TryGetProperty("function", out var f) ? f : default;

                if (function.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                calls.Add(new LlmToolCall(
                    Id: call.TryGetProperty("id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
                    Name: function.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                    ArgumentsJson: function.TryGetProperty("arguments", out var args)
                        ? args.ValueKind == JsonValueKind.String ? args.GetString() ?? "{}" : args.GetRawText()
                        : "{}"));
            }
        }

        var usage = root.TryGetProperty("usage", out var u) ? u : default;

        return new LlmCompletion
        {
            Content = message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out var content)
                    ? content.GetString()
                    : null,
            ToolCalls = calls,
            FinishReason = choice.TryGetProperty("finish_reason", out var reason) ? reason.GetString() : null,
            SystemFingerprint = root.TryGetProperty("system_fingerprint", out var fingerprint)
                ? fingerprint.GetString()
                : null,
            PromptTokens = Count(usage, "prompt_tokens"),
            CompletionTokens = Count(usage, "completion_tokens"),
            TotalTokens = Count(usage, "total_tokens"),
        };

        static int? Count(JsonElement usage, string name) =>
            usage.ValueKind == JsonValueKind.Object
            && usage.TryGetProperty(name, out var value)
            && value.TryGetInt32(out var parsed)
                ? parsed
                : null;
    }

    private Task<string> PostAsync(
        string path, Dictionary<string, object?> payload, CancellationToken cancellationToken) =>
        SendAsync(() => Request(HttpMethod.Post, path, JsonSerializer.Serialize(payload, JsonOptions)),
            cancellationToken);

    private HttpRequestMessage Request(HttpMethod method, string path, string? body)
    {
        var request = new HttpRequestMessage(method, new Uri(BaseUrl, path));

        if (!string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Settings.ApiKey.Trim());
        }

        if (body is not null)
        {
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        }

        return request;
    }

    /// <summary>
    /// Sends, retrying transient failures with exponential backoff and jitter.
    /// </summary>
    /// <remarks>
    /// Transient means a connection error, a timeout, or 408/429/5xx. A 4xx is the request being
    /// wrong and retrying it is just a slower way to fail. A cancellation the caller asked for is
    /// never retried and propagates unchanged, so pausing the runner stops it rather than starting
    /// a backoff nobody is waiting for.
    /// </remarks>
    private async Task<string> SendAsync(
        Func<HttpRequestMessage> create, CancellationToken cancellationToken)
    {
        var maxRetries = Math.Max(0, Settings.MaxRetries);

        for (var attempt = 0; ; attempt++)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(Settings.TimeoutMs > 0 ? Settings.TimeoutMs : 120_000);

            try
            {
                using var request = create();
                using var response = await _http.SendAsync(request, deadline.Token).ConfigureAwait(false);

                var status = (int)response.StatusCode;
                var body = await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    return body;
                }

                if (IsTransient(status) && attempt < maxRetries)
                {
                    await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                throw new LlmProviderException(
                    $"The provider returned {status.ToString(CultureInfo.InvariantCulture)}.", status, body);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
            {
                // Reaching here with the caller's token still live means the deadline fired,
                // which is as transient as a dropped socket and is retried the same way.
                if (attempt >= maxRetries)
                {
                    // A timeout from a local endpoint is most often a model still being read in from
                    // disk — minutes, on a hard drive, for the first call after it was unloaded. Said
                    // plainly, because "did not answer" alone sends people looking for a network fault.
                    var seconds = Math.Max(1, (Settings.TimeoutMs > 0 ? Settings.TimeoutMs : 120_000) / 1000);

                    throw new LlmProviderException(
                        ex is HttpRequestException
                            ? $"Could not reach {BaseUrl.Host}: {ex.Message}"
                            : $"{BaseUrl.Host} did not answer within {seconds} seconds. A local model may "
                              + "still be loading — from a hard drive that can take minutes. Try again "
                              + "shortly, or give it longer under \"Wait for an answer\" on the AI page.",
                        inner: ex);
                }

                await BackoffAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(int status) => status is 408 or 429 or 500 or 502 or 503 or 504;

    private Task BackoffAsync(int attempt, CancellationToken cancellationToken)
    {
        var baseDelay = Math.Max(0, Settings.RetryBaseDelayMs);
        var delay = baseDelay * (1 << Math.Min(attempt, 6));

        return Task.Delay(delay + Random.Shared.Next(0, 100), cancellationToken);
    }
}
