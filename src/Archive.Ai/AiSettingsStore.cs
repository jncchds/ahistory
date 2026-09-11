using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Archive.Ai;

/// <summary>
/// Reads and writes <see cref="AiSettings"/> as a JSON file beside the user's other configuration.
/// </summary>
/// <remarks>
/// <para>
/// One file per machine, shared by every save — see <see cref="AiSettings"/> for why it is not in
/// the archive. Environment variables prefixed <c>AHISTORY_Ai__</c> override the file, matching how
/// the rest of the app resolves configuration.
/// </para>
/// <para>
/// The file is written whole, through a temporary file and a replace, because a settings file
/// truncated by a crash mid-write is a first-run experience nobody can diagnose.
/// </para>
/// </remarks>
public sealed class AiSettingsStore
{
    private const string FileName = "ai.json";
    private const string EnvironmentPrefix = "AHISTORY_" + AiSettings.SectionName + "__";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly ILogger _log;

    public AiSettingsStore(string? directory = null, ILogger<AiSettingsStore>? logger = null)
    {
        Directory = directory ?? DefaultDirectory;
        Path = System.IO.Path.Combine(Directory, FileName);
        _log = logger ?? NullLogger<AiSettingsStore>.Instance;
    }

    /// <summary>Where the file lives when nothing says otherwise — beside the logs directory.</summary>
    public static string DefaultDirectory => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ahistory");

    public string Directory { get; }

    public string Path { get; }

    /// <summary>
    /// The settings as they currently stand, or defaults if there is no file.
    /// </summary>
    /// <remarks>
    /// A corrupt file returns defaults rather than throwing. AI is optional by design, and
    /// refusing to open an archive because a file describing a feature the user may never touch
    /// has a stray comma in it would be the exact inversion of P1.
    /// </remarks>
    public AiSettings Load()
    {
        var settings = ReadFile() ?? new AiSettings();

        ApplyEnvironment(settings);

        return settings;
    }

    public void Save(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        System.IO.Directory.CreateDirectory(Directory);

        var temporary = Path + ".tmp";

        File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));

        // Replace rather than Move: Move throws when the destination exists on some platforms,
        // and deleting first leaves a window where the settings file does not exist at all.
        if (File.Exists(Path))
        {
            File.Replace(temporary, Path, destinationBackupFileName: null);
        }
        else
        {
            File.Move(temporary, Path);
        }

        // No values: an endpoint is a URL the user chose, but a key is not, and a log that names
        // one field of this file invites the next one to name another (P6).
        _log.LogInformation("AI settings saved. Enabled {Enabled}.", settings.Enabled);
    }

    private AiSettings? ReadFile()
    {
        if (!File.Exists(Path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<AiSettings>(File.ReadAllText(Path), Json);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _log.LogWarning(ex, "AI settings could not be read; using defaults.");

            return null;
        }
    }

    /// <summary>
    /// Applies <c>AHISTORY_Ai__*</c> overrides.
    /// </summary>
    /// <remarks>
    /// Written out rather than bound by reflection: the list of things an environment variable may
    /// change is a decision, and a reflective binder silently extends it every time a property is
    /// added. Nothing here can switch AI on — enabling it is a consent step (§2.3), and a consent
    /// step that an environment variable can perform is not one.
    /// </remarks>
    private static void ApplyEnvironment(AiSettings settings)
    {
        Set(nameof(AiSettings.Endpoint), value => settings.Endpoint = value);
        Set(nameof(AiSettings.ApiKey), value => settings.ApiKey = value);
        Set(nameof(AiSettings.MainModel), value => settings.MainModel = value);
        Set(nameof(AiSettings.UtilityModel), value => settings.UtilityModel = value);
        Set(nameof(AiSettings.EmbeddingModel), value => settings.EmbeddingModel = value);
        Set(nameof(AiSettings.OutputLanguage), value => settings.OutputLanguage = value);

        Set(nameof(AiSettings.Provider), value =>
        {
            if (Enum.TryParse<LlmProviderKind>(value, ignoreCase: true, out var kind))
            {
                settings.Provider = kind;
            }
        });

        SetInt(nameof(AiSettings.TimeoutMs), value => settings.TimeoutMs = value);
        SetInt(nameof(AiSettings.MaxRetries), value => settings.MaxRetries = value);
        SetInt(nameof(AiSettings.MaxParallelCalls), value => settings.MaxParallelCalls = value);

        static void Set(string name, Action<string> apply)
        {
            var value = Environment.GetEnvironmentVariable(EnvironmentPrefix + name);

            if (!string.IsNullOrWhiteSpace(value))
            {
                apply(value.Trim());
            }
        }

        static void SetInt(string name, Action<int> apply) =>
            Set(name, value =>
            {
                if (int.TryParse(value, out var parsed))
                {
                    apply(parsed);
                }
            });
    }
}
