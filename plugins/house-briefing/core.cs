using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Nodes;

/// <summary>
/// All house-briefing logic in one file, in the global namespace (C# scripts cannot declare
/// namespaces). The plugin script #loads this file, and the test project
/// (tests/HouseBriefing.Tests) compiles the very same file — so what is tested is
/// exactly what gets installed. Only one top-level type is exported (everything else is
/// nested), so it cannot collide with other plugins; each plugin is its own script compile.
///
/// The core works on plain <see cref="HouseSnapshot"/> records (no Home Assistant types)
/// so the analysis is testable in isolation. All times are UTC; culture is invariant.
/// "Weird" signals, in display priority order: unreachable &gt; stale &gt; off-target climate
/// &gt; media stuck. Analysis returns every signal (true count); formatting caps what is read aloud.
/// </summary>
public static class HouseBriefingCore
{
    // ---------------------------------------------------------------- data types

    /// <summary>One entity from the state snapshot, reduced to what the analysis needs.</summary>
    public sealed record HouseSnapshot(
        string EntityId,
        string Domain,
        string State,
        DateTime LastUpdatedUtc,
        DateTime LastChangedUtc,
        double? CurrentTemperature,
        double? TargetTemperature);

    /// <summary>A detected anomaly. Kind is one of: unreachable, stale, off-target, media.</summary>
    public sealed record Signal(string Kind, string EntityId, string Message);

    /// <summary>The house side of the brief: how many entities were watched and what looked off.</summary>
    public sealed record HouseReport(int WatchedCount, IReadOnlyList<Signal> Signals);

    /// <summary>Today's forecast, one day, pre-parsed from Open-Meteo daily arrays.</summary>
    public sealed record DailyForecast(
        double MaxTemp,
        double MinTemp,
        int? PrecipProbPct,
        double? WindSpeedMax,
        string TempUnit);

    /// <summary>
    /// One calendar event reduced to what the briefing needs. Start/End are absolute UTC;
    /// TimeLabel is pre-formatted by the glue layer (it owns time zones) — null for all-day events.
    /// </summary>
    public sealed record CalEvent(
        string Summary,
        DateTime StartUtc,
        DateTime EndUtc,
        bool AllDay,
        string? TimeLabel);

    /// <summary>
    /// The calendar side of the brief: the true count plus a possibly-capped list line.
    /// Line is null when Total is 0 (the section is omitted) or Failed is set (named plainly).
    /// </summary>
    public sealed record CalendarResult(int Total, string? Line, bool Failed = false)
    {
        public static CalendarResult Failure { get; } = new(0, null, true);
    }

    /// <summary>Tunable knobs. Defaults are deliberately conservative — a quiet house is fine.</summary>
    public sealed record HouseAnalysisOptions
    {
        public DateTime NowUtc { get; init; }
        /// <summary>"F" or "C" (from the Home Assistant unit system); picks thresholds and units.</summary>
        public string TemperatureUnit { get; init; } = "F";
        public int QuietAfterHours { get; init; } = 12;
        public double OffTargetDeltaF { get; init; } = 2.0;
        public double OffTargetDeltaC { get; init; } = 1.0;
        public int MediaStuckHours { get; init; } = 6;
    }

    // ---------------------------------------------------------------- analysis

    /// <summary>
    /// Domains the brief watches. Lights/switches are included because an UNREACHABLE one is
    /// a real problem, but they are exempt from staleness — they report state changes, not heartbeats.
    /// </summary>
    private static readonly HashSet<string> MonitoredDomains = new()
    {
        "sensor", "binary_sensor", "climate", "media_player", "device_tracker", "light", "switch",
    };

    /// <summary>Domains whose silence means something (they report heartbeats).</summary>
    private static readonly HashSet<string> StaleEligible = new()
    {
        "sensor", "binary_sensor", "device_tracker",
    };

