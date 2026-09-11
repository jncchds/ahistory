using System.Security.Cryptography;
using System.Text;
using Archive.Ai.Extraction;

namespace Archive.Ai.Tests;

/// <summary>
/// Prompts are pinned the way migrations are, and for the same reason.
/// </summary>
/// <remarks>
/// <para>
/// Every derived row records the <c>prompt_version</c> that produced it, and §6.6 wants "re-run
/// only what was produced with prompt &lt; v4" to be a one-line query. That is only true while a
/// version means one text: edit a prompt without bumping its version and "processed with v1" now
/// describes two different sets of instructions, with nothing anywhere able to tell them apart —
/// and no way to find the rows that need redoing.
/// </para>
/// <para>
/// <b>Adding a prompt adds a line here. Changing a shipped one is the mistake this exists to
/// catch</b> — the fix is a new version, never an edit under the old number. Updating a hash to
/// make this pass is the same mistake with an extra step.
/// </para>
/// </remarks>
public sealed class PromptTests
{
    private static readonly Dictionary<string, string> Released = new(StringComparer.Ordinal)
    {
        ["extract.session@1"] = "07650420dfd1b3e8e36ea5ee395f2a3cce01aea8cc924a04cf03c6ccc19f380a",
        ["adjudicate.fact@1"] = "5cee4e6e448ec4cd5b65a8a1557f07f436b2efe8f392f8821f89056458613886",
        ["diary.window@1"] = "820f3802aa361540b959992b296cdac5e819ef0289648e35c7d0f7c0d42e77c6",
        ["rollup.year@1"] = "ea316c6946be5975cd8d9632aab260ea40d3e40c740b606d167b32140807ada9",
        ["rollup.profile@1"] = "819dd951a129e8d8f22a6cf6e8e7b89f91cd774297b2b1de91bbc58d91583ff9",
        ["ocr.image@1"] = "15b878432c9452c5f91ccb2ed6fed9031d0edb951fc3857287c79d3d6b2e2f9a",
    };

    [Fact]
    public void No_prompt_that_has_shipped_has_been_edited()
    {
        foreach (var (name, version, text) in PromptCatalog.All())
        {
            if (!Released.TryGetValue($"{name}@{version}", out var expected))
            {
                // A genuinely new prompt or version. Add it above, with this hash.
                continue;
            }

            Assert.Equal(expected, Sha256(text));
        }
    }

    [Fact]
    public void No_prompt_that_has_shipped_has_been_withdrawn()
    {
        var present = PromptCatalog.All().Select(p => $"{p.Name}@{p.Version}").ToHashSet(StringComparer.Ordinal);

        foreach (var key in Released.Keys)
        {
            Assert.Contains(key, present);
        }
    }

    /// <summary>
    /// The extraction prompt says the things the tool set depends on it saying.
    /// </summary>
    /// <remarks>
    /// A reworded prompt is fine; a prompt that has quietly lost its instruction to cite, or its
    /// permission to record nothing, is a different pipeline with the same version number. These
    /// are the two that change behaviour rather than tone.
    /// </remarks>
    [Fact]
    public void The_extraction_prompt_keeps_its_load_bearing_instructions()
    {
        var text = PromptCatalog.TextOf(PromptCatalog.ExtractSession);

        Assert.Contains(FactTools.NothingToRecord, text, StringComparison.Ordinal);
        Assert.Contains("message ids", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("confidence", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Asking_for_a_prompt_that_does_not_exist_says_so()
    {
        Assert.Throws<ArgumentException>(() => PromptCatalog.VersionOf("no.such.prompt"));
    }

    private static string Sha256(string text) =>
        Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(text.ReplaceLineEndings("\n"))));
}
