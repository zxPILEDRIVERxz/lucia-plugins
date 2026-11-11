namespace HouseBriefing.Tests;

/// <summary>
/// Today's weather, one compact line from Open-Meteo /v1/forecast daily arrays.
/// The line carries no trailing period — the briefing assembler owns sentence endings.
/// Precipitation wording: &gt;=60% "rain/snow likely", 30-59% "some chance of rain/snow",
/// &lt;30% (or unknown) "mostly dry"; snow is guessed when the high is below freezing
/// (28 °F / 0 °C). Wind: mph in Fahrenheit mode, km/h in Celsius mode.
/// </summary>
public sealed class WeatherFormatTests
{
    private static string DailyJson(double? tmax, double? tmin, int? prob, double? wind) =>
        "{\"daily\":{\"temperature_2m_max\":[" + Num(tmax) +
        "],\"temperature_2m_min\":[" + Num(tmin) +
        "],\"precipitation_probability_max\":[" + Num(prob) +
        "],\"wind_speed_10m_max\":[" + Num(wind) + "]}}";

    private static string? Num(double? v) => v is null ? "null" : v.Value.ToString("0.0");
    private static string? Num(int? v) => v is null ? "null" : v.Value.ToString();

    [Fact]
    public void FullFahrenheitLine()
    {
        var json = DailyJson(72.1, 54.3, 15, 18.2);

        var forecast = HouseBriefingCore.ParseWeatherDaily(json, "F");

        Assert.NotNull(forecast);
        Assert.Equal("high 72°, low 54°, mostly dry, wind up to 18 mph",
            HouseBriefingCore.FormatWeatherLine(forecast!));
    }

    [Fact]
    public void CelsiusSnowLine()
    {
        var json = DailyJson(-1.2, -6.4, 70, 31.1);

        var forecast = HouseBriefingCore.ParseWeatherDaily(json, "C");

        Assert.NotNull(forecast);
        Assert.Equal("high -1°, low -6°, snow likely, wind up to 31 km/h",
            HouseBriefingCore.FormatWeatherLine(forecast!));
    }

    [Fact]
    public void SomeChanceWithoutWind()
    {
        var json = DailyJson(55, 40, 45, null);

        var forecast = HouseBriefingCore.ParseWeatherDaily(json, "F");

        Assert.NotNull(forecast);
        Assert.Equal("high 55°, low 40°, some chance of rain",
            HouseBriefingCore.FormatWeatherLine(forecast!));
    }

    [Fact]
    public void UnknownProbabilityOmitsTheWetClause()
    {
        var json = DailyJson(60, 45, null, 12);

        var forecast = HouseBriefingCore.ParseWeatherDaily(json, "F");

        Assert.NotNull(forecast);
        Assert.Equal("high 60°, low 45°, wind up to 12 mph",
            HouseBriefingCore.FormatWeatherLine(forecast!));
    }

    [Fact]
    public void BareTemperaturesWhenNothingElse()
    {
        var json = DailyJson(60, 45, null, null);

        var forecast = HouseBriefingCore.ParseWeatherDaily(json, "F");

        Assert.NotNull(forecast);
        Assert.Equal("high 60°, low 45°", HouseBriefingCore.FormatWeatherLine(forecast!));
    }

    [Theory]
    [InlineData(30, "some chance of rain")]   // inclusive lower bound
    [InlineData(59, "some chance of rain")]
    [InlineData(60, "rain likely")]           // inclusive upper bound
    [InlineData(29, "mostly dry")]
    public void PrecipitationWordingBoundaries(int prob, string expectedClause)
    {
        var json = DailyJson(55, 40, prob, null);

        var forecast = HouseBriefingCore.ParseWeatherDaily(json, "F");

        Assert.NotNull(forecast);
        Assert.Contains(expectedClause, HouseBriefingCore.FormatWeatherLine(forecast!));
    }

    [Fact]
    public void ParseReturnsNull_OnMalformedOrMissingData()
    {
        Assert.Null(HouseBriefingCore.ParseWeatherDaily("not json", "F"));
        Assert.Null(HouseBriefingCore.ParseWeatherDaily("", "F"));
        Assert.Null(HouseBriefingCore.ParseWeatherDaily("""{"latitude":38.9}""", "F"));
        Assert.Null(HouseBriefingCore.ParseWeatherDaily("""{"daily":{"temperature_2m_min":[40]}}""", "F"));
    }

    [Fact]
    public void ParseReadsFirstElementOfEachArray()
    {
        // forecast_days=1 gives single-element arrays; longer ones are tolerated.
        var json = DailyJson(72, 54, 10, 18);

        var forecast = HouseBriefingCore.ParseWeatherDaily(json, "F");

        Assert.NotNull(forecast);
        Assert.Equal(72, forecast!.MaxTemp);
        Assert.Equal(54, forecast!.MinTemp);
        Assert.Equal(10, forecast!.PrecipProbPct);
        Assert.Equal(18, forecast!.WindSpeedMax);
    }
}
