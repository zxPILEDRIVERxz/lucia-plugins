namespace FlightWeather.Tests;

/// <summary>
/// The verdict engine. Safety rules, in priority order:
/// 1. no usable data at all -> NoData (never claim flyability on nothing)
/// 2. hard-block alerts (tornado, severe thunderstorm, high wind, blizzard, ice storm, hurricane) -> NoGo
/// 3. observed/model sustained or gusts beyond the pilot's limits -> NoGo
/// 4. within 80% of a limit, stale observation, or softer alerts (fog, thunderstorms, wind advisory) -> Marginal
/// Gusts are the dangerous failure mode: when the station reports no gust, the model's
/// gust may be used as a proxy but the verdict must say it is modeled.
/// </summary>
public sealed class VerdictTests
{
    private const int MaxSustained = 15; // mph
    private const int MaxGust = 25;      // mph
    private const int StaleAfterMinutes = 90;

    private static FlightWeatherCore.WindObservation Obs(
        double? sustainedMph,
        double? gustMph,
        int ageMinutes,
        string stationId = "KOWB") =>
        new(stationId, "Owensboro", sustainedMph, gustMph, 200,
            DateTime.UtcNow.AddMinutes(-ageMinutes), ageMinutes, "RAW METAR TEXT");

    private static FlightWeatherCore.ModelWinds Model(double? sustainedMph, double? gustMph, double? aloftMaxMph = null) =>
        new(sustainedMph, gustMph, gustMph, aloftMaxMph, HasData: sustainedMph is not null);

    private FlightWeatherCore.VerdictResult Evaluate(
        FlightWeatherCore.WindObservation? obs,
        FlightWeatherCore.ModelWinds? model,
        params (string Event, string Severity)[] alerts) =>
        FlightWeatherCore.Evaluate(obs, model,
            alerts.Select(a => new FlightWeatherCore.WeatherAlert(a.Event, a.Severity)).ToList(),
            MaxSustained, MaxGust, StaleAfterMinutes);

    [Fact]
    public void NoData_WhenNeitherSourceHasAnything()
    {
        var result = Evaluate(null, null);

        Assert.Equal(FlightWeatherCore.Verdict.NoData, result.Verdict);
        Assert.Single(result.Reasons);
    }

    [Theory]
    [InlineData("Tornado Warning", "Extreme")]
    [InlineData("Severe Thunderstorm Warning", "Extreme")]
    [InlineData("High Wind Warning", "Severe")]
    [InlineData("Blizzard Warning", "Severe")]
    [InlineData("Ice Storm Warning", "Severe")]
    [InlineData("Tropical Storm Warning", "Severe")]
    public void NoGo_WhenAHardBlockAlertIsActive(string eventName, string severity)
    {
        var result = Evaluate(Obs(8, 12, 30), null, (eventName, severity));

        Assert.Equal(FlightWeatherCore.Verdict.NoGo, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains(eventName, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoGo_WhenSustainedWindExceedsLimit()
    {
        var result = Evaluate(Obs(17, 20, 30), null);

        Assert.Equal(FlightWeatherCore.Verdict.NoGo, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("15", StringComparison.Ordinal)); // mentions the limit
    }

    [Fact]
    public void NoGo_WhenObservedGustsExceedLimit()
    {
        var result = Evaluate(Obs(10, 26, 30), null);

        Assert.Equal(FlightWeatherCore.Verdict.NoGo, result.Verdict);
    }

    [Fact]
    public void NoGo_WhenModelGustsExceedLimit_AndStationReportsNoGust_MarkedAsModeled()
    {
        var result = Evaluate(Obs(10, gustMph: null, 30), Model(11, 26));

        Assert.Equal(FlightWeatherCore.Verdict.NoGo, result.Verdict);
        Assert.Contains(result.Reasons, r =>
            r.Contains("26", StringComparison.Ordinal) &&
            r.Contains("modeled", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoGo_WhenGustsExpectedToBuildWithinNextHours()
    {
        var model = new FlightWeatherCore.ModelWinds(10, 14, 27, null, HasData: true); // next-hours max gust 27
        var result = Evaluate(Obs(9, 14, 30), model);

        Assert.Equal(FlightWeatherCore.Verdict.NoGo, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("expected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Marginal_WhenSustainedWindIsWithin80PercentOfLimit()
    {
        var result = Evaluate(Obs(13, 14, 30), null); // 13 > 0.8 * 15 = 12

        Assert.Equal(FlightWeatherCore.Verdict.Marginal, result.Verdict);
    }

    [Fact]
    public void Marginal_WhenObservationIsStale()
    {
        var result = Evaluate(Obs(8, 12, ageMinutes: 120), null);

        Assert.Equal(FlightWeatherCore.Verdict.Marginal, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("old", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Marginal_WhenObservationTimeIsUnknown()
    {
        var result = Evaluate(Obs(8, 12, ageMinutes: -1), null);

        Assert.Equal(FlightWeatherCore.Verdict.Marginal, result.Verdict);
    }

    [Fact]
    public void Marginal_WhenSofterAlertsAreActive()
    {
        var withFog = Evaluate(Obs(8, 12, 30), null, ("Dense Fog Advisory", "Minor"));
        Assert.Equal(FlightWeatherCore.Verdict.Marginal, withFog.Verdict);

        var withStorms = Evaluate(Obs(8, 12, 30), null, ("Thunderstorm Watch", "Moderate")); // not "severe"
        Assert.Equal(FlightWeatherCore.Verdict.Marginal, withStorms.Verdict);

        var withWindAdvisory = Evaluate(Obs(6, 9, 30), null, ("Wind Advisory", "Moderate"));
        Assert.Equal(FlightWeatherCore.Verdict.Marginal, withWindAdvisory.Verdict);
    }

    [Fact]
    public void Flyable_WhenWindsAreWithinLimitsAndNoAlerts()
    {
        var result = Evaluate(Obs(8, 12, 30), null);

        Assert.Equal(FlightWeatherCore.Verdict.Flyable, result.Verdict);
    }

    [Fact]
    public void Flyable_WithAloftNote_WhenWindsAloftAreNoticeablyStronger()
    {
        var result = Evaluate(Obs(8, 12, 30), Model(9, 13, aloftMaxMph: 20));

        Assert.Equal(FlightWeatherCore.Verdict.Flyable, result.Verdict);
        Assert.Contains(result.Reasons, r => r.Contains("aloft", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NoData_WhenStationParsedButHasNoUsableWindAndNoModel()
    {
        var obs = new FlightWeatherCore.WindObservation("KOWB", "Owensboro", null, null, null,
            DateTime.UtcNow.AddMinutes(-10), 10, "CALM");

        var result = Evaluate(obs, null);

        Assert.Equal(FlightWeatherCore.Verdict.NoData, result.Verdict);
    }

    [Fact]
    public void DirectionWord_MapsDegreesToCompass()
    {
        Assert.Equal("north", FlightWeatherCore.DirectionWord(355));
        Assert.Equal("northeast", FlightWeatherCore.DirectionWord(45));
        Assert.Equal("south", FlightWeatherCore.DirectionWord(200));
        Assert.Equal("southwest", FlightWeatherCore.DirectionWord(225));
        Assert.Null(FlightWeatherCore.DirectionWord(null));
    }
}
