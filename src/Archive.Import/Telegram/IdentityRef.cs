namespace Archive.Import.Telegram;

/// <summary>Raised when an export contains an identity prefix the importer does not know.</summary>
/// <remarks>
/// This is deliberately fatal rather than best-effort. A prefix names a *class* of identity, and
/// an unrecognized one means Telegram introduced a kind of participant nobody has looked at yet.
/// Treating the whole string as an id would silently create a parallel population of identities
/// that never merge with the real ones — the sort of corruption that is only noticed months later
/// when someone's history is mysteriously split in two.
/// </remarks>
public sealed class UnknownIdentityPrefixException(string value)
    : Exception($"Unrecognized Telegram identity prefix in '{value}'. "
              + "This is a new kind of participant — teach IdentityRef about it rather than guessing.")
{
    public string Value { get; } = value;
}

/// <summary>The kind of thing an identity refers to.</summary>
public enum IdentityKind
{
    User,
    Channel,
    Chat,
}

/// <summary>
/// A parsed <c>from_id</c> / <c>actor_id</c> value.
/// </summary>
/// <remarks>
/// §2: these arrive prefixed — <c>user123</c>, <c>channel456</c>. The prefix is stripped so that
/// the same person referenced from different export versions lands on one identity row.
/// </remarks>
public readonly record struct IdentityRef(IdentityKind Kind, string Id)
{
    private static readonly (string Prefix, IdentityKind Kind)[] Prefixes =
    [
        ("user", IdentityKind.User),
        ("channel", IdentityKind.Channel),
        ("chat", IdentityKind.Chat),
    ];

    public static IdentityRef Parse(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        foreach (var (prefix, kind) in Prefixes)
        {
            if (value.StartsWith(prefix, StringComparison.Ordinal) && IsDigits(value.AsSpan(prefix.Length)))
            {
                return new IdentityRef(kind, value[prefix.Length..]);
            }
        }

        // Older exports write a bare number for a user.
        if (IsDigits(value))
        {
            return new IdentityRef(IdentityKind.User, value);
        }

        throw new UnknownIdentityPrefixException(value);
    }

    // Deliberately no TryParse. An "unknown prefixes are fine here" overload is exactly how the
    // guarantee above gets quietly opted out of, one call site at a time.

    private static bool IsDigits(ReadOnlySpan<char> span)
    {
        if (span.IsEmpty)
        {
            return false;
        }

        foreach (var c in span)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    public override string ToString() => Id;
}
