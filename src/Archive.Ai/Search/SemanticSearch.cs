using Archive.Ai.Llm;

namespace Archive.Ai.Search;

/// <summary>A session, and how close it is in meaning to what was asked.</summary>
public sealed record SemanticHit(string SessionId, float Score);

/// <summary>Whether search by meaning can run, and on what.</summary>
/// <param name="Model">The model whose vectors a search would use — not necessarily the one configured.</param>
/// <param name="Recommended">
/// True once enough of the archive is covered that ranking by meaning helps more than it confuses;
/// what the search bar's default follows (ai-plan.md §10).
/// </param>
public sealed record SemanticAvailability(bool IsAvailable, string? Model, double Coverage, bool Recommended)
{
    public static SemanticAvailability None { get; } = new(false, null, 0, false);
}

/// <summary>
/// Finds sessions by meaning: embed the query, compare it with every stored vector.
/// </summary>
/// <remarks>
/// <para>
/// Zero coverage is silent (§10). With AI off, no consent for the endpoint, or nothing embedded with
/// a model search can use, this reports itself unavailable and the search bar never mentions it —
/// an empty "semantic" pane explaining what the user is missing is exactly the failure P1 names.
/// </para>
/// <para>
/// The query goes to the endpoint to be embedded, so it needs the same agreement reading does. It
/// is user-typed rather than archive text, but it is still words about their life sent somewhere.
/// </para>
/// </remarks>
public sealed class SemanticSearch(EmbeddingStore store, AiClient client, AiState state, ILlmProviderFactory factory)
{
    /// <summary>Coverage past which meaning is switched on by default.</summary>
    private const double RecommendedCoverage = 0.5;

    /// <summary>
    /// How close to the best match a session has to be to count as a match at all.
    /// </summary>
    /// <remarks>
    /// Relative rather than absolute, because what a cosine of 0.6 means differs from one embedding
    /// model to the next, and the user chooses the model.
    /// </remarks>
    private const float RelativeFloor = 0.8f;

    private readonly EmbeddingStore _store = store ?? throw new ArgumentNullException(nameof(store));

    private readonly AiClient _client = client ?? throw new ArgumentNullException(nameof(client));

    private readonly AiState _state = state ?? throw new ArgumentNullException(nameof(state));

    private readonly ILlmProviderFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    private readonly Lock _gate = new();

    /// <summary>
    /// The vectors of the active model, held once loaded.
    /// </summary>
    /// <remarks>
    /// Keyed on the model and its count, so a run that adds vectors, or a switch to a new model, is
    /// seen on the next search without anything having to say so.
    /// </remarks>
    private (string Model, long Count, IReadOnlyList<(string SessionId, float[] Vector)> Vectors)? _cache;

    public SemanticAvailability Availability()
    {
        var settings = _state.Current;

        if (!AiConsent.CoversExtraction(settings, _factory))
        {
            return SemanticAvailability.None;
        }

        var model = _store.Active(settings.EmbeddingModel);

        if (model is null)
        {
            return SemanticAvailability.None;
        }

        var coverage = _store.Coverage(model).Fraction;

        return new SemanticAvailability(true, model, coverage, coverage >= RecommendedCoverage);
    }

    /// <summary>The sessions nearest in meaning to a query, best first.</summary>
    public async Task<IReadOnlyList<SemanticHit>> SearchAsync(
        string query, int limit, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);

        var availability = Availability();

        if (!availability.IsAvailable || availability.Model is not { } model)
        {
            return [];
        }

        // Embedded with the model the vectors were made with, which may not be the one configured:
        // a query in one space compared with documents in another ranks nonsense confidently.
        var settings = _state.Current.Clone();
        settings.EmbeddingModel = model;

        var embedded = await _client
            .EmbedAsync(settings, [SessionText.Query(model, query)], default, cancellationToken)
            .ConfigureAwait(false);

        if (embedded.Count == 0 || embedded[0].Length == 0)
        {
            return [];
        }

        var target = EmbeddingStore.Decode(EmbeddingStore.Encode(embedded[0]));
        var vectors = Vectors(model);

        var ranked = vectors
            .Where(v => v.Vector.Length == target.Length)
            .Select(v => new SemanticHit(v.SessionId, Dot(v.Vector, target)))
            .OrderByDescending(hit => hit.Score)
            .ToList();

        if (ranked.Count == 0)
        {
            return [];
        }

        // Nearest neighbours always exist, however far away they are. Only those reasonably close
        // to the best match are returned: a session about a knee is not "found by meaning" for a
        // search about a trip just because it came second out of two.
        var floor = ranked[0].Score * RelativeFloor;

        return [.. ranked.Where(hit => hit.Score > 0 && hit.Score >= floor).Take(limit)];
    }

    private IReadOnlyList<(string SessionId, float[] Vector)> Vectors(string model)
    {
        var count = _store.Count(model);

        lock (_gate)
        {
            if (_cache is { } cache && cache.Model == model && cache.Count == count)
            {
                return cache.Vectors;
            }
        }

        var vectors = _store.Vectors(model);

        lock (_gate)
        {
            _cache = (model, count, vectors);
        }

        return vectors;
    }

    private static float Dot(float[] a, float[] b)
    {
        var sum = 0f;

        for (var i = 0; i < a.Length; i++)
        {
            sum += a[i] * b[i];
        }

        return sum;
    }
}