    private static readonly Dictionary<string, int> GroupPriority = new()
    {
        ["unreachable"] = 0,
        ["stale"] = 1,
        ["off-target"] = 2,
        ["media"] = 3,
    };

    /// <summary>
    /// Scans a state snapshot for anomalies. Returns every signal found (no capping —
    /// the brief formats a capped view) plus the count of watched entities.
    /// </summary>
    public static HouseReport AnalyzeHouse(IReadOnlyList<HouseSnapshot> states, HouseAnalysisOptions options)
    {
        var signals = new List<Signal>();
        var watched = 0;

        foreach (var s in states)
        {
            if (!MonitoredDomains.Contains(s.Domain))
                continue;
            watched++;

            // Unreachable wins over everything else for this entity.
            if (s.State is "unavailable" or "unknown")
            {
                signals.Add(new Signal("unreachable", s.EntityId, $"{DisplayName(s)} is unreachable."));
                continue;
            }

            if (StaleEligible.Contains(s.Domain))
            {
                var age = options.NowUtc - s.LastUpdatedUtc;
                // "off" binary sensors are usually just quiet — not weird.
                if (age.TotalHours >= options.QuietAfterHours && s.State.Length > 0 && s.State != "off")
                    signals.Add(new Signal("stale", s.EntityId, $"{DisplayName(s)} hasn't checked in for {FormatAge(age)}."));
            }
            else if (s.Domain == "climate")
            {
                if (s.State != "off" && s.CurrentTemperature is { } cur && s.TargetTemperature is { } tgt)
                {
                    var delta = cur - tgt;
                    var threshold = options.TemperatureUnit == "C" ? options.OffTargetDeltaC : options.OffTargetDeltaF;
                    if (Math.Abs(delta) > threshold)
                        signals.Add(new Signal(
                            "off-target",
                            s.EntityId,
                            $"{DisplayName(s)} is {FmtNum(Math.Abs(delta))}° {(delta > 0 ? "above" : "below")} target."));
                }
            }
            else if (s.Domain == "media_player")
            {
                // The state transitioned to "playing" a while ago and never changed since.
                var age = options.NowUtc - s.LastChangedUtc;
                if (s.State == "playing" && age.TotalHours >= options.MediaStuckHours)
                    signals.Add(new Signal("media", s.EntityId, $"{DisplayName(s)} has been playing for {FormatAge(age)}."));
            }
        }

        var sorted = signals
            .OrderBy(s => GroupPriority[s.Kind])
            .ThenBy(s => s.EntityId, StringComparer.Ordinal)
            .ToList();

        return new HouseReport(watched, sorted);
    }

    // ---------------------------------------------------------------- calendar

    private const int MaxCalendarListed = 5;

    /// <summary>
    /// Selects today's events (overlap with [todayStartUtc, todayEndUtc), half-open) and
    /// formats the list line: timed events first by start time ("dentist at 1 PM"), all-day
    /// events last ("yoga (all day)"), in-progress events marked. At most
    /// <see cref="MaxCalendarListed"/> are listed — the true count is always kept so the
    /// brief can lead honestly. No events = null line (omitted, not announced).
    /// </summary>
    public static CalendarResult BuildCalendarLine(IReadOnlyList<CalEvent> events, DateTime nowUtc, DateTime todayStartUtc, DateTime todayEndUtc)
    {
        var todays = events
            .Where(e => e.StartUtc < todayEndUtc && e.EndUtc > todayStartUtc)
            .OrderBy(e => e.AllDay)                       // timed (false) before all-day
            .ThenBy(e => e.AllDay ? DateTime.MinValue : e.StartUtc)
            .ThenBy(e => e.Summary, StringComparer.Ordinal)
            .ToList();

        if (todays.Count == 0)
            return new CalendarResult(0, null);

        var line = string.Join(", ", todays.Take(MaxCalendarListed).Select(e => EventPhrase(e, nowUtc)));
        return new CalendarResult(todays.Count, line);
    }

