using System.Diagnostics;
using Archive.Data;
using Archive.Import.Synthetic;

namespace Archive.Import.Tests;

/// <summary>
/// The archive at a size where the query plans matter.
/// </summary>
/// <remarks>
/// Excluded from a normal run — <c>dotnet test --filter Category!=Perf</c> — because generating
/// and importing this much takes a minute. What it protects is the claim the whole design rests
/// on: that reading a conversation stays fast however far back you scroll, and does not quietly
/// become a table scan when someone adds a column to a query.
/// </remarks>
[Trait("Category", "Perf")]
public sealed class ScaleTests
{
    private const int Messages = 100_000;

    [Fact]
    public void Reading_a_conversation_stays_fast_at_scale()
    {
        using var save = new TempSave();

        var folder = Path.Combine(Path.GetTempPath(), "ahistory-scale", Guid.NewGuid().ToString("N"));

        try
        {
            SyntheticExport.Write(folder, new SyntheticOptions(Messages, Chats: 40));
            save.Runner.Run(folder);

            var queries = new ArchiveQueries(save.Database);
            var conversation = new PersonConversation(save.Database);

            var person = queries.People().Where(p => !p.IsOwner).MaxBy(p => p.MessageCount)!;

            // First page.
            var first = Measure(() => conversation.Page(person.Id));

            // And two thousand messages further back: keyset paging means this is the same work,
            // where an OFFSET would re-read everything it skipped.
            var page = conversation.Page(person.Id);

            for (var i = 0; i < 20 && page.HasMore; i++)
            {
                page = conversation.Page(person.Id, 100, page.NextBeforeUnix, page.NextBeforeId);
            }

            var cursor = page;
            var deep = Measure(() => conversation.Page(person.Id, 100, cursor.NextBeforeUnix, cursor.NextBeforeId));

            Assert.True(first < 100, $"First page took {first:N0} ms.");
            Assert.True(deep < 100, $"Deep page took {deep:N0} ms.");

            // The real assertion: paging deeper is not progressively more expensive.
            Assert.True(
                deep < Math.Max(20, first * 4),
                $"Paging degraded with depth: {first:N0} ms first, {deep:N0} ms deep.");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    [Fact]
    public void Search_stays_usable_at_scale()
    {
        using var save = new TempSave();

        var folder = Path.Combine(Path.GetTempPath(), "ahistory-scale", Guid.NewGuid().ToString("N"));

        try
        {
            SyntheticExport.Write(folder, new SyntheticOptions(Messages, Chats: 40));
            save.Runner.Run(folder);

            var search = new ArchiveSearch(save.Database);

            var common = Measure(() => search.Search("harbour"));
            var rare = Measure(() => search.Search("zeppelin"));
            var cyrillic = Measure(() => search.Search("Праг"));

            // A word in a large share of messages is the worst case, and it is the one that has
            // to stay bearable.
            Assert.True(common < 500, $"Common-word search took {common:N0} ms.");
            Assert.True(rare < 100, $"Rare-word search took {rare:N0} ms.");
            Assert.True(cyrillic < 500, $"Cyrillic search took {cyrillic:N0} ms.");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>Best of several runs: the interest is the floor, not the scheduler's noise.</summary>
    private static double Measure(Action work, int runs = 3)
    {
        work();

        var best = double.MaxValue;

        for (var i = 0; i < runs; i++)
        {
            var stopwatch = Stopwatch.StartNew();
            work();
            stopwatch.Stop();
            best = Math.Min(best, stopwatch.Elapsed.TotalMilliseconds);
        }

        return best;
    }
}
