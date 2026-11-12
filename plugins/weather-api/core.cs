using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

/// <summary>
/// All weather-api logic in one file, in the global namespace (C# scripts cannot declare
/// namespaces). The plugin script #loads this file, and the test project
/// (tests/WeatherApi.Tests) compiles the very same file — so what is tested is
/// exactly what gets installed.
/// </summary>

/// <summary>
/// The home location read from Home Assistant's system configuration
/// (the user's onboarding coordinates) plus a human-readable display name.
/// </summary>
public sealed record WeatherHomeLocation(double Latitude, double Longitude, string DisplayName);

/// <summary>
/// Decides which location a weather lookup targets: an explicit "home" request wins
/// (it means the user's house, not a place name — bare "Home" would geocode to Homei,
/// T'ai-wan), then a place named in the question, then the configured default override,
/// then the automatic Home Assistant home coordinates (when enabled and usable).
/// Coordinates are unambiguous to WeatherAPI's geocoder; bare postal codes are not.
/// </summary>
public static class WeatherQueryResolver
{
    /// <summary>Values that mean "my house" rather than a place name (case-insensitive, exact after trim).</summary>
    private static readonly HashSet<string> HomeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        "home", "my home", "at home", "the home",
    };

    /// <summary>True when the requested location means "the user's home" instead of a named place.</summary>
    public static bool IsHomeRequest(string? location)
        => location is not null && HomeAliases.Contains(location.Trim());

    public static string? Resolve(string? requestedLocation, string? defaultOverride, bool useHomeLocation, WeatherHomeLocation? home)
    {
        var requested = requestedLocation?.Trim();

        if (IsHomeRequest(requested))
            // The user said HOME — never substitute a configured default; if the HA home
            // location is disabled or missing, return null so the caller asks the user.
            return useHomeLocation && IsUsable(home) ? Coordinates(home!) : null;

        if (!string.IsNullOrWhiteSpace(requested))
            return requested;

        if (!string.IsNullOrWhiteSpace(defaultOverride))
            return defaultOverride.Trim();

        if (useHomeLocation && IsUsable(home))
            return Coordinates(home!);

        return null;
    }

    public static bool IsUsable(WeatherHomeLocation? home) =>
        home is not null && (home.Latitude != 0 || home.Longitude != 0);

    public static string Coordinates(WeatherHomeLocation home) =>
        $"{home.Latitude:0.######}, {home.Longitude:0.######}";
}

/// <summary>
/// Normalizes WeatherAPI.com forecast.json payloads into one canonical shape before
/// deserialization. The service has served two shapes over time: the classic
/// <c>current_condition[]</c> / <c>forecast[]</c> layout and the current spec's
/// <c>current</c> object / <c>forecast.forecastday[]</c> layout with top-level
/// <c>alerts.alert[]</c>. This accepts both so the plugin keeps working either way.
/// </summary>
public static class WeatherResponseNormalizer
{
    public static string Normalize(string raw)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(raw);
        }
        catch (JsonException)
        {
            return raw;
        }

        if (node is not JsonObject root)
            return raw;

        // forecast: object { forecastday: [...] } -> [...] (clone: node already has a parent)
        if (root["forecast"] is JsonObject fore && fore["forecastday"] is JsonArray days)
            root["forecast"] = days.DeepClone();

        // current_condition missing but a `current` object is present -> wrap in list
        if (root["current_condition"] is null && root["current"] is JsonObject cur)
            root["current_condition"] = new JsonArray(cur.DeepClone());

        NormalizeAlerts(root);

        if (root["forecast"] is JsonArray forecast)
        {
            foreach (var dayNode in forecast)
            {
                if (dayNode is not JsonObject day)
                    continue;
                HoistDayTemps(day);
                ConvertPollenObject(day);
                NormalizeAlerts(day);
                NormalizeHours(day);
            }
        }

        return root.ToJsonString();
    }

    /// <summary>Moves max/min temps up to the day entry when they only exist in the `day` sub-object.</summary>
    private static void HoistDayTemps(JsonObject day)
    {
        if (day["day"] is not JsonObject sub)
            return;
        foreach (var key in new[] { "maxtemp_c", "maxtemp_f", "mintemp_c", "mintemp_f" })
        {
            if (day[key] is null && sub[key] is JsonNode node)
                day[key] = node.DeepClone();
        }
    }

    /// <summary>Converts a pollen object ({ "Birch": 102, ... }) into the list shape ([{name, count}]).</summary>
    private static void ConvertPollenObject(JsonObject day)
    {
        if (day["pollen"] is not JsonObject pollenObj)
            return;
        var list = new JsonArray();
        foreach (var (name, value) in pollenObj)
        {
            if (value is null)
                continue;
            if (value is JsonValue jv && jv.TryGetValue<double>(out var count) && count == 0)
                continue;
            list.Add(new JsonObject { ["name"] = name, ["count"] = value.DeepClone() });
        }
        day["pollen"] = list;
    }

    /// <summary>Unwraps alerts containers ({ event: [...] } or { alert: [...] }) into a bare array.</summary>
    private static void NormalizeAlerts(JsonObject scope)
    {
        if (scope["alerts"] is not JsonObject alertsObj)
            return;
        var list = alertsObj["event"] as JsonArray ?? alertsObj["alert"] as JsonArray;
        if (list is not null)
            scope["alerts"] = list.DeepClone();
    }

    /// <summary>Converts numeric hour `time` values (0/300/600...) to their string form.</summary>
    private static void NormalizeHours(JsonObject day)
    {
        if (day["hour"] is not JsonArray hours)
            return;
        foreach (var hourNode in hours)
        {
            if (hourNode is not JsonObject hour)
                continue;
            if (hour["time"] is JsonValue tv && tv.TryGetValue<double>(out var t))
                hour["time"] = ((long)t).ToString();
        }
    }
}