    private static string EventPhrase(CalEvent e, DateTime nowUtc)
    {
        var name = string.IsNullOrWhiteSpace(e.Summary) ? "calendar event" : e.Summary.Trim();
        if (e.AllDay)
            return $"{name} (all day)";

        var phrase = string.IsNullOrEmpty(e.TimeLabel) ? name : $"{name} at {e.TimeLabel}";
        var inProgress = e.StartUtc < nowUtc && e.EndUtc > nowUtc;
        return inProgress ? $"{phrase} (in progress)" : phrase;
    }

    // ---------------------------------------------------------------- weather

    /// <summary>
    /// Parses the Open-Meteo /v1/forecast daily payload for one day:
    /// {"daily":{"temperature_2m_max":[72.1],"temperature_2m_min":[54.3],
    ///  "precipitation_probability_max":[15],"wind_speed_10m_max":[18.2]}}.
    /// Temperatures are required; probability and wind are optional (null when the API omits them).
    /// Returns null on malformed input or missing temperatures — never a guess.
    /// </summary>
    public static DailyForecast? ParseWeatherDaily(string json, string tempUnit)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            var daily = root?["daily"] as JsonObject;
            if (daily is null)
                return null;

            var tmax = FirstNumber(daily["temperature_2m_max"]);
            var tmin = FirstNumber(daily["temperature_2m_min"]);
            if (tmax is null || tmin is null)
                return null;

            return new DailyForecast(
                tmax.Value,
                tmin.Value,
                FirstNumber(daily["precipitation_probability_max"]) is { } p ? (int)Math.Round(p, MidpointRounding.AwayFromZero) : null,
                FirstNumber(daily["wind_speed_10m_max"]),
                tempUnit);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// One compact weather line, no trailing period (the brief owns sentence endings).
    /// Shape: "high 72°, low 54°, mostly dry, wind up to 18 mph" — the wet clause is
    /// "rain/snow likely" (≥60%), "some chance of rain/snow" (30-59%), "mostly dry" (&lt;30% or unknown);
    /// snow is guessed when the high is below freezing (28 °F / 0 °C). Wind: mph in F mode, km/h in C.
    /// </summary>
    public static string FormatWeatherLine(DailyForecast f)
    {
        var parts = new List<string> { $"high {FmtNum(f.MaxTemp)}°, low {FmtNum(f.MinTemp)}°" };

        if (f.PrecipProbPct is { } p)
        {
            if (p >= 30)
            {
                var precip = Freezing(f) ? "snow" : "rain";
                parts.Add(p >= 60 ? $"{precip} likely" : $"some chance of {precip}");
            }
            else
            {
                parts.Add("mostly dry");
            }
        }

        if (f.WindSpeedMax is { } w)
            parts.Add($"wind up to {FmtNum(w)} {(f.TempUnit == "C" ? "km/h" : "mph")}");

        return string.Join(", ", parts);
    }

    // ---------------------------------------------------------------- brief assembly

    private const int MaxBriefChars = 650;
    private const int MaxSignalsListed = 6;

    /// <summary>Convenience overload: no calendar section.</summary>
    public static string FormatBriefing(HouseReport? report, string? weatherLine)
        => FormatBriefing(report, new CalendarResult(0, null), weatherLine);

