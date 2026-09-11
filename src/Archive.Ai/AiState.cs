namespace Archive.Ai;

/// <summary>
/// The settings as they currently stand, and a way to be told when they change.
/// </summary>
/// <remarks>
/// <para>
/// Settings are read once at startup and then held here, because half a dozen places need to know
/// whether AI is on — the rail, the statistics page, and eventually the background runner — and
/// re-reading a file on every question is both slower and capable of giving two different answers
/// in the same frame.
/// </para>
/// <para>
/// <see cref="Changed"/> is what makes switching AI off take effect immediately: pages disappear
/// from the rail rather than waiting for a restart, which is the difference between a feature
/// being optional and a feature claiming to be.
/// </para>
/// </remarks>
public sealed class AiState
{
    private readonly AiSettingsStore _store;

    public AiState(AiSettingsStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        Current = store.Load();
    }

    /// <summary>Raised after <see cref="Current"/> has been replaced.</summary>
    public event EventHandler? Changed;

    public AiSettings Current { get; private set; }

    /// <summary>Where the file is, so the UI can tell a user where to look.</summary>
    public string SettingsPath => _store.Path;

    /// <summary>Persists the settings and tells everyone.</summary>
    public void Update(AiSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings.Validate();

        _store.Save(settings);

        Current = settings;

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
