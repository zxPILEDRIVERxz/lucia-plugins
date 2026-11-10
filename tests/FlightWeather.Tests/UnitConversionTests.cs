namespace FlightWeather.Tests;

/// <summary>
/// NWS normalizes station-reported wind into WMO units via `unitCode` (US stations report
/// knots or mph in the raw METAR but the API serves km/h, m/s, etc.), so every value must
/// be converted to mph. Unknown codes must yield null — never a guess.
/// </summary>
public sealed class UnitConversionTests
{
    [Theory]
    // Real probe values: KOWB served 7.56 km/h for "20004KT" (4 kt); KLOU served 11.16 km/h for 6 kt.
    [InlineData(7.56, "wmoUnit:km_h-1", 4.70)]
    [InlineData(11.16, "wmoUnit:km_h-1", 6.93)]
    [InlineData(4.0, "wmoUnit:kn", 4.60)]
    [InlineData(10.0, "wmoUnit:m_s-1", 22.37)]
    [InlineData(12.0, "wmoUnit:mph", 12.0)]
    public void ToMph_ConvertsKnownUnits(double value, string unitCode, double expectedMph)
    {
        Assert.Equal(expectedMph, FlightWeatherCore.ToMph(value, unitCode)!.Value, precision: 2);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wmoUnit:bogus_unit")]
    public void ToMph_UnknownOrMissingUnit_ReturnsNull(string? unitCode)
    {
        Assert.Null(FlightWeatherCore.ToMph(10.0, unitCode));
    }
}