    /// <summary>
    /// Assembles the final voice report: house signals first, calendar second, weather last.
    /// A failed calendar is named plainly; an empty one is omitted (a quiet day is fine).
    /// Missing sections are never silently skipped when they represent a failure. Capped at
    /// <see cref="MaxBriefChars"/> chars by dropping whole trailing sentences.
    /// </summary>
    public static string FormatBriefing(HouseReport? report, CalendarResult? calendar, string? weatherLine)
    {
        var weatherSentence = string.IsNullOrWhiteSpace(weatherLine) ? null : $"Today: {weatherLine}.";

        if (report is null)
            return weatherSentence is null
                ? "The briefing came up empty — Home Assistant and the weather service are both unreachable right now."
                : $"I couldn't reach Home Assistant, so the house side is missing. {weatherSentence}";

        var sentences = new List<string>();

        if (report.Signals.Count == 0)
        {
            sentences.Add(report.WatchedCount > 0
                ? $"The house looks quiet — {report.WatchedCount} entities reporting normally."
                : "The house looks quiet.");
        }
        else
        {
            sentences.Add(report.Signals.Count == 1
                ? "1 thing looks off."
                : report.Signals.Count > MaxSignalsListed
                    ? $"{report.Signals.Count} things look off — here are the top six:"
                    : $"{report.Signals.Count} things look off.");

            foreach (var signal in report.Signals.Take(MaxSignalsListed))
                sentences.Add(Capitalize(signal.Message));
        }

        if (calendar is { Failed: true })
            sentences.Add("The calendar section is missing — the calendar service was unreachable.");
        else if (calendar is { Total: > 0, Line: not null })
            sentences.Add(calendar.Total <= MaxCalendarListed
                ? $"On the calendar: {calendar.Line}."
                : $"{calendar.Total} things on the calendar today — starting with {calendar.Line}.");

        if (weatherSentence is not null)
            sentences.Add(weatherSentence);

        return Assemble(sentences);
    }

    /// <summary>Joins whole sentences under the cap; falls back to a hard cut only if even one sentence overflows.</summary>
    private static string Assemble(IReadOnlyList<string> sentences)
    {
        var result = "";
        foreach (var sentence in sentences)
        {
            var candidate = result.Length == 0 ? sentence : result + " " + sentence;
            if (candidate.Length > MaxBriefChars)
                break;
            result = candidate;
        }

        return result.Length == 0 && sentences.Count > 0 ? HardTruncate(sentences[0]) : result;
    }

    /// <summary>Caps text at maxChars, marking truncation with an ellipsis.</summary>
    public static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";

    private static string HardTruncate(string text) => Truncate(text, MaxBriefChars);

    // ---------------------------------------------------------------- helpers

    /// <summary>Human display name: "sensor.garage_door" → "garage door", climate gets "thermostat", media gets "media player".</summary>
    private static string DisplayName(HouseSnapshot s)
    {
        var dot = s.EntityId.IndexOf('.');
        var rest = (dot >= 0 ? s.EntityId[(dot + 1)..] : s.EntityId).Replace("_", " ").Trim();

        return s.Domain switch
        {
            "climate" => rest + " thermostat",
            "media_player" => rest + " media player",
            _ => rest,
        };
    }

    /// <summary>Age in plain words: "45 minutes" / "7 hours" / "2 days" (singular when exactly one).</summary>
    private static string FormatAge(TimeSpan age)
    {
        if (age.TotalMinutes < 60)
            return Plural((int)Math.Round(age.TotalMinutes, MidpointRounding.AwayFromZero), "minute");
        if (age.TotalHours < 48)
            return Plural((int)Math.Round(age.TotalHours, MidpointRounding.AwayFromZero), "hour");
        return Plural((int)Math.Round(age.TotalDays, MidpointRounding.AwayFromZero), "day");
    }

    private static string Plural(int value, string unit) => $"{value} {(value == 1 ? unit : unit + "s")}";

    private static string FmtNum(double value) => value.ToString("0", CultureInfo.InvariantCulture);

    private static bool Freezing(DailyForecast f)
        => f.TempUnit == "C" ? f.MaxTemp < 0 : f.MaxTemp < 28;

    private static string Capitalize(string text)
        => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    private static double? FirstNumber(JsonNode? node)
    {
        if (node is not JsonArray array)
            return null;
        foreach (var element in array)
        {
            var value = ToNumber(element);
            if (value is not null)
                return value;
        }
        return null;
    }

    private static double? ToNumber(JsonNode? node)
    {
        if (node is null)
            return null;
        try
        {
            return node.GetValue<double?>();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
