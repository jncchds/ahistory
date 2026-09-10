using Archive.Data;
using Microsoft.Data.Sqlite;

namespace Archive.Import;

/// <summary>
/// Works out which source an export folder belongs to, and what to suggest when it is unclear.
/// </summary>
/// <remarks>
/// The suggestion ladder, strongest first:
///
/// 1. The export names its account and a source for that account already exists — extend it. This
///    is the re-export case and it is unambiguous.
/// 2. The export names its account and no such source exists — create it.
/// 3. The export does not name its account, and the save has exactly one source for that
///    platform — suggest extending it. A save holds one person's archive, so a second export from
///    the same platform is almost always the same account. Almost, not certainly, which is why
///    this is a suggestion the user can override.
/// 4. Otherwise — create a source named after the folder, and let the user redirect it.
///
/// Formats that never state their account — Hangouts, VK, QIP, most of the old ones — reach step
/// 3 or 4 every time, which is exactly why the answer is offered rather than assumed.
/// </remarks>
public static class ImportSourceResolver
{
    public static ImportPreview Preview(Database database, string exportFolder, ImporterMatch match)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(match);

        var platform = match.Platform;
        var accountId = match.Detection.AccountId;
        var accountName = match.Detection.AccountName;

        var existing = ExistingSources(database, platform);

        string suggestedId;
        string? suggestedLabel;
        string reason;

        if (accountId is not null)
        {
            suggestedId = AccountSourceId(platform, accountId);
            suggestedLabel = $"{accountName ?? accountId} ({match.DisplayName})";

            reason = existing.Any(s => s.Id == suggestedId)
                ? $"This export belongs to the same account as an existing source ({accountName ?? accountId})."
                : $"This export identifies its account as {accountName ?? accountId}, which is new to this archive.";
        }
        else if (existing.Count == 1)
        {
            suggestedId = existing[0].Id;
            suggestedLabel = existing[0].Label;
            reason =
                $"This export does not name its account, and this archive has one {match.DisplayName} source. "
                + "It is most likely part of that one — change it if this came from someone else.";
        }
        else
        {
            suggestedId = FolderSourceId(platform, exportFolder);
            suggestedLabel = $"{new DirectoryInfo(exportFolder).Name} ({match.DisplayName})";
            reason = existing.Count == 0
                ? "This export does not name its account, so it starts a new source."
                : "This export does not name its account and several sources exist — pick the one it belongs to.";
        }

        var suggestedExists = existing.Any(s => s.Id == suggestedId);
        var (ownerName, ownerAccounts) = Owner(database);

        return new ImportPreview
        {
            ExportFolder = exportFolder,
            Platform = platform,
            PlatformName = match.DisplayName,
            FileCount = match.Detection.FileCount,
            FormatNote = match.Detection.Note,
            DetectedAccountId = accountId,
            DetectedAccountName = accountName,
            OwnerName = ownerName,
            AccountIsNewToOwner =
                ownerName is not null && accountId is not null && !ownerAccounts.Contains(accountId),
            ExistingSources = [.. existing.Select(s => s with { IsSuggested = s.Id == suggestedId })],
            SuggestedSourceId = suggestedId,
            SuggestedSourceExists = suggestedExists,
            SuggestedLabel = suggestedLabel,
            SuggestionReason = reason,
        };
    }

    public static string AccountSourceId(string platform, string accountId) =>
        $"{platform}:account:{accountId}";

    /// <summary>
    /// Last-resort id for an export that will not say who it belongs to.
    /// </summary>
    /// <remarks>
    /// Keyed by folder name rather than full path, so moving the export folder does not silently
    /// create a second source for the same data.
    /// </remarks>
    public static string FolderSourceId(string platform, string exportFolder) =>
        $"{platform}:folder:{new DirectoryInfo(exportFolder).Name}";

    /// <summary>The save's owner and the platform accounts already known to be theirs.</summary>
    private static (string? Name, HashSet<string> Accounts) Owner(Database database)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.display_name, i.source_identity_id
            FROM person p
            LEFT JOIN identity_person ip ON ip.person_id = p.id
            LEFT JOIN identity i ON i.id = ip.identity_id
            WHERE p.is_owner = 1;
            """;

        string? name = null;
        var accounts = new HashSet<string>(StringComparer.Ordinal);

        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            name ??= reader.GetString(0);

            if (!reader.IsDBNull(1))
            {
                accounts.Add(reader.GetString(1));
            }
        }

        return (name, accounts);
    }

    private static List<ImportSourceOption> ExistingSources(Database database, string platform)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();

        // Counts come from message_source, which is the same table the UI's filter reads, so the
        // number shown next to a source is the number of messages choosing it would show.
        command.CommandText = """
            SELECT s.id,
                   s.label,
                   (SELECT count(*) FROM message_source ms WHERE ms.source_id = s.id),
                   (SELECT count(*) FROM import i WHERE i.source_id = s.id),
                   (SELECT max(i.finished_utc) FROM import i WHERE i.source_id = s.id AND i.status = 'completed')
            FROM import_source s
            WHERE s.platform = $platform
            ORDER BY 3 DESC, s.id;
            """;
        command.Parameters.AddWithValue("$platform", platform);

        var sources = new List<ImportSourceOption>();
        using var reader = command.ExecuteReader();

        while (reader.Read())
        {
            sources.Add(new ImportSourceOption(
                reader.GetString(0),
                reader.IsDBNull(1) ? null : reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                IsSuggested: false));
        }

        return sources;
    }
}
