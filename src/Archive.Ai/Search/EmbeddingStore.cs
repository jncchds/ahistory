using System.Globalization;
using System.Runtime.InteropServices;
using Archive.Ai.Sessions;
using Archive.Data;

namespace Archive.Ai.Search;

/// <summary>How far one model has got through the sessions worth finding.</summary>
public sealed record EmbeddingCoverage(string Model, long Eligible, long Embedded)
{
    public double Fraction => Eligible == 0 ? 0 : (double)Embedded / Eligible;
}

/// <summary>
/// Vectors, one per session per model, stored as plain BLOBs (ai-plan.md §9.1).
/// </summary>
/// <remarks>
/// <para>
/// Any model and any dimension, because the canonical store is not an index: a BLOB with its own
/// length. Two models can sit side by side while one replaces the other, which is the whole of how
/// search keeps working, in the old space, for as long as a new model takes to build.
/// </para>
/// <para>
/// Vectors are normalized to unit length when written, so similarity is a dot product — and
/// little-endian float32, which is what every platform this app builds for already is in memory.
/// </para>
/// <para>
/// No <c>sqlite-vec</c>. Brute force over the BLOBs, held in memory once loaded, is fast enough for
/// one person's archive — the sessions worth embedding number in the tens of thousands at most —
/// and it keeps a native, per-platform extension out of a build that would otherwise ship it to
/// everyone. The index was always meant to be disposable; it is also, so far, unnecessary.
/// </para>
/// </remarks>
public sealed class EmbeddingStore(Database database)
{
    /// <summary>
    /// The sessions worth being found by meaning: the ones worth reading, minus everything left out.
    /// </summary>
    /// <remarks>
    /// The same scope extraction has. Embedding sends a session's text to the endpoint as surely as
    /// reading it does, so a person or conversation left out is left out of this too. Logistics
    /// sessions are not embedded at all — keyword search already finds "on my way", and a vector
    /// for it is noise that costs memory on every search.
    /// </remarks>
    internal const string Eligible = """
        s.is_substantive = 1
        AND s.segmenter_version = $segmenter
        AND t.ai_excluded = 0
        AND NOT EXISTS (SELECT 1 FROM save_meta WHERE ai_opt_out = 1)
        AND NOT (t.kind = 'dm' AND EXISTS (
            SELECT 1
            FROM thread_participant AS tp
            JOIN identity_person AS ip ON ip.identity_id = tp.identity_id
            JOIN person AS p ON p.id = ip.person_id
            WHERE tp.thread_id = t.id AND p.ai_excluded = 1))
        """;

    /// <summary>
    /// How close to complete the configured model has to be before search moves onto it.
    /// </summary>
    /// <remarks>
    /// Not 100%: an import that lands while the new model is finishing would otherwise hold search
    /// on the old one until the import, too, had been embedded.
    /// </remarks>
    private const double CompleteEnough = 0.98;

    private readonly Database _database = database ?? throw new ArgumentNullException(nameof(database));

    /// <summary>What a session's vector is keyed on, besides the model.</summary>
    public static string InputHash(string memberHash) => $"{memberHash}|{SessionText.Version}";

    /// <summary>Sessions in a thread with no current vector for this model.</summary>
    public IReadOnlyList<(string SessionId, string InputHash)> Pending(string threadId, string model)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT s.id, s.member_hash
            FROM session AS s
            JOIN thread AS t ON t.id = s.thread_id
            WHERE s.thread_id = $thread
              AND {Eligible}
              AND NOT EXISTS (
                  SELECT 1 FROM embedding AS e
                  WHERE e.session_id = s.id AND e.model = $model
                    AND e.input_hash = s.member_hash || '|' || $version)
            ORDER BY s.started_at_unix DESC;
            """;

        command.Parameters.AddWithValue("$thread", threadId);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$version", SessionText.Version);
        command.Parameters.AddWithValue("$segmenter", SessionSegmenter.Version);

        var pending = new List<(string, string)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            pending.Add((reader.GetString(0), InputHash(reader.GetString(1))));
        }

        return pending;
    }

    /// <summary>
    /// Threads with sessions still to embed with this model, newest first, keyed on exactly those.
    /// </summary>
    public IReadOnlyList<(string ThreadId, string InputHash, long LastUnix)> ThreadsNeeding(string model)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT s.thread_id, s.id, s.member_hash, s.ended_at_unix
            FROM session AS s
            JOIN thread AS t ON t.id = s.thread_id
            WHERE {Eligible}
              AND NOT EXISTS (
                  SELECT 1 FROM embedding AS e
                  WHERE e.session_id = s.id AND e.model = $model
                    AND e.input_hash = s.member_hash || '|' || $version)
            ORDER BY s.thread_id, s.id;
            """;

        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$version", SessionText.Version);
        command.Parameters.AddWithValue("$segmenter", SessionSegmenter.Version);

        var threads = new List<(string ThreadId, List<string> Parts, long LastUnix)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            var thread = reader.GetString(0);

            if (threads.Count == 0 || threads[^1].ThreadId != thread)
            {
                threads.Add((thread, [], 0));
            }

