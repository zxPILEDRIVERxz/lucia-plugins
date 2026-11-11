namespace HouseBriefing.Tests;

/// <summary>
/// "Anything weird in the house?" — signal detection over one state snapshot.
/// Signal groups, in display priority order: unreachable > stale > off-target climate > media stuck.
/// Analysis returns ALL signals (with the true count); the brief formats a capped view of them.
/// </summary>
public sealed class HouseAnalysisTests
{
    private static readonly DateTime Now = new(2026, 11, 11, 12, 0, 0, DateTimeKind.Utc);

    private static HouseBriefingCore.HouseSnapshot Snap(
        string entityId,
        string domain,
        string state,
        int staleMinutes = 5,
        int changedMinutesAgo = -1,
        double? currentTemp = null,
        double? targetTemp = null) => new(
        entityId,
        domain,
        state,
        Now.AddMinutes(-staleMinutes),
        Now.AddMinutes(changedMinutesAgo < 0 ? staleMinutes : -changedMinutesAgo),
        currentTemp,
        targetTemp);

    private static HouseBriefingCore.HouseReport Analyze(params HouseBriefingCore.HouseSnapshot[] states)
        => HouseBriefingCore.AnalyzeHouse(states, new HouseBriefingCore.HouseAnalysisOptions { NowUtc = Now });

    // ── unreachable ────────────────────────────────────────────────

    [Fact]
    public void UnavailableEntityIsASignal()
    {
        var report = Analyze(
            Snap("sensor.garage_door", "sensor", "open"),
            Snap("light.driveway_light", "light", "unavailable"));

        Assert.Single(report.Signals);
        var signal = report.Signals[0];
        Assert.Equal("unreachable", signal.Kind);
        Assert.Equal("light.driveway_light", signal.EntityId);
        Assert.Equal("driveway light is unreachable.", signal.Message);
    }

    [Fact]
    public void UnknownStateCountsAsUnreachable()
    {
        var report = Analyze(Snap("binary_sensor.front_door", "binary_sensor", "unknown"));

        Assert.Single(report.Signals);
        Assert.Equal("unreachable", report.Signals[0].Kind);
    }

    [Fact]
    public void EmptyStateIsNotASignal()
    {
        var report = Analyze(Snap("sensor.odd", "sensor", ""));

        Assert.Empty(report.Signals);
    }

    // ── stale ──────────────────────────────────────────────────────

    [Fact]
    public void StaleSensorIsASignal()
    {
        var report = Analyze(Snap("sensor.garage_door", "sensor", "open", staleMinutes: 30 * 60));

        Assert.Single(report.Signals);
        Assert.Equal("stale", report.Signals[0].Kind);
        Assert.Equal("garage door hasn't checked in for 30 hours.", report.Signals[0].Message);
    }

    [Fact]
    public void OffBinarySensorIsNotStale()
    {
        // An "off" window/motion sensor is usually just quiet — not weird.
        var report = Analyze(Snap("binary_sensor.window", "binary_sensor", "off", staleMinutes: 40 * 60));

        Assert.Empty(report.Signals);
    }

    [Fact]
    public void OnBinarySensorGoingQuietIsStale()
    {
        var report = Analyze(Snap("binary_sensor.window", "binary_sensor", "on", staleMinutes: 26 * 60));

        Assert.Single(report.Signals);
        Assert.Equal("window hasn't checked in for 26 hours.", report.Signals[0].Message);
    }

    [Fact]
    public void FreshEntityIsNotStale()
    {
        var report = Analyze(Snap("sensor.temperature", "sensor", "71.2", staleMinutes: 11 * 60));

        Assert.Empty(report.Signals);
    }

    [Fact]
    public void UnmonitoredDomainsNeverGoStale()
    {
        var report = Analyze(
            Snap("light.kitchen", "light", "off", staleMinutes: 48 * 60),
            Snap("switch.strip", "switch", "off", staleMinutes: 48 * 60));

        Assert.Empty(report.Signals);
    }

    // ── off-target climate ─────────────────────────────────────────

    [Fact]
    public void FahrenheitOffTargetIsASignal()
    {
        var report = Analyze(Snap("climate.upstairs", "climate", "heat", currentTemp: 72, targetTemp: 69));

        Assert.Single(report.Signals);
        Assert.Equal("off-target", report.Signals[0].Kind);
        Assert.Equal("upstairs thermostat is 3° above target.", report.Signals[0].Message);
    }

