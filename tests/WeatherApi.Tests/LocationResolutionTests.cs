namespace WeatherApi.Tests;

/// <summary>
/// Default location resolution, tested against the real resolver in
/// plugins/weather-api/core.cs. Precedence: place named in the question >
/// configured default override > automatic Home Assistant home coordinates
/// (when enabled and usable).
/// </summary>
public sealed class LocationResolutionTests
{
    [Fact]
    public void Resolve_RequestedLocation_WinsOverEverything()
    {
        var home = new WeatherHomeLocation(38.9, -77.0, "Washington");

        Assert.Equal("Tokyo", WeatherQueryResolver.Resolve("  Tokyo ", "Washington, DC", true, home));
    }

    [Fact]
    public void Resolve_DefaultOverride_BeatsAutomaticHome()
    {
        var home = new WeatherHomeLocation(38.9, -77.0, "Washington");

        Assert.Equal("Washington, DC", WeatherQueryResolver.Resolve(null, "Washington, DC", true, home));
    }

    [Fact]
    public void Resolve_AutomaticHome_UsedWhenEnabledAndNoOverride()
    {
        var home = new WeatherHomeLocation(38.9072, -77.0369, "Washington");

        Assert.Equal("38.9072, -77.0369", WeatherQueryResolver.Resolve(null, "", true, home));
    }

    [Fact]
    public void Resolve_GateDisabled_ReturnsNullEvenWithHome()
    {
        var home = new WeatherHomeLocation(38.9, -77.0, "Washington");

        Assert.Null(WeatherQueryResolver.Resolve(null, "", false, home));
    }

    [Fact]
    public void Resolve_ZeroOrMissingCoordinates_Ignored()
    {
        var origin = new WeatherHomeLocation(0, 0, "Nowhere");

        Assert.Null(WeatherQueryResolver.Resolve(null, "", true, origin));
        Assert.Null(WeatherQueryResolver.Resolve(null, null, true, null));
    }

    [Fact]
    public void Resolve_BlankValues_TreatedAsAbsent()
    {
        var home = new WeatherHomeLocation(38.9, -77.0, "Washington");

        Assert.Equal("38.9, -77", WeatherQueryResolver.Resolve("   ", null, true, home));
    }
}
