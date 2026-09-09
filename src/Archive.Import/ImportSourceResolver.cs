using System.Globalization;
using System.Text.Json;
using Archive.Data;
using Archive.Import.Telegram;

namespace Archive.Import;

/// <summary>
/// Works out which source an export folder belongs to, and what to suggest when it is unclear.
/// </summary>
/// <remarks>
/// The suggestion ladder, strongest first:
///
/// 1. The export names its account (personal_information.user_id) and a source for that account
///    already exists — extend it. This is the re-export case and it is unambiguous.
/// 2. The export names its account and no such source exists — create it.
/// 3. The export does not name its account (a single-chat export) and the save has exactly one
///    source for this platform — suggest extending it. A save holds one person's archive, so a
///    second Telegram export in it is almost always the same account. Almost, not certainly,
///    which is why this is a suggestion the user can override.
/// 4. Otherwise — create a source named after the folder, and let the user redirect it.
/// </remarks>
public static class ImportSourceResolver
{
    public static ImportPreview Preview(Database database, string exportFolder, string[] resultFiles)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(resultFiles);

        var (accountId, accountName) = DetectAccount(resultFiles);
        var existing = ExistingSources(database, TelegramNormalizer.Platform);

        string suggestedId;
        string? suggestedLabel;
        string reason;

        if (accountId is not null)
        {
            suggestedId = AccountSourceId(accountId);
            suggestedLabel = accountName is null ? null : $"{accountName} (Telegram)";

            reason = existing.Any(s => s.Id == suggestedId)
                ? $"This export belongs to the same account as an existing source ({accountName ?? accountId})."
                : $"This export identifies its account as {accountName ?? accountId}, which is new to this archive.";
        }
        else if (existing.Count == 1)
        {
            suggestedId = existing[0].Id;
            suggestedLabel = existing[0].Label;
            reason =
                "This export does not name its account, and this archive has one Telegram source. "
                + "It is most likely part of that one — change it if this came from someone else.";
        }
        else
        {
            suggestedId = FolderSourceId(exportFolder);
            suggestedLabel = new DirectoryInfo(exportFolder).Name;
            reason = existing.Count == 0
                ? "This export does not name its account, so it starts a new source."
                : "This export does not name its account and several sources exist — pick the one it belongs to.";
        }

        var suggestedExists = existing.Any(s => s.Id == suggestedId);
        var (ownerName, ownerAccounts) = Owner(database);

        return new ImportPreview
        {
            ExportFolder = exportFolder,
            Platform = TelegramNormalizer.Platform,
            FileCount = resultFiles.Length,
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

    public static string AccountSourceId(string accountId) => $"telegram:account:{accountId}";

    /// <summary>
    /// Last-resort id for an export that will not say who it belongs to.
    /// </summary>
    /// <remarks>
    /// Keyed by folder name rather than full path, so moving the export folder does not silently
    /// create a second source for the same data.
    /// </remarks>
    public static string FolderSourceId(string exportFolder) =>
        $"telegram:folder:{new DirectoryInfo(exportFolder).Name}";

    private static (string? Id, string? Name) DetectAccount(string[] resultFiles)
    {
        foreach (var file in resultFiles)
        {
            using var stream = File.OpenRead(file);
            var personal = TelegramExportReader.ReadPersonalInformation(stream);

            if (personal is not { } element)
            {
                continue;
            }

            var id = Text(element, "user_id");

            if (id is null)
            {
                continue;
            }

            var name = string.Join(' ', new[] { Text(element, "first_name"), Text(element, "last_name") }
                .Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            return (id, string.IsNullOrWhiteSpace(name) ? Text(element, "username") : name);
        }

        return (null, null);
    }

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

    private static string? Text(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number when value.TryGetInt64(out var n) => n.ToString(CultureInfo.InvariantCulture),
            _ => null,
        };
    }
}
