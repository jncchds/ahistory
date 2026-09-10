using System.Globalization;
using System.Text.Json;

namespace Archive.Import.Telegram;

/// <summary>
/// Telegram Desktop's JSON export (§2).
/// </summary>
/// <remarks>
/// The reference importer: the one with a real format specification behind it, the one every §2
/// trap was found in, and the one the schema was designed around. The others are measured against
/// how much of it they can reuse unchanged.
/// </remarks>
public sealed class TelegramImporter : IPlatformImporter
{
    public string Platform => TelegramNormalizer.Platform;

    public string DisplayName => "Telegram";

    public ImportDetection Detect(string path)
    {
        if (!Directory.Exists(path))
        {
            return ImportDetection.No;
        }

        var files = ResultFiles(path);

        if (files.Length == 0)
        {
            return ImportDetection.No;
        }

        // result.json is Telegram's own filename, so the name alone is a strong signal; reading
        // personal_information confirms it and names the account in one pass of a few kilobytes.
        var (accountId, accountName) = DetectAccount(files);

        return new ImportDetection(
            ImportConfidence.Certain, accountId, accountName, files.Length);
    }

    public void Read(string path, IImportSink sink, ImportOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sink);

        var files = ResultFiles(path);

        if (files.Length == 0)
        {
            throw new InvalidDataException(
                $"'{path}' contains no result.json. Export from Telegram Desktop as JSON, not HTML (§2).");
        }

        var adapter = new SinkAdapter(sink);

        foreach (var file in files)
        {
            using var stream = File.OpenRead(file);
            TelegramExportReader.Read(stream, adapter);
        }
    }

    /// <summary>
    /// The export's JSON files, in the order Telegram numbers them.
    /// </summary>
    /// <remarks>
    /// Ordinal sort on the name would be wrong for result10.json, so the numeric suffix is
    /// compared as a number. Messages are keyed by uid, so order does not affect correctness —
    /// but it does affect which run is recorded as discovering a row.
    /// </remarks>
    internal static string[] ResultFiles(string folder) =>
        [.. Directory.EnumerateFiles(folder, "result*.json")
            .OrderBy(SuffixNumber)
            .ThenBy(Path.GetFileName, StringComparer.Ordinal)];

    private static int SuffixNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = name.AsSpan("result".Length);

        return digits.Length > 0 && int.TryParse(digits, out var n) ? n : 1;
    }

    private static (string? Id, string? Name) DetectAccount(string[] files)
    {
        foreach (var file in files)
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

    internal static string? Text(JsonElement element, string property)
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

    /// <summary>Bridges the Telegram-shaped reader onto the platform-neutral sink.</summary>
    private sealed class SinkAdapter(IImportSink sink) : ITelegramExportSink
    {
        public void OnPersonalInformation(JsonElement element)
        {
            var userId = Text(element, "user_id");

            if (userId is null)
            {
                return;
            }

            var name = string.Join(' ', new[]
            {
                Text(element, "first_name"),
                Text(element, "last_name"),
            }.Where(s => !string.IsNullOrWhiteSpace(s))).Trim();

            if (string.IsNullOrWhiteSpace(name))
            {
                name = Text(element, "username") ?? userId;
            }

            sink.OnOwner(new NormalizedIdentity(
                TelegramNormalizer.Platform, userId, Text(element, "username"), name, IsSynthetic: false));
        }

        public void OnChat(TelegramChatHeader chat) =>
            sink.OnThread(new NormalizedThread(chat.SourceThreadId, chat.ThreadKind, chat.Name));

        public void OnMessage(TelegramChatHeader chat, JsonElement message) =>
            sink.OnMessage(
                new NormalizedThread(chat.SourceThreadId, chat.ThreadKind, chat.Name),
                TelegramNormalizer.Normalize(chat, message));
    }
}