            var current = threads[^1];
            current.Parts.Add(reader.GetString(1) + ":" + reader.GetString(2));
            threads[^1] = current with { LastUnix = Math.Max(current.LastUnix, reader.GetInt64(3)) };
        }

        return [.. threads.Select(t => (
            t.ThreadId,
            Merging.FactMerger.Hash($"{model}|{SessionText.Version}\n{string.Join('\n', t.Parts)}"),
            t.LastUnix))];
    }

    /// <summary>Writes a batch of vectors, normalized, in one short transaction.</summary>
    public void Write(string model, IReadOnlyList<(string SessionId, string InputHash, float[] Vector)> vectors)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(vectors);

        var now = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        using var connection = _database.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT INTO embedding (session_id, model, dim, vector, input_hash, created_utc)
            VALUES ($session, $model, $dim, $vector, $hash, $now)
            ON CONFLICT (session_id, model) DO UPDATE SET
                dim = excluded.dim, vector = excluded.vector,
                input_hash = excluded.input_hash, created_utc = excluded.created_utc;
            """;

        var session = command.Parameters.AddWithValue("$session", string.Empty);
        command.Parameters.AddWithValue("$model", model);
        var dim = command.Parameters.AddWithValue("$dim", 0);
        var blob = command.Parameters.AddWithValue("$vector", Array.Empty<byte>());
        var hash = command.Parameters.AddWithValue("$hash", string.Empty);
        command.Parameters.AddWithValue("$now", now);

        foreach (var (sessionId, inputHash, vector) in vectors)
        {
            if (vector.Length == 0)
            {
                continue;
            }

            session.Value = sessionId;
            dim.Value = vector.Length;
            blob.Value = Encode(vector);
            hash.Value = inputHash;

            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>How far a model has got through the sessions worth finding.</summary>
    public EmbeddingCoverage Coverage(string model)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = $"""
            SELECT count(*),
                   count(e.session_id)
            FROM session AS s
            JOIN thread AS t ON t.id = s.thread_id
            LEFT JOIN embedding AS e ON e.session_id = s.id AND e.model = $model
            WHERE {Eligible};
            """;

        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$segmenter", SessionSegmenter.Version);

        using var reader = command.ExecuteReader();
        reader.Read();

        return new EmbeddingCoverage(model, reader.GetInt64(0), reader.GetInt64(1));
    }

    /// <summary>Every model with vectors in the save, and how many.</summary>
    public IReadOnlyList<(string Model, long Count)> Models()
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT model, count(*) FROM embedding GROUP BY model ORDER BY count(*) DESC, model;";

        var models = new List<(string, long)>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            models.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return models;
    }

    /// <summary>
    /// The model search should use: the configured one once it is complete enough, and until then
    /// whichever covers more (ai-plan.md §9.2).
    /// </summary>
    /// <remarks>
    /// Embedding spaces are not comparable, so a ranking never mixes two models — but nothing stops
    /// both existing. Changing the setting keeps search on the old vectors, at full coverage, until
    /// the new model catches up; then it moves across in one step. Reverting the setting halfway makes
    /// the old model configured and active again at once, because its vectors were never touched.
    /// </remarks>
    public string? Active(string? configured)
    {
        var models = Models();

        if (models.Count == 0)
        {
            return null;
        }

        configured = string.IsNullOrWhiteSpace(configured) ? null : configured.Trim();

        if (configured is not null)
        {
            var coverage = Coverage(configured);

            if (coverage.Embedded > 0 && coverage.Fraction >= CompleteEnough)
            {
                return configured;
            }

            var mine = models.FirstOrDefault(m => m.Model == configured).Count;
            var best = models.Where(m => m.Model != configured).OrderByDescending(m => m.Count).FirstOrDefault();

            if (best.Model is not null && best.Count > mine)
            {
                return best.Model;
            }

            return mine > 0 ? configured : null;
        }

        return models[0].Model;
    }

    /// <summary>How many vectors a model has — cheap, and what a cache of them is keyed on.</summary>
    public long Count(string model)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM embedding WHERE model = $model;";
        command.Parameters.AddWithValue("$model", model);

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    /// <summary>Every vector for a model, for brute-force similarity.</summary>
    public IReadOnlyList<(string SessionId, float[] Vector)> Vectors(string model)
    {
        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "SELECT session_id, vector FROM embedding WHERE model = $model;";
        command.Parameters.AddWithValue("$model", model);

        var vectors = new List<(string, float[])>();

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            vectors.Add((reader.GetString(0), Decode((byte[])reader.GetValue(1))));
        }

        return vectors;
    }

    /// <summary>
    /// Deletes the vectors of every model that is neither configured nor in use.
    /// </summary>
    /// <remarks>
    /// Its own action, beside forgetting everything (§9.2), and recoverable by re-embedding — which
    /// is why it needs no typed confirmation.
    /// </remarks>
    /// <returns>How many vectors went.</returns>
    public int ClearUnused(string? configured)
    {
        var keep = new[] { configured?.Trim(), Active(configured) }
            .Where(m => !string.IsNullOrWhiteSpace(m))
            .ToArray();

        using var connection = _database.Open();
        using var command = connection.CreateCommand();

        command.CommandText = "DELETE FROM embedding WHERE model NOT IN (SELECT value FROM json_each($keep));";
        command.Parameters.AddWithValue("$keep", System.Text.Json.JsonSerializer.Serialize(keep));

        return command.ExecuteNonQuery();
    }

    /// <summary>Unit length, then raw float32 bytes.</summary>
    internal static byte[] Encode(float[] vector)
    {
        var norm = MathF.Sqrt(vector.Sum(v => v * v));
        var unit = norm > 0 ? vector.Select(v => v / norm).ToArray() : vector;

        return MemoryMarshal.AsBytes(unit.AsSpan()).ToArray();
    }

    internal static float[] Decode(byte[] blob) => MemoryMarshal.Cast<byte, float>(blob).ToArray();
}
