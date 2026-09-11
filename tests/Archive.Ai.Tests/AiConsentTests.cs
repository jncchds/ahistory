using Archive.Ai.Llm;

namespace Archive.Ai.Tests;

/// <summary>
/// Consent to send archive text is given for one endpoint, and does not travel to another.
/// </summary>
public sealed class AiConsentTests
{
    private static readonly LlmProviderFactory Factory = new();

    private static AiSettings Usable(string endpoint) => new()
    {
        Enabled = true,
        MainModel = "m",
        Endpoint = endpoint,
        DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion,
    };

    [Fact]
    public void Nothing_is_covered_until_the_user_has_said_yes()
    {
        Assert.False(AiConsent.CoversExtraction(Usable("http://localhost:1234/v1"), Factory));
    }

    [Fact]
    public void A_yes_covers_the_endpoint_it_was_given_for()
    {
        var settings = Usable("http://localhost:1234/v1");
        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, Factory);

        Assert.True(AiConsent.CoversExtraction(settings, Factory));
    }

    /// <summary>
    /// Agreeing to a model on your own machine is not agreeing to a hosted API.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is quiet and serious: a user consents once for a local model,
    /// later points the settings at a hosted endpoint to try it, and the background runner starts
    /// sending a decade of correspondence there without anyone having been asked.
    /// </remarks>
    [Fact]
    public void Changing_the_endpoint_makes_a_yes_stale()
    {
        var settings = Usable("http://localhost:1234/v1");
        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, Factory);

        settings.Endpoint = "https://api.example.com/v1";

        Assert.False(AiConsent.CoversExtraction(settings, Factory));
    }

    [Fact]
    public void A_yes_means_nothing_while_ai_is_switched_off()
    {
        var settings = Usable("http://localhost:1234/v1");
        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, Factory);
        settings.Enabled = false;

        Assert.False(AiConsent.CoversExtraction(settings, Factory));
    }

    /// <summary>
    /// A trailing slash, or the lack of one, is not a different endpoint.
    /// </summary>
    [Fact]
    public void The_same_endpoint_written_two_ways_is_the_same_endpoint()
    {
        var settings = Usable("http://localhost:1234/v1");
        settings.ExtractionConfirmedFor = AiConsent.Destination(settings, Factory);

        settings.Endpoint = "http://localhost:1234/v1/";

        Assert.True(AiConsent.CoversExtraction(settings, Factory));
    }

    /// <summary>
    /// Only this machine counts as local; a box on the LAN is still somebody's other computer.
    /// </summary>
    [Fact]
    public void Only_loopback_is_local()
    {
        Assert.True(AiConsent.IsLocal(Usable("http://localhost:1234/v1"), Factory));
        Assert.True(AiConsent.IsLocal(Usable("http://127.0.0.1:1234/v1"), Factory));
        Assert.False(AiConsent.IsLocal(Usable("http://192.168.2.33:1234/v1"), Factory));
        Assert.False(AiConsent.IsLocal(Usable("https://api.openai.com/v1"), Factory));
    }

    /// <summary>A shell variable cannot give consent any more than it can switch AI on.</summary>
    [Fact]
    public void The_environment_cannot_consent()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ahistory-tests", Guid.NewGuid().ToString("N"));

        Environment.SetEnvironmentVariable("AHISTORY_Ai__ExtractionConfirmedFor", "http://localhost:1234/v1/");

        try
        {
            Assert.Equal(string.Empty, new AiSettingsStore(directory).Load().ExtractionConfirmedFor);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AHISTORY_Ai__ExtractionConfirmedFor", null);
        }
    }
}
