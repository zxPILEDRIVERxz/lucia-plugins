namespace HouseBriefing.Tests;

/// <summary>
/// Assembling the final voice report: house section first, weather last, one compact
/// block capped at 650 chars (cut at a sentence boundary, never mid-word except as a
/// last resort). Missing sections are named plainly — never silently skipped.
/// </summary>
public sealed class BriefingFormatTests
{
    private const string Weather = "high 72°, low 54°, mostly dry, wind up to 18 mph";

    private static HouseBriefingCore.Signal Sig(string kind, string entityId, string message)
        => new(kind, entityId, message);

    private static HouseBriefingCore.HouseReport Report(int watched, params HouseBriefingCore.Signal[] signals)
        => new(watched, signals);

    [Fact]
    public void AllClearWithWeather()
    {
        var text = HouseBriefingCore.FormatBriefing(Report(42), Weather);

        Assert.Equal("The house looks quiet — 42 entities reporting normally. Today: " + Weather + ".", text);
    }

    [Fact]
    public void AllClearWithoutWeather()
    {
        var text = HouseBriefingCore.FormatBriefing(Report(42), null);

        Assert.Equal("The house looks quiet — 42 entities reporting normally.", text);
    }

    [Fact]
    public void SingleSignalUsesSingularVerb()
    {
        var report = Report(10, Sig("stale", "binary_sensor.window", "window hasn't checked in for 26 hours."));

        Assert.Equal("1 thing looks off. Window hasn't checked in for 26 hours.",
            HouseBriefingCore.FormatBriefing(report, null));
    }

    [Fact]
    public void MultipleSignalsThenWeather()
    {
        var report = Report(40,
            Sig("stale", "sensor.garage_door", "garage door hasn't checked in for 30 hours."),
            Sig("off-target", "climate.upstairs", "upstairs thermostat is 3° above target."));

        Assert.Equal(
            "2 things look off. Garage door hasn't checked in for 30 hours. " +
            "Upstairs thermostat is 3° above target. Today: " + Weather + ".",
            HouseBriefingCore.FormatBriefing(report, Weather));
    }

    [Fact]
    public void MoreThanSixSignalsListsOnlyTopSix()
    {
        var signals = Enumerable.Range(1, 7)
            .Select(i => Sig("stale", $"sensor.s{i:D2}", $"sensor s{i:D2} hasn't checked in for 30 hours."))
            .ToArray();

        var text = HouseBriefingCore.FormatBriefing(Report(50, signals), null);

        Assert.StartsWith("7 things look off — here are the top six:", text);
        Assert.Contains("Sensor s01 hasn't checked in for 30 hours.", text);
        Assert.Contains("Sensor s06 hasn't checked in for 30 hours.", text);
        Assert.DoesNotContain("s07", text);
    }

    [Fact]
    public void BothSectionsMissing()
    {
        var text = HouseBriefingCore.FormatBriefing(null, null);

        Assert.Equal(
            "The briefing came up empty — Home Assistant and the weather service are both unreachable right now.",
            text);
    }

    [Fact]
    public void HouseMissingStillDeliversWeather()
    {
        var text = HouseBriefingCore.FormatBriefing(null, Weather);

        Assert.Equal("I couldn't reach Home Assistant, so the house side is missing. Today: " + Weather + ".", text);
    }

    [Fact]
    public void OutputRespectsCharCapAtSentenceBoundary()
    {
        // Long entity names push the honest list well past 650 chars — the brief must cut
        // at a sentence boundary and never exceed the cap.
        var longName = "the very long descriptive entity name that a real install can produce";
        var signals = Enumerable.Range(1, 6)
            .Select(i => Sig("stale", $"sensor.n{i}", $"{longName} number {i} hasn't checked in for 30 hours."))
            .ToArray();

        var text = HouseBriefingCore.FormatBriefing(Report(60, signals), Weather);

        Assert.True(text.Length <= 650, $"expected <= 650 chars, got {text.Length}: {text}");
        Assert.EndsWith(".", text);
        Assert.StartsWith("6 things look off", text);
    }
}
