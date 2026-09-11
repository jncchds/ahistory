namespace Archive.Ai.Tests;

/// <summary>
/// The settings file: where it is, what survives a round trip, and what it refuses to do.
/// </summary>
public sealed class AiSettingsStoreTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

    private AiSettingsStore Store() => new(_directory);

    [Fact]
    public void Settings_survive_a_round_trip()
    {
        var store = Store();

        store.Save(new AiSettings
        {
            Enabled = true,
            Provider = LlmProviderKind.Ollama,
            Endpoint = "http://192.168.2.33:1234/v1",
            ApiKey = "sk-abc",
            MainModel = "google/gemma-4-31b",
            EmbeddingModel = "text-embedding-nomic-embed-text-v1.5",
            OutputLanguage = "Russian",
            RecordPromptBodies = true,
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
        });

        var loaded = Store().Load();

        Assert.True(loaded.Enabled);
        Assert.Equal(LlmProviderKind.Ollama, loaded.Provider);
        Assert.Equal("http://192.168.2.33:1234/v1", loaded.Endpoint);
        Assert.Equal("sk-abc", loaded.ApiKey);
        Assert.Equal("google/gemma-4-31b", loaded.MainModel);
        Assert.Equal("Russian", loaded.OutputLanguage);
        Assert.True(loaded.RecordPromptBodies);
        Assert.True(loaded.DisclaimerIsCurrent);
    }

    [Fact]
    public void A_missing_file_is_defaults_and_not_an_error()
    {
        var settings = Store().Load();

        Assert.False(settings.Enabled);
        Assert.Equal("English", settings.OutputLanguage);
    }

    /// <summary>
    /// A corrupt file is defaults too.
    /// </summary>
    /// <remarks>
    /// P1 again: the archive must open. Refusing to start because a file describing an optional
    /// feature has a stray comma in it would make that feature load-bearing.
    /// </remarks>
    [Fact]
    public void A_corrupt_file_falls_back_to_defaults_rather_than_throwing()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(Path.Combine(_directory, "ai.json"), "{ this is not json");

        var settings = Store().Load();

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void Saving_over_an_existing_file_replaces_it()
    {
        var store = Store();

        store.Save(new AiSettings { MainModel = "first" });
        store.Save(new AiSettings { MainModel = "second" });

        Assert.Equal("second", Store().Load().MainModel);
        Assert.False(File.Exists(Path.Combine(_directory, "ai.json.tmp")));
    }

    [Fact]
    public void An_environment_variable_overrides_the_file()
    {
        Store().Save(new AiSettings { MainModel = "from-file" });

        Environment.SetEnvironmentVariable("AHISTORY_Ai__MainModel", "from-environment");

        try
        {
            Assert.Equal("from-environment", Store().Load().MainModel);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AHISTORY_Ai__MainModel", null);
        }
    }

    /// <summary>
    /// Nothing in the environment can switch AI on.
    /// </summary>
    /// <remarks>
    /// Enabling it is a consent step — the user reads what it means and says yes. A consent step
    /// that a variable in a shell profile can perform on their behalf is not one, and this is the
    /// setting whose whole job is to be deliberate.
    /// </remarks>
    [Fact]
    public void The_environment_cannot_enable_ai()
    {
        Environment.SetEnvironmentVariable("AHISTORY_Ai__Enabled", "true");

        try
        {
            Assert.False(Store().Load().Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AHISTORY_Ai__Enabled", null);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
