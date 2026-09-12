using System.Text;

namespace Archive.Import.IMessage;

/// <summary>
/// The text of a message that Messages stored as an archived <c>NSAttributedString</c>.
/// </summary>
/// <remarks>
/// <para>
/// Since macOS High Sierra the <c>text</c> column is often empty and the words live in
/// <c>attributedBody</c>: an NSArchiver typedstream, which is a serialization format for Objective-C
/// objects and not documented anywhere Apple publishes. Skipping those messages would lose most of
/// a modern archive; guessing at the format would produce text assembled from the wrong bytes.
/// </para>
/// <para>
/// So this reads exactly one shape and refuses everything else (D20): the <c>NSString</c> class
/// name, the typedstream's marker for a C string, a length, and UTF-8 bytes. When the bytes are not
/// that, it says so and the import stops rather than inventing a message body.
/// </para>
/// </remarks>
internal static class TypedStreamText
{
    /// <summary>The class name whose value is the string itself.</summary>
    private static readonly byte[] Marker = "NSString"u8.ToArray();

    /// <summary>
    /// What follows the class name: the typedstream's version byte, two class-table bytes, and
    /// <c>+</c>, which is its type code for a C string.
    /// </summary>
    private static readonly byte[] StringPrefix = [0x01, 0x94, 0x84, 0x01, 0x2B];

    /// <summary>
    /// Reads the message text out of an archived attributed string.
    /// </summary>
    /// <returns>The text, or null when the blob holds no string at all — an empty message is real.</returns>
    /// <exception cref="InvalidDataException">
    /// The blob names an NSString and then does not describe one. That is a format this reader does
    /// not understand, and the archive is better off stopping than storing whatever the bytes
    /// happened to spell.
    /// </exception>
    public static string? Read(byte[] blob)
    {
        ArgumentNullException.ThrowIfNull(blob);

        var start = IndexOf(blob, Marker);

        if (start < 0)
        {
            return null;
        }

        var at = start + Marker.Length;

        foreach (var expected in StringPrefix)
        {
            if (at >= blob.Length)
            {
                throw new InvalidDataException(
                    "An iMessage attributedBody ends immediately after NSString, with no string in it.");
            }

            if (blob[at] != expected)
            {
                throw new InvalidDataException(
                    $"An iMessage attributedBody has 0x{blob[at]:x2} where a typedstream string marker "
                    + $"(0x{expected:x2}) belongs. This reader does not know that shape.");
            }

            at++;
        }

        var length = Length(blob, ref at);

        if (length < 0 || at + length > blob.Length)
        {
            throw new InvalidDataException(
                $"An iMessage attributedBody says its text is {length} bytes, which runs past the end of it.");
        }

        return Encoding.UTF8.GetString(blob, at, length);
    }

    /// <summary>
    /// A typedstream integer: one byte for small values, or a marker saying how many follow.
    /// </summary>
    /// <remarks>
    /// 0x81 and 0x82 introduce a two- and four-byte little-endian value. Anything else in that
    /// range is a shape this has not met, and reading it as a length would take the text from the
    /// wrong offset — which looks like a message rather than like a failure.
    /// </remarks>
    private static int Length(byte[] blob, ref int at)
    {
        if (at >= blob.Length)
        {
            throw new InvalidDataException("An iMessage attributedBody has no length where one belongs.");
        }

        var first = blob[at++];

        switch (first)
        {
            case < 0x80:
                return first;

            case 0x81 when at + 2 <= blob.Length:
                var two = blob[at] | (blob[at + 1] << 8);
                at += 2;
                return two;

            case 0x82 when at + 4 <= blob.Length:
                var four = blob[at] | (blob[at + 1] << 8) | (blob[at + 2] << 16) | (blob[at + 3] << 24);
                at += 4;
                return four;

            default:
                throw new InvalidDataException(
                    $"An iMessage attributedBody has length marker 0x{first:x2}, which this reader does not know.");
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            var found = true;

            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    found = false;
                    break;
                }
            }

            if (found)
            {
                return i;
            }
        }

        return -1;
    }
}
