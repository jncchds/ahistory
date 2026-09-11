namespace Archive.Ai.Llm;

/// <summary>Any endpoint that speaks the OpenAI wire format. The default, and what a gateway is.</summary>
public sealed class OpenAiCompatibleProvider(AiSettings settings, HttpMessageHandler? handler = null)
    : OpenAiShapedProvider(settings, handler)
{
    public override LlmProviderKind Kind => LlmProviderKind.OpenAiCompatible;

    protected override string DefaultBaseUrl => "http://localhost:1234/v1";
}

/// <summary>OpenAI itself.</summary>
public sealed class OpenAiProvider(AiSettings settings, HttpMessageHandler? handler = null)
    : OpenAiShapedProvider(settings, handler)
{
    public override LlmProviderKind Kind => LlmProviderKind.OpenAi;

    protected override string DefaultBaseUrl => "https://api.openai.com/v1";
}

/// <summary>Ollama, through its OpenAI-compatible surface rather than its native one.</summary>
public sealed class OllamaProvider(AiSettings settings, HttpMessageHandler? handler = null)
    : OpenAiShapedProvider(settings, handler)
{
    public override LlmProviderKind Kind => LlmProviderKind.Ollama;

    protected override string DefaultBaseUrl => "http://localhost:11434/v1";
}

/// <summary>Google AI Studio, likewise through its OpenAI-compatible surface.</summary>
public sealed class GoogleAiStudioProvider(AiSettings settings, HttpMessageHandler? handler = null)
    : OpenAiShapedProvider(settings, handler)
{
    public override LlmProviderKind Kind => LlmProviderKind.GoogleAiStudio;

    protected override string DefaultBaseUrl => "https://generativelanguage.googleapis.com/v1beta/openai";
}

/// <summary>
/// Builds the provider a configuration names.
/// </summary>
/// <remarks>
/// A fresh instance per call, because settings change while the app is running and a cached
/// provider would keep talking to the endpoint the user has just moved away from.
/// </remarks>
public sealed class LlmProviderFactory(HttpMessageHandler? handler = null) : ILlmProviderFactory
{
    public ILlmProvider Create(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.Provider switch
        {
            LlmProviderKind.OpenAi => new OpenAiProvider(settings, handler),
            LlmProviderKind.Ollama => new OllamaProvider(settings, handler),
            LlmProviderKind.GoogleAiStudio => new GoogleAiStudioProvider(settings, handler),
            _ => new OpenAiCompatibleProvider(settings, handler),
        };
    }
}
