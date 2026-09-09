using Microsoft.EntityFrameworkCore;

namespace Archive.Data.Tests;

/// <summary>
/// Keeps "the SQL is the authority, EF is the mapper" (decisions.md D3) from quietly becoming
/// "the model drifted six months ago and nobody noticed".
/// </summary>
/// <remarks>
/// A mismatch between an entity and its table does not fail at build time and often does not
/// fail at startup either — it fails on the one query that touches the renamed column, usually
/// in front of a user. Comparing the model against pragma table_info turns that into a test
/// failure at the moment the schema changes.
/// </remarks>
public sealed class EfSchemaTests
{
    private static ArchiveDbContext ContextFor(TempDatabase db)
    {
        var options = new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseSqlite(db.Database.ConnectionString)
            .AddInterceptors(new PragmaConnectionInterceptor())
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options;

        return new ArchiveDbContext(options);
    }

    [Fact]
    public void Every_mapped_column_exists_in_the_sql_schema()
    {
        using var db = new TempDatabase();
        using var context = ContextFor(db);

        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()
                ?? throw new InvalidOperationException($"{entity.DisplayName()} maps to no table.");

            var actual = ColumnsOf(db, table);

            Assert.True(actual.Count > 0, $"Table '{table}' does not exist in the migrated schema.");

            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName();

                Assert.True(
                    actual.Contains(column),
                    $"{entity.DisplayName()}.{property.Name} maps to '{table}.{column}', which does not exist.");
            }
        }
    }

    /// <summary>
    /// The other direction: a column added to a migration but never mapped is invisible to every
    /// EF query, which reads as "the importer isn't saving that field".
    /// </summary>
    [Fact]
    public void Every_column_of_a_mapped_table_is_mapped()
    {
        using var db = new TempDatabase();
        using var context = ContextFor(db);

        foreach (var entity in context.Model.GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            var mapped = entity.GetProperties().Select(p => p.GetColumnName()).ToHashSet(StringComparer.Ordinal);
            var unmapped = ColumnsOf(db, table).Where(c => !mapped.Contains(c)).ToArray();

            Assert.True(
                unmapped.Length == 0,
                $"Table '{table}' has unmapped column(s): {string.Join(", ", unmapped)}.");
        }
    }

    /// <summary>
    /// EnsureCreated would build a schema from the model and silently bypass every migration,
    /// producing a database with no FTS5 table and no triggers that otherwise looks correct.
    /// </summary>
    [Fact]
    public void The_model_alone_does_not_produce_the_search_index()
    {
        using var db = new TempDatabase();
        using var context = ContextFor(db);

        var tables = context.Model.GetEntityTypes().Select(e => e.GetTableName()).ToArray();

        Assert.DoesNotContain("search_fts", tables);
        Assert.DoesNotContain("search_document", tables);
    }

    private static HashSet<string> ColumnsOf(TempDatabase db, string table)
    {
        using var connection = db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}');";

        var columns = new HashSet<string>(StringComparer.Ordinal);
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
