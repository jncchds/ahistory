namespace Archive.Ai.Tests;

public sealed class AiSettingsTests
{
    [Fact]
    public void A_blank_utility_model_falls_back_to_the_main_one()
    {
        var settings = new AiSettings { MainModel = "big", UtilityModel = "   " };

        Assert.Equal("big", settings.ModelFor(AiWorkKind.Utility));
        Assert.Equal("big", settings.ModelFor(AiWorkKind.Main));

        settings.UtilityModel = "small";

        Assert.Equal("small", settings.ModelFor(AiWorkKind.Utility));
        Assert.Equal("big", settings.ModelFor(AiWorkKind.Main));
    }

    /// <summary>
    /// Switched off, nothing else about the configuration matters.
    /// </summary>
    /// <remarks>
    /// P1: refusing to start because a field describing a feature the user never enabled is blank
    /// would be the exact inversion of "the archive works without any of this".
    /// </remarks>
    [Fact]
    public void A_disabled_configuration_is_never_invalid()
    {
        var settings = new AiSettings { Enabled = false, MainModel = "", Endpoint = "not a url" };

        settings.Validate();
    }

    [Fact]
    public void Enabling_without_a_model_is_refused_by_name()
    {
        var settings = new AiSettings { Enabled = true };

        var error = Assert.Throws<InvalidOperationException>(settings.Validate);

        Assert.Contains("MainModel", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_endpoint_that_is_not_an_http_url_is_refused()
    {
        var settings = new AiSettings { Enabled = true, MainModel = "m", Endpoint = "file:///models" };

        Assert.Throws<InvalidOperationException>(settings.Validate);

        settings.Endpoint = "http://192.168.2.33:1234/v1";
        settings.Validate();
    }

    /// <summary>
    /// Enabled is not the same as ready.
    /// </summary>
    /// <remarks>
    /// Both halves matter: a half-filled form that is switched on must not start a background
    /// drain against a blank model name, and consent to a disclaimer the user has not seen the
    /// current wording of is not consent.
    /// </remarks>
    [Fact]
    public void A_configuration_is_usable_only_once_it_is_complete_and_acknowledged()
    {
        var settings = new AiSettings { Enabled = true, MainModel = "m" };

        Assert.False(settings.IsUsable);

        settings.DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion;

        Assert.True(settings.IsUsable);

        settings.MainModel = "  ";

        Assert.False(settings.IsUsable);
    }

    [Fact]
    public void An_older_acknowledgement_does_not_cover_a_reworded_disclaimer()
    {
        var settings = new AiSettings
        {
            Enabled = true,
            MainModel = "m",
            DisclaimerAcknowledgedVersion = AiSettings.CurrentDisclaimerVersion - 1,
        };

        Assert.False(settings.DisclaimerIsCurrent);
        Assert.False(settings.IsUsable);
    }
}