public sealed class WeatherForecastResponse
{
    [JsonPropertyName("location")]
    public WeatherLocationDto? Location { get; set; }

    [JsonPropertyName("current_condition")]
    public List<WeatherCurrentConditionDto> CurrentCondition { get; set; } = [];

    [JsonPropertyName("forecast")]
    public List<WeatherForecastDayDto> Forecast { get; set; } = [];

    [JsonPropertyName("alerts")]
    public List<WeatherAlertDto>? Alerts { get; set; }
}

public sealed class WeatherLocationDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";

    [JsonPropertyName("country")]
    public string Country { get; set; } = "";

    [JsonPropertyName("localtime")]
    public string? Localtime { get; set; }
}

public sealed class WeatherCurrentConditionDto
{
    [JsonPropertyName("temp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempF { get; set; }

    [JsonPropertyName("temp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempC { get; set; }

    [JsonPropertyName("feelslike_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? FeelslikeF { get; set; }

    [JsonPropertyName("feelslike_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? FeelslikeC { get; set; }

    [JsonPropertyName("humidity")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? Humidity { get; set; }

    [JsonPropertyName("wind_mph")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? WindMph { get; set; }

    [JsonPropertyName("wind_kmph")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? WindKmph { get; set; }

    [JsonPropertyName("wind_dir_16_point")]
    public string? WindDir16 { get; set; }

    [JsonPropertyName("wind_dir")]
    public string? WindDir { get; set; }

    [JsonPropertyName("weatherDesc")]
    public List<WeatherDescriptionDto>? WeatherDesc { get; set; }

    [JsonPropertyName("condition")]
    public WeatherConditionDto? Condition { get; set; }

    /// <summary>Condition text from either the classic weatherDesc list or the condition object.</summary>
    public string? ConditionText()
    {
        var fromList = WeatherDesc is { Count: > 0 } d ? d[0].Value : null;
        return string.IsNullOrWhiteSpace(fromList) ? Condition?.Text : fromList;
    }

    /// <summary>Wind direction from either the classic or current field name.</summary>
    public string? WindDirection =>
        string.IsNullOrWhiteSpace(WindDir16) ? WindDir : WindDir16;
}

public sealed class WeatherDescriptionDto
{
    [JsonPropertyName("value")]
    public string Value { get; set; } = "";
}

public sealed class WeatherConditionDto
{
    [JsonPropertyName("text")]
    public string? Text { get; set; }

    [JsonPropertyName("value")]
    public string? Value { get; set; }
}

public sealed class WeatherForecastDayDto
{
    [JsonPropertyName("date")]
    public string Date { get; set; } = "";

    [JsonPropertyName("maxtemp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MaxTempF { get; set; }

    [JsonPropertyName("mintemp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MinTempF { get; set; }

    [JsonPropertyName("maxtemp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MaxTempC { get; set; }

    [JsonPropertyName("mintemp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? MinTempC { get; set; }

    [JsonPropertyName("day")]
    public WeatherDaySummaryDto? Day { get; set; }

    [JsonPropertyName("aqi")]
    public WeatherAqiDto? Aqi { get; set; }

    [JsonPropertyName("alerts")]
    public List<WeatherAlertDto>? Alerts { get; set; }

    [JsonPropertyName("pollen")]
    public List<WeatherPollenDto>? Pollen { get; set; }

    [JsonPropertyName("hour")]
    public List<WeatherHourDto>? Hour { get; set; }

    /// <summary>Day-level rain chance, falling back to the max hourly value.</summary>
    public double? DailyChanceOfRain()
    {
        if (Day?.DailyChanceOfRain is not null)
            return Day.DailyChanceOfRain;
        double? max = null;
        foreach (var h in Hour ?? [])
        {
            var chance = h.ChanceOfRain();
            if (chance is not null && (max is null || chance > max))
                max = chance;
        }
        return max;
    }
}

public sealed class WeatherDaySummaryDto
{
    [JsonPropertyName("daily_chance_of_rain")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? DailyChanceOfRain { get; set; }
}

public sealed class WeatherAqiDto
{
    [JsonPropertyName("avgAqi")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? AvgAqi { get; set; }
}

public sealed class WeatherAlertDto
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("headline")]
    public string? Headline { get; set; }

    [JsonPropertyName("expires")]
    public string? Expires { get; set; }

    /// <summary>Human-readable alert line, e.g. "Severe Thunderstorm Warning until 9 PM CDT".</summary>
    public string DisplayText()
    {
        var name = string.IsNullOrWhiteSpace(Event) ? (Headline ?? "") : Event;
        return string.IsNullOrWhiteSpace(Expires) ? name : $"{name} until {Expires}";
    }
}

public sealed class WeatherPollenDto
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("count")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? Count { get; set; }
}

public sealed class WeatherHourDto
{
    [JsonPropertyName("time")]
    public string Time { get; set; } = "";

    [JsonPropertyName("temp_f")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempF { get; set; }

    [JsonPropertyName("temp_c")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? TempC { get; set; }

    [JsonPropertyName("chanceofrain")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? ChanceOfRainLegacy { get; set; }

    [JsonPropertyName("chance_of_rain")]
    [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
    public double? ChanceOfRainNew { get; set; }

    [JsonPropertyName("condition")]
    public WeatherConditionDto? Condition { get; set; }

    /// <summary>Rain chance from either the classic or current field name.</summary>
    public double? ChanceOfRain() => ChanceOfRainNew ?? ChanceOfRainLegacy;

    /// <summary>Condition text from either field of the condition object.</summary>
    public string? ConditionText() => Condition?.Text ?? Condition?.Value;
}

/// <summary>
/// Small pure-text helpers (error-message extraction and length capping) shared by the
/// skill's request path and kept in this file so tests cover exactly what ships.
/// </summary>
public static class WeatherApiText
{
    /// <summary>Pulls a human-readable message out of a WeatherAPI error payload; falls back to the raw body.</summary>
    public static string ExtractErrorMessage(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err) &&
                err.TryGetProperty("message", out var msg) &&
                msg.GetString() is { Length: > 0 } message)
                return $"WeatherAPI says: {message}";
        }
        catch (JsonException)
        {
            // Fall through to raw-body truncation below.
        }

        return Truncate(body, 200);
    }

    /// <summary>Caps a string at maxChars, trimming and adding an ellipsis when it is truncated.</summary>
    public static string Truncate(string value, int maxChars)
    {
        var trimmed = value.Trim();
        return trimmed.Length <= maxChars ? trimmed : trimmed[..maxChars].TrimEnd() + "…";
    }
}