    [Fact]
    public void ExactlyAtThresholdIsNotOffTarget()
    {
        var report = Analyze(Snap("climate.upstairs", "climate", "cool", currentTemp: 71, targetTemp: 69));

        Assert.Empty(report.Signals);
    }

    [Fact]
    public void CelsiusUsesItsOwnThreshold()
    {
        // 1.5 °C off — below the Fahrenheit threshold (2) but above the Celsius one (1).
        var report = HouseBriefingCore.AnalyzeHouse(
            [Snap("climate.upstairs", "climate", "heat", currentTemp: 21.5, targetTemp: 20)],
            new HouseBriefingCore.HouseAnalysisOptions { NowUtc = Now, TemperatureUnit = "C" });

        Assert.Single(report.Signals);
        Assert.Equal("upstairs thermostat is 2° above target.", report.Signals[0].Message);
    }

    [Fact]
    public void OffThermostatIsNotOffTarget()
    {
        var report = Analyze(Snap("climate.basement", "climate", "off", currentTemp: 55, targetTemp: 68));

        Assert.Empty(report.Signals);
    }

    [Fact]
    public void MissingTemperaturesSkipTheCheck()
    {
        var report = Analyze(
            Snap("climate.a", "climate", "heat", currentTemp: null, targetTemp: 69),
            Snap("climate.b", "climate", "heat", currentTemp: 75, targetTemp: null));

        Assert.Empty(report.Signals);
    }

    // ── media stuck ────────────────────────────────────────────────

    [Fact]
    public void LongPlayingMediaIsASignal()
    {
        var report = Analyze(
            Snap("media_player.living_room", "media_player", "playing", staleMinutes: 10, changedMinutesAgo: 7 * 60));

        Assert.Single(report.Signals);
        Assert.Equal("media", report.Signals[0].Kind);
        Assert.Equal("living room media player has been playing for 7 hours.", report.Signals[0].Message);
    }

    [Fact]
    public void PausedMediaIsNotStuck()
    {
        var report = Analyze(
            Snap("media_player.living_room", "media_player", "paused", staleMinutes: 10, changedMinutesAgo: 7 * 60));

        Assert.Empty(report.Signals);
    }

    // ── report shape ───────────────────────────────────────────────

    [Fact]
    public void WatchedCountCoversOnlyMonitoredDomains()
    {
        // Lights/switches ARE monitored (an unreachable one is a signal) but never go
        // "stale" — they report changes, not heartbeats. Anything else is ignored.
        var report = Analyze(
            Snap("sensor.a", "sensor", "1"),
            Snap("binary_sensor.b", "binary_sensor", "on"),
            Snap("climate.c", "climate", "idle"),
            Snap("media_player.d", "media_player", "idle"),
            Snap("device_tracker.e", "device_tracker", "home"),
            Snap("light.f", "light", "off"),
            Snap("script.g", "script", "off"));

        Assert.Equal(6, report.WatchedCount);
    }

    [Fact]
    public void SignalsAreOrderedByGroupPriorityThenEntityId()
    {
        var report = Analyze(
            Snap("media_player.b", "media_player", "playing", staleMinutes: 10, changedMinutesAgo: 8 * 60),
            Snap("climate.a", "climate", "heat", currentTemp: 74, targetTemp: 69),
            Snap("sensor.z", "sensor", "open", staleMinutes: 30 * 60),
            Snap("light.q", "light", "unavailable"),
            Snap("binary_sensor.a", "binary_sensor", "on", staleMinutes: 30 * 60));

        Assert.Equal(
            ["unreachable", "stale", "stale", "off-target", "media"],
            report.Signals.Select(s => s.Kind).ToList());
        // Within the stale group, alphabetical by entity id.
        Assert.Equal("binary_sensor.a", report.Signals[1].EntityId);
        Assert.Equal("sensor.z", report.Signals[2].EntityId);
    }

    [Fact]
    public void AnalysisKeepsEverySignalCappingHappensAtFormatTime()
    {
        var states = Enumerable.Range(1, 9)
            .Select(i => Snap($"sensor.s{i:D2}", "sensor", "open", staleMinutes: 30 * 60))
            .ToList();

        var report = Analyze(states.ToArray());

        Assert.Equal(9, report.Signals.Count);
    }
}
