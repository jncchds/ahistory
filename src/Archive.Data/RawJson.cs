using System.IO.Compression;
using System.Text;

namespace Archive.Data;

/// <summary>
/// Compresses the raw export JSON kept alongside every message.
/// </summary>
/// <remarks>
/// <para>
/// §1 keeps each message's original JSON so that a parser gap can be re-run rather than
/// re-requested from the user. Measured on a 495k-message archive it was the single largest thing
/// in the database — 206 MB of 645 MB — which is a lot of disk for something read only when the
/// importer is improved.
/// </para>
/// <para>
/// It compresses extremely well: it is repetitive JSON with the same two dozen keys on every
/// object. Brotli at its fastest level gives most of the benefit for very little time, and the
/// decompression cost is irrelevant because nothing reads this on a normal path — not the
/// conversation view, not search, not the importer's dedupe.
/// </para>
/// <para>
/// The guarantee is unchanged: the bytes are still there, exactly. Only their shape on disk
/// differs.
/// </para>
/// </remarks>
public static class RawJson
{
    public static byte[]? Compress(string? json)
    {
        if (json is null)
        {
            return null;
        }

        var bytes = Encoding.UTF8.GetBytes(json);

        using var output = new MemoryStream(bytes.Length / 4);

        // Fastest, not smallest: this runs once per message during an import, and the difference
        // between levels is a few percent of size against a large multiple of the time.
        using (var brotli = new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            brotli.Write(bytes);
        }

        return output.ToArray();
    }

    public static string? Decompress(byte[]? compressed)
    {
        if (compressed is null || compressed.Length == 0)
        {
            return null;
        }

        using var input = new MemoryStream(compressed);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(brotli, Encoding.UTF8);

        return reader.ReadToEnd();
    }
}
