using Archive.Ai.Attachments;
using Archive.Ai.Diary;
using Archive.Ai.Extraction;
using Archive.Ai.Jobs;
using Archive.Ai.Llm;
using Archive.Ai.Merging;
using Archive.Ai.Search;
using Archive.Ai.Sessions;
using Microsoft.Extensions.DependencyInjection;

namespace Archive.Ai;

/// <summary>Registers the AI layer. One call, from the head, and nothing else knows it happened.</summary>
/// <remarks>
/// Removing this call and the project reference leaves an app that still builds and still opens
/// every archive — which is AGENTS.md P1 stated as something a person can actually check.
/// </remarks>
public static class AiServiceCollectionExtensions
{
    public static IServiceCollection AddAi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AiSettingsStore>();
        services.AddSingleton<AiState>();
        services.AddSingleton<ILlmProviderFactory>(_ => new LlmProviderFactory());
        services.AddSingleton<AiInteractions>();
        services.AddSingleton<AiClient>();
        services.AddSingleton<AiConnectionCheck>();
        services.AddSingleton<AiCoverage>();
        services.AddSingleton<SessionSegmenter>();
        services.AddSingleton<AiJobs>();
        services.AddSingleton<IAiJobHandler, SegmentJobHandler>();
        services.AddSingleton<ExtractionWindows>();
        services.AddSingleton<FactWriter>();
        services.AddSingleton<FactStore>();
        services.AddSingleton<AiExclusions>();
        services.AddSingleton<AiForget>();
        services.AddSingleton<ExtractRunner>();
        services.AddSingleton<IAiJobHandler, ExtractJobHandler>();
        services.AddSingleton<FactMerger>();
        services.AddSingleton<IAiJobHandler, AdjudicateJobHandler>();
        services.AddSingleton<DiaryInputs>();
        services.AddSingleton<DiaryStore>();
        services.AddSingleton<DiaryRunner>();
        services.AddSingleton<DiaryPlanner>();
        services.AddSingleton<IAiJobHandler, DiaryJobHandler>();
        services.AddSingleton<IAiJobHandler, RollupJobHandler>();
        services.AddSingleton<EmbeddingStore>();
        services.AddSingleton<SessionText>();
        services.AddSingleton<SemanticSearch>();
        services.AddSingleton<HybridSearch>();
        services.AddSingleton<IAiJobHandler, EmbedJobHandler>();
        services.AddSingleton<MediaReader>();
        services.AddSingleton<IAiJobHandler, OcrJobHandler>();
        services.AddSingleton<IAiJobHandler, TranscribeJobHandler>();
        services.AddSingleton<AiBudget>();
        services.AddSingleton<AiRunner>();
        services.AddSingleton<AiWork>();

        return services;
    }
}
