using System.Text;

namespace Archive.Data.Tests;

/// <summary>
/// §1's guarantee is that the original export bytes survive. Compressing them (decisions.md D17)
/// must not weaken that by a byte.
/// </summary>
public sealed class RawJsonTests
{
    [Fact]
    public void The_original_text_comes_back_exactly()
    {
        const string Json = """
            {"id":1,"type":"message","from":"Марина","text":"привет — всё в порядке 🐢",
             "text_entities":[{"type":"plain","text":"привет"}]}
            """;

        Assert.Equal(Json, RawJson.Decompress(RawJson.Compress(Json)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("плайн текст без структуры")]
    public void Round_trips_whatever_it_is_given(string json) =>
        Assert.Equal(json, RawJson.Decompress(RawJson.Compress(json)) ?? string.Empty);

    [Fact]
    public void Null_stays_null()
    {
        Assert.Null(RawJson.Compress(null));
        Assert.Null(RawJson.Decompress(null));
        Assert.Null(RawJson.Decompress([]));
    }

    /// <summary>
    /// The reason for doing this at all: export JSON is repetitive, and at half a million
    /// messages the difference is hundreds of megabytes.
    /// </summary>
    [Fact]
    public void Export_shaped_json_compresses_substantially()
    {
        var json = string.Join(",", Enumerable.Range(1, 40).Select(i => $$"""
            {"id":{{i}},"type":"message","date":"2019-04-02T18:12:03","date_unixtime":"1554221523",
             "from":"Sam Ruiz","from_id":"user5001","text":"the harbour was freezing",
             "text_entities":[{"type":"plain","text":"the harbour was freezing"}]}
            """));

        var original = Encoding.UTF8.GetByteCount(json);
        var compressed = RawJson.Compress(json)!.Length;

        Assert.True(
            compressed < original / 4,
            $"Expected a large reduction; got {original} to {compressed} bytes.");
    }
}
