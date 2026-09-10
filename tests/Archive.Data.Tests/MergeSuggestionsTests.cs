namespace Archive.Data.Tests;

/// <summary>
/// Finding the pairs worth merging, without ever merging one.
/// </summary>
/// <remarks>
/// §1 says auto-matching on names produces wrong merges, and the plan declined to do it: an
/// under-merge is untidy and one click to fix, an over-merge is a confident lie that §7's
/// knowledge base cannot detect. Everything here therefore proposes; <see cref="IdentityMerger"/>
/// is the only thing that writes.
/// </remarks>
public sealed class MergeSuggestionsTests
{
    private static (TempDatabase Db, MergeSuggestions Suggestions) Fixture()
    {
        var db = new TempDatabase();
        Seed.Basics(db);
        Seed.People(db);

        return (db, new MergeSuggestions(db.Database));
    }

    /// <summary>Adds an account on another platform with a person of its own, as an import would.</summary>
    private static void AddAccount(
        TempDatabase db,
        string identityId,
        string platform,
        string sourceId,
        string displayName,
        string? handle = null,
        bool synthetic = false)
    {
        var personId = "p:" + identityId;
        var handleSql = handle is null ? "NULL" : $"'{handle}'";
        var sourceSql = sourceId.Length == 0 ? "NULL" : $"'{sourceId}'";

        db.Execute($"""
            INSERT INTO identity (id, platform, source_identity_id, handle, display_name, is_synthetic,
                                  first_import_id, created_utc)
            VALUES ('{identityId}', '{platform}', {sourceSql}, {handleSql}, '{displayName}',
                    {(synthetic ? 1 : 0)}, '{Seed.ImportId}', '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO person (id, display_name, is_owner, created_utc)
            VALUES ('{personId}', '{displayName}', 0, '2020-01-01T00:00:00.0000000+00:00');

            INSERT INTO identity_person (identity_id, person_id, confidence, linked_utc)
            VALUES ('{identityId}', '{personId}', 'auto', '2020-01-01T00:00:00.0000000+00:00');
            """);
    }

    [Fact]
    public void The_same_name_on_two_platforms_is_offered()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        AddAccount(db, "idn-vk-sam", "vk", "222", "Sam");

        var suggestion = Assert.Single(suggestions.Candidates());

        Assert.Equal([Seed.IdentityId, "idn-vk-sam"], new[] { suggestion.LeftIdentityId, suggestion.RightIdentityId }.Order());
        Assert.Contains("Telegram", suggestion.Reason, StringComparison.Ordinal);
        Assert.Contains("VKontakte", suggestion.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Two accounts on one platform sharing a name are two people, not one.
    /// </summary>
    /// <remarks>
    /// Telegram will not let two accounts share a username, and a display-name collision there is
    /// a coincidence. Offering it would train the user to click through the list without reading.
    /// </remarks>
    [Fact]
    public void Two_accounts_on_the_same_platform_with_one_name_are_not_offered()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        AddAccount(db, "idn-tg-sam2", "telegram", "9999", "Sam");

        Assert.Empty(suggestions.Candidates());
    }

    /// <summary>
    /// A name with no account behind it is the exception: it is a guess by construction (§2).
    /// </summary>
    [Fact]
    public void A_name_only_identity_is_offered_against_the_real_account_of_that_name()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        AddAccount(db, "idn-tg-name", "telegram", string.Empty, "Sam", synthetic: true);

        var suggestion = Assert.Single(suggestions.Candidates());

        Assert.Contains("no account behind it", suggestion.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_shared_handle_outranks_a_shared_name()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        db.Execute($"UPDATE identity SET handle = 'samr' WHERE id = '{Seed.IdentityId}';");

        AddAccount(db, "idn-vk-handle", "vk", "222", "S. Ruiz", handle: "samr");
        AddAccount(db, "idn-hangouts-sam", "hangouts", "333", "Sam");

        var candidates = suggestions.Candidates();

        Assert.Equal(2, candidates.Count);
        Assert.Contains("handle", candidates[0].Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The cap keeps the strongest pairs, not the busiest ones.
    /// </summary>
    /// <remarks>
    /// The ranking and the LIMIT have to agree. Ordering by message volume and re-sorting by
    /// strength afterwards cuts a strong pair with few messages before it is ever ranked — which
    /// is precisely the suggestion most worth showing, because a quiet duplicate account is the
    /// one a person would never find by eye.
    /// </remarks>
    [Fact]
    public void A_strong_pair_survives_the_cap_even_with_nothing_said_in_it()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        // A quiet cross-platform match.
        AddAccount(db, "idn-vk-sam", "vk", "222", "Sam");

        // And a noisier same-platform one, which is the weaker kind of evidence.
        AddAccount(db, "idn-tg-name", "telegram", string.Empty, "Sam", synthetic: true);
        Seed.MessageFrom(db, "tg/100/2", "lots to say", "idn-tg-name", 1577880001);
        Seed.MessageFrom(db, "tg/100/3", "and more", "idn-tg-name", 1577880002);

        var best = suggestions.Candidates(limit: 1);

        var only = Assert.Single(best);

        Assert.Contains("VKontakte", only.Reason, StringComparison.Ordinal);
    }

    /// <summary>Accounts already on one person have nothing to suggest.</summary>
    [Fact]
    public void Accounts_already_on_one_person_are_not_offered()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        AddAccount(db, "idn-vk-sam", "vk", "222", "Sam");

        new IdentityMerger(db.Database).MergeInto("idn-vk-sam", Seed.SamPersonId);

        Assert.Empty(suggestions.Candidates());
    }

    /// <summary>
    /// A pair the user has turned down stays turned down.
    /// </summary>
    /// <remarks>
    /// A list that keeps re-offering what it has been told is wrong is one people stop reading —
    /// and the pairs it is most confident about are the ones it would put back at the top.
    /// </remarks>
    [Fact]
    public void A_dismissed_pair_is_not_offered_again()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        AddAccount(db, "idn-vk-sam", "vk", "222", "Sam");

        suggestions.Dismiss(Seed.IdentityId, "idn-vk-sam");

        Assert.Empty(suggestions.Candidates());

        // And the answer can be taken back, for someone who changed their mind.
        suggestions.Restore(Seed.IdentityId, "idn-vk-sam");

        Assert.Single(suggestions.Candidates());
    }

    /// <summary>The pair is one pair whichever way round it is dismissed.</summary>
    [Fact]
    public void Dismissing_a_pair_the_other_way_round_is_the_same_pair()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        AddAccount(db, "idn-vk-sam", "vk", "222", "Sam");

        suggestions.Dismiss("idn-vk-sam", Seed.IdentityId);

        Assert.Empty(suggestions.Candidates());
    }

    /// <summary>A suggestion involving the owner says so, because accepting it needs confirming.</summary>
    [Fact]
    public void A_pair_that_touches_the_owner_is_flagged()
    {
        var (db, suggestions) = Fixture();
        using var _ = db;

        AddAccount(db, "idn-vk-owner", "vk", "1", "Owner Synthetic");

        Assert.True(Assert.Single(suggestions.Candidates()).TouchesOwner);
    }
}
