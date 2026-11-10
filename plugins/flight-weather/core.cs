using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>
/// All flight-weather logic in one file, in the global namespace (C# scripts cannot declare
/// namespaces). The plugin script #loads this file, and the test project
/// (tests/FlightWeather.Tests) compiles the very same file — so what is tested is
/// exactly what gets installed. Only one top-level type is exported (everything else is
/// nested), so it cannot collide with other plugins; each plugin is its own script compile.
///
/// Speeds are mph everywhere. NWS values are converted from their WMO unitCode; Open-Meteo
/// values arrive pre-converted because the caller requests wind_speed_unit=mph.
/// </summary>
public static class FlightWeatherCore
{
    // ---------------------------------------------------------------- data types

    public enum Verdict { Flyable, Marginal, NoGo, NoData }

    /// <summary>A surface wind observation from NWS. Gust is null when the station observed no gust.</summary>
    public sealed record WindObservation(
        string StationId,
        string? StationName,
        double? SustainedMph,
        double? GustMph,
        int? DirectionDeg,
        DateTime ObservedAtUtc,
        int AgeMinutes, // -1 = unknown (treated as stale)
        string? RawMessage);

    /// <summary>Model-layer winds from Open-Meteo for the current hour and short build-up.</summary>
    public sealed record ModelWinds(
        double? CurrentSustainedMph,
        double? CurrentGustMph,
        double? NextHoursMaxGustMph, // max gust over the current hour plus the next three
        double? AloftMaxMph,         // max sustained across 80/120/180 m AGL at the current hour
        bool HasData);

    public sealed record WeatherAlert(string Event, string? Severity);

    public sealed record VerdictResult(Verdict Verdict, IReadOnlyList<string> Reasons);

    // ---------------------------------------------------------------- unit conversion

    /// <summary>
    /// Converts a NWS WMO-unit value to mph. Unknown or missing unit codes return null —
    /// never a guess: a wrong wind speed is worse than no wind speed.
    /// </summary>
    public static double? ToMph(double value, string? unitCode)
    {
        switch (unitCode)
        {
            case "wmoUnit:km_h-1": return value / 1.609344;
            case "wmoUnit:kn": return value * 1.15078;
            case "wmoUnit:m_s-1": return value * 2.236936;
            case "wmoUnit:mph": return value;
            default: return null;
        }
    }

    // ---------------------------------------------------------------- parsing

    /// <summary>Parses an NWS /stations/{icao}/observations/latest response (GeoJSON-ld feature).</summary>
    public static WindObservation? ParseNwsObservation(string json, DateTime nowUtc)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception)
        {
            return null;
        }
        if (node is not JsonObject root)
            return null;

        var props = root["properties"];
        if (props is null)
            return null;

        // Station identity: the stationId property when present, otherwise derive it from
        // the @id url ("https://api.weather.gov/stations/KOWB" -> "KOWB").
        string? stationId = GetText(props, "stationId");
        if (string.IsNullOrWhiteSpace(stationId))
        {
            var idUrl = GetText(props, "@id");
            if (!string.IsNullOrWhiteSpace(idUrl))
                stationId = idUrl!.TrimEnd('/').Split('/').Last();
        }
        if (string.IsNullOrWhiteSpace(stationId))
            return null;

        var observedUtc = DateTime.MinValue;
        var ageMinutes = -1;
        var timestamp = GetText(props, "timestamp");
        if (!string.IsNullOrWhiteSpace(timestamp) && DateTimeOffset.TryParse(timestamp, out var when))
        {
            observedUtc = when.UtcDateTime;
            ageMinutes = (int)Math.Floor((nowUtc - observedUtc).TotalMinutes);
        }

        var directionValue = GetNumberValue(props["windDirection"]?["value"]);
        int? directionDeg = directionValue is not null ? (int?)directionValue.Value : null;

        return new WindObservation(
            stationId!,
            GetText(props, "stationName"),
            ReadWindMph(props["windSpeed"]),
            ReadWindMph(props["windGust"]),
            directionDeg,
            observedUtc,
            ageMinutes,
            GetText(props, "rawMessage"));
    }

    /// <summary>Parses an NWS /alerts/active response; nameless entries are skipped, malformed input yields an empty list.</summary>
    public static List<WeatherAlert> ParseNwsAlerts(string json)
    {
        var alerts = new List<WeatherAlert>();
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception)
        {
            return alerts;
        }
        if (node is not JsonObject root || root["features"] is not JsonArray features)
            return alerts;

        foreach (var feature in features)
        {
            var props = feature?["properties"];
            var event_ = GetText(props, "event");
            if (string.IsNullOrWhiteSpace(event_))
                continue;
            alerts.Add(new WeatherAlert(event_, GetText(props, "severity")));
        }
        return alerts;
    }

    /// <summary>
    /// Parses an Open-Meteo hourly response. The caller must request timeformat=unixtime and
    /// wind_speed_unit=mph so the values land here already in those units.
    /// </summary>
    public static ModelWinds? ParseOpenMeteoHourly(string json, long nowUnixSeconds)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (Exception)
        {
            return null;
        }
        if (node is not JsonObject root || root["hourly"]?["time"] is not JsonArray timeArray)
            return null;

        var hourly = root["hourly"]!;
        var times = new List<long>();
        foreach (var t in timeArray)
        {
            var tv = GetNumberValue(t);
            if (tv is not null)
                times.Add((long)tv.Value);
        }
        if (times.Count == 0)
            return null;

        // Current hour = last timestamp at or before now; fall back to the first hour when now is earlier.
        var currentIdx = 0;
        for (var i = times.Count - 1; i >= 0; i--)
        {
            if (times[i] <= nowUnixSeconds)
            {
                currentIdx = i;
                break;
            }
        }

        double? At(string key, int idx)
        {
            if (hourly[key] is not JsonArray arr || idx < 0 || idx >= arr.Count)
                return null;
            return GetNumberValue(arr[idx]);
        }

        var currentSustained = At("wind_speed_10m", currentIdx);
        var currentGust = At("wind_gusts_10m", currentIdx);

        // Max gust over the current hour plus the next three (nulls ignored).
        double? nextMaxGust = null;
        for (var i = currentIdx; i <= Math.Min(currentIdx + 3, times.Count - 1); i++)
        {
            var g = At("wind_gusts_10m", i);
            if (g is not null && (nextMaxGust is null || g > nextMaxGust))
                nextMaxGust = g;
        }

        // Max aloft sustained at the current hour across AGL levels 80/120/180 m (nulls ignored).
        double? aloftMax = null;
        foreach (var level in new[] { "wind_speed_80m", "wind_speed_120m", "wind_speed_180m" })
        {
            var v = At(level, currentIdx);
            if (v is not null && (aloftMax is null || v > aloftMax))
                aloftMax = v;
        }

        return new ModelWinds(
            currentSustained,
            currentGust,
            nextMaxGust,
            aloftMax,
            currentSustained is not null || currentGust is not null || nextMaxGust is not null || aloftMax is not null);
    }

    // ---------------------------------------------------------------- verdict engine

    private static readonly string[] HardBlockKeywords =
        { "tornado", "severe thunderstorm", "high wind", "blizzard", "ice storm", "hurricane", "tropical storm" };

    private static readonly string[] SoftAlertKeywords = { "fog", "thunderstorm", "wind" };

    /// <summary>
    /// Safety rules, in priority order:
    /// 1. no usable data at all -> NoData (never claim flyability on nothing),
    /// 2. hard-block alerts active -> NoGo,
    /// 3. sustained or gusts beyond the pilot's limits (observed preferred; model values are
    ///    used as a proxy and flagged "modeled") -> NoGo, including gusts expected to build
    ///    within the next few hours,
    /// 4. within 80% of a limit, stale/unknown-age observation, or softer alerts -> Marginal.
    /// Winds aloft are informational only.
    /// </summary>
    public static VerdictResult Evaluate(
        WindObservation? obs,
        ModelWinds? model,
        IReadOnlyList<WeatherAlert> alerts,
        int maxSustainedMph,
        int maxGustMph,
        int staleAfterMinutes)
    {
        var observedSustained = obs?.SustainedMph;
        var observedGust = obs?.GustMph;
        var modeledSustained = (model != null && model.HasData) ? model.CurrentSustainedMph : null;
        var modeledGust = (model != null && model.HasData) ? model.CurrentGustMph : null;

        // Rule 1: never claim flyability on nothing.
        if (observedSustained is null && observedGust is null && modeledSustained is null && modeledGust is null)
            return new VerdictResult(Verdict.NoData, new[] { "No usable wind data from any source." });

        // Observed station data wins; the model fills gaps (and says so when it does).
        var sustained = observedSustained ?? modeledSustained;
        var gust = observedGust ?? modeledGust;
        var sustainedIsModeled = observedSustained is null && sustained is not null;
        var gustIsModeled = observedGust is null && gust is not null;

        var noGo = false;
        var marginal = false;
        var reasons = new List<string>();

        // Rule 2: hard-block alerts are an automatic No-Go.
        foreach (var alert in alerts)
        {
            if (IsHardBlock(alert))
            {
                noGo = true;
                reasons.Add($"Active {alert.Event} — do not fly.");
            }
        }

        // Rule 3: beyond the pilot's limits.
        if (sustained is not null && sustained > maxSustainedMph)
        {
            noGo = true;
            reasons.Add(sustainedIsModeled
                ? $"Modeled sustained wind {sustained:F0} mph exceeds your limit of {maxSustainedMph} mph (not observed)."
                : $"Sustained wind {sustained:F0} mph exceeds your limit of {maxSustainedMph} mph.");
        }
        if (gust is not null && gust > maxGustMph)
        {
            noGo = true;
            reasons.Add(gustIsModeled
                ? $"Modeled gusts {gust:F0} mph exceed your limit of {maxGustMph} mph; the station reported no observed gust."
                : $"Observed gusts {gust:F0} mph exceed your limit of {maxGustMph} mph.");
        }
        var nextMaxGust = (model != null && model.HasData) ? model.NextHoursMaxGustMph : null;
        if (nextMaxGust is not null && nextMaxGust > maxGustMph)
        {
            noGo = true;
            reasons.Add($"Gusts up to {nextMaxGust:F0} mph are expected in the next few hours, above your limit of {maxGustMph} mph.");
        }

        // Rule 4: marginal conditions.
        if (sustained is not null && sustained > maxSustainedMph * 0.8)
        {
            marginal = true;
            reasons.Add($"Sustained wind {sustained:F0} mph is within 80% of your {maxSustainedMph} mph limit.");
        }
        if (gust is not null && gust > maxGustMph * 0.8)
        {
            marginal = true;
            reasons.Add($"Gusts {gust:F0} mph are within 80% of your {maxGustMph} mph limit.");
        }
        if (obs is not null && obs.AgeMinutes < 0)
        {
            marginal = true;
            reasons.Add("The station's observation time is unknown, so the reading may be stale.");
        }
        else if (obs is not null && obs.AgeMinutes > staleAfterMinutes)
        {
            marginal = true;
            reasons.Add($"Latest observation is {obs.AgeMinutes} minutes old (stale after {staleAfterMinutes}).");
        }
        foreach (var alert in alerts)
        {
            if (!IsHardBlock(alert) && IsSoftAlert(alert))
            {
                marginal = true;
                reasons.Add($"Active {alert.Event}.");
            }
        }

        // Informational: noticeably stronger winds aloft than near the surface.
        var surfaceRef = sustained ?? gust;
        var aloftMax = (model != null && model.HasData) ? model.AloftMaxMph : null;
        if (aloftMax is not null && surfaceRef is not null && aloftMax > surfaceRef + 5)
            reasons.Add($"Winds aloft up to {aloftMax:F0} mph — noticeably stronger than the {surfaceRef:F0} mph near the surface; expect more at altitude.");

        var verdict = noGo ? Verdict.NoGo : marginal ? Verdict.Marginal : Verdict.Flyable;
        return new VerdictResult(verdict, reasons);
    }

    private static bool IsHardBlock(WeatherAlert alert) =>
        HardBlockKeywords.Any(k => alert.Event.Contains(k, StringComparison.OrdinalIgnoreCase));

    private static bool IsSoftAlert(WeatherAlert alert) =>
        SoftAlertKeywords.Any(k => alert.Event.Contains(k, StringComparison.OrdinalIgnoreCase));

    // ---------------------------------------------------------------- helpers

    private static readonly string[] CompassRose =
        { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };

    /// <summary>Maps wind direction (degrees FROM which the wind blows) to an 8-point compass word.</summary>
    public static string? DirectionWord(int? degrees)
    {
        if (degrees is null)
            return null;
        var deg = ((degrees.Value % 360) + 360) % 360;
        var sector = (int)Math.Round(deg / 45.0, MidpointRounding.AwayFromZero) % 8;
        return CompassRose[sector];
    }

    /// <summary>Reads a NWS {unitCode, value} wind property and converts it to mph (null when absent or unconvertible).</summary>
    private static double? ReadWindMph(JsonNode? windNode)
    {
        if (windNode is null)
            return null;
        var value = GetNumberValue(windNode["value"]);
        if (value is null)
            return null;
        return ToMph(value.Value, GetTextValue(windNode["unitCode"]));
    }

    private static string? GetText(JsonNode? node, string field) => GetTextValue(node?[field]);

    private static string? GetTextValue(JsonNode? valueNode)
    {
        if (valueNode is null)
            return null;
        try
        {
            return valueNode.GetValue<string>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? GetNumberValue(JsonNode? valueNode)
    {
        if (valueNode is null)
            return null;
        try
        {
            return valueNode.GetValue<double?>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
