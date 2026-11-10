#load "core.cs"

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// Parsed flight-weather settings (dashboard config section "FlightWeather").
/// </summary>
public sealed class FlightWeatherOptions
{
    public string Station { get; init; } = "";
    public bool UseHomeLocation { get; init; } = true;
    public int MaxSustainedMph { get; init; } = 15;
    public int MaxGustMph { get; init; } = 25;
    public int StaleAfterMinutes { get; init; } = 90;
    public bool IncludeModelWinds { get; init; } = true;
    public bool IncludeAlerts { get; init; } = true;
    public int TimeoutSeconds { get; init; } = 12;
}

/// <summary>
/// The home location read from Home Assistant's system configuration
/// (the user's onboarding coordinates) plus a human-readable display name.
/// </summary>
public sealed record FlightWeatherHomeLocation(double Latitude, double Longitude, string DisplayName);

/// <summary>
/// Flyability check for paramotor and ultralight flights. Data sources (all keyless):
/// - NWS latest observation for the nearest station (or a configured ICAO override) — preferred
///   because gusts are only trusted when actually observed;
/// - NWS active alerts for the home point — hard-block events (tornado, severe thunderstorm, ...)
///   are an automatic no-go;
/// - Open-Meteo hourly winds as backup (flagged "modeled") and for aloft context at 80/120/180 m.
/// The verdict itself lives in FlightWeatherCore (plugins/flight-weather/core.cs), which the test
/// project compiles verbatim, so the safety logic is covered by unit tests.
/// </summary>
public sealed class FlightWeatherSkill : IAgentToolProvider, IPluginStatusProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly FlightWeatherOptions _options;
    private readonly ILogger _logger;
    private readonly IHomeAssistantClient? _homeAssistant;

    private static readonly ActivitySource ActivitySource = new("Lucia.Skills.FlightWeather", "1.0.0");
    private static readonly Meter Meter = new("Lucia.Skills.FlightWeather", "1.0.0");
    private static readonly Counter<long> Checks = Meter.CreateCounter<long>("flightweather.checks", "{count}", "Number of flight-weather flyability checks.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("flightweather.failures", "{count}", "Number of failed flight-weather data fetches.");
    private static readonly Histogram<double> DurationMs = Meter.CreateHistogram<double>("flightweather.duration", "ms", "Duration of flight-weather flyability checks.");

    private readonly List<string> _startupNotes = [];
    private FlightWeatherHomeLocation? _home;
    private string? _autoStation;
    private string? _autoStationName;
    private string? _statusLine;

    public string Name => "Flight weather";

    public string Description => "Paramotor and ultralight flyability check using live station winds, gusts, and NWS alerts";

    public string? InstructionHint => "when asked whether it is safe to fly a paramotor or ultralight (or 'can I fly', 'flyability check'), call the check_flight_conditions tool and answer with its verdict; never guess flyability from general weather knowledge";

    public string? StatusLine => _statusLine;

    public FlightWeatherSkill(IHttpClientFactory httpClientFactory, FlightWeatherOptions options, ILogger logger, IHomeAssistantClient? homeAssistant = null)
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
        _homeAssistant = homeAssistant;
    }

    public IList<AITool> GetTools() => [AIFunctionFactory.Create(CheckFlightConditionsAsync, new AIFunctionFactoryOptions { Name = "check_flight_conditions" })];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_options.UseHomeLocation)
            await TryLoadHomeLocationAsync(cancellationToken).ConfigureAwait(false);
        else
            _startupNotes.Add("home location disabled — alerts and modeled winds are only available from the station's own position");

        if (string.IsNullOrWhiteSpace(_options.Station))
            await TryDetectNearestStationAsync(cancellationToken).ConfigureAwait(false);

        var stationDisplay = !string.IsNullOrWhiteSpace(_options.Station)
            ? _options.Station
            : (_autoStation is null ? "<none detected>" : $"{_autoStation}{(string.IsNullOrWhiteSpace(_autoStationName) ? "" : $" ({_autoStationName})")}");

        _statusLine = $"Checking {stationDisplay}. Limits: {_options.MaxSustainedMph} mph sustained / {_options.MaxGustMph} mph gust."
            + (_startupNotes.Count > 0 ? " " + string.Join(" ", _startupNotes) : "");

        _logger.LogInformation("Flight weather skill initialized (station: {Station}, limits: {MaxSustained} mph sustained / {MaxGust} mph gust).",
            stationDisplay, _options.MaxSustainedMph, _options.MaxGustMph);
    }

    [Description("Check whether it is flyable for a paramotor or ultralight near home right now. Use when asked 'can I fly', 'is it safe to fly', or 'flyability check'. Returns a verdict — FLYABLE, MARGINAL, NO-GO, or NO DATA — based on the latest station wind and gusts plus NWS alerts for the home area. Takes no parameters; it always checks the configured station/home area. If the verdict is NO DATA, say so plainly instead of guessing.")]
    public async Task<string> CheckFlightConditionsAsync(CancellationToken cancellationToken = default)
    {
        Checks.Add(1);
        var start = Stopwatch.GetTimestamp();
        using var activity = ActivitySource.StartActivity("flightweather.check");

        var station = string.IsNullOrWhiteSpace(_options.Station) ? _autoStation : _options.Station;
        if (string.IsNullOrWhiteSpace(station))
            return "No weather station is available for the flyability check — none is configured and none was auto-detected. Set a Station code (e.g. KOWB) in the Lucia dashboard under Plugins > Flight weather.";

        var obsJson = await FetchTextAsync($"https://api.weather.gov/stations/{station}/observations/latest", cancellationToken).ConfigureAwait(false);

        // Coordinates for alerts and modeled winds: Home Assistant home first, then the station's
        // own position from the observation payload.
        var coords = HomeCoordinates() ?? ParseStationGeometry(obsJson);

        string? alertsJson = null;
        if (_options.IncludeAlerts && coords is not null)
            alertsJson = await FetchTextAsync($"https://api.weather.gov/alerts/active?point={coords.Value.Item1:F6},{coords.Value.Item2:F6}&status=actual", cancellationToken).ConfigureAwait(false);

        string? modelJson = null;
        if (_options.IncludeModelWinds && coords is not null)
            modelJson = await FetchTextAsync($"https://api.open-meteo.com/v1/forecast?latitude={coords.Value.Item1:F6}&longitude={coords.Value.Item2:F6}&hourly=wind_speed_10m,wind_gusts_10m,wind_speed_80m,wind_speed_120m,wind_speed_180m&forecast_hours=4&timeformat=unixtime&wind_speed_unit=mph", cancellationToken).ConfigureAwait(false);

        var nowUtc = DateTime.UtcNow;
        var obs = obsJson is null ? null : FlightWeatherCore.ParseNwsObservation(obsJson, nowUtc);
        var alerts = _options.IncludeAlerts
            ? FlightWeatherCore.ParseNwsAlerts(alertsJson ?? "")
            : new List<FlightWeatherCore.WeatherAlert>();
        var model = _options.IncludeModelWinds && modelJson is not null
            ? FlightWeatherCore.ParseOpenMeteoHourly(modelJson, new DateTimeOffset(nowUtc).ToUnixTimeSeconds())
            : null;

        var result = FlightWeatherCore.Evaluate(
            obs, model, alerts,
            _options.MaxSustainedMph, _options.MaxGustMph, _options.StaleAfterMinutes);

        if (result.Verdict == FlightWeatherCore.Verdict.NoData)
            Failures.Add(1);

        activity?.SetTag("flightweather.station", station);
        activity?.SetTag("flightweather.verdict", result.Verdict.ToString());
        activity?.SetStatus(ActivityStatusCode.Ok);
        var ms = RecordDuration(start);
        _logger.LogInformation("Flight weather check at {Station}: {Verdict} in {Ms}ms.", station, result.Verdict, ms);

        return RenderReport(result, obs, model);
    }

    /// <summary>
    /// Reads the home coordinates from Home Assistant's system configuration.
    /// Every failure mode is non-fatal and surfaced via <see cref="StatusLine"/> on the dashboard.
    /// </summary>
    private async Task TryLoadHomeLocationAsync(CancellationToken cancellationToken)
    {
        if (_homeAssistant is null)
        {
            _startupNotes.Add("no Home Assistant connection — home location unavailable");
            return;
        }

        ConfigResponse config;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            config = await _homeAssistant.GetConfigAsync(cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read the Home Assistant home location; flight weather falls back to station coordinates.");
            _startupNotes.Add("Home Assistant is unreachable — home location unavailable");
            return;
        }

        if (config.Latitude == 0 && config.Longitude == 0)
        {
            _startupNotes.Add("Home Assistant has no home coordinates set");
            return;
        }

        _home = new FlightWeatherHomeLocation(config.Latitude, config.Longitude, config.LocationName);
        _logger.LogInformation("Flight weather home location from Home Assistant: {Display} ({Lat}, {Lon}).",
            config.LocationName, config.Latitude, config.Longitude);
    }

    /// <summary>
    /// Auto-detects the nearest NWS observation station to the home coordinates via
    /// /points/{lat},{lon} -> properties.observationStations (feature list sorted by distance).
    /// </summary>
    private async Task TryDetectNearestStationAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_options.Station))
            return;

        if (_home is null || (_home.Latitude == 0 && _home.Longitude == 0))
        {
            _startupNotes.Add("no station configured and no home location — set a Station code in the plugin config");
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(20));
            var token = cts.Token;

            var pointsUrl = $"https://api.weather.gov/points/{_home.Latitude:F6},{_home.Longitude:F6}";
            var pointsJson = await FetchTextAsync(pointsUrl, token).ConfigureAwait(false);
            if (pointsJson is null)
            {
                _startupNotes.Add("could not detect a nearby station");
                return;
            }

            var stationsUrl = JsonNode.Parse(pointsJson)?["properties"]?["observationStations"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(stationsUrl))
            {
                _startupNotes.Add("NWS found no observation stations near the home location");
                return;
            }

            var featureCollection = await FetchTextAsync(stationsUrl, token).ConfigureAwait(false);
            if (featureCollection is null)
            {
                _startupNotes.Add("could not detect a nearby station");
                return;
            }

            JsonNode? firstFeature = null;
            if (JsonNode.Parse(featureCollection)?["features"] is JsonArray features && features.Count > 0)
                firstFeature = features[0]; // sorted by distance

            var stationId = firstFeature?["properties"]?["stationIdentifier"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(stationId))
            {
                _startupNotes.Add("could not detect a nearby station");
                return;
            }

            _autoStation = stationId;
            _autoStationName = firstFeature?["properties"]?["name"]?.GetValue<string>();
            _logger.LogInformation("Flight weather auto-detected nearest station {Station} ({Name}).", stationId, _autoStationName ?? "");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not auto-detect a nearby NWS observation station.");
            _startupNotes.Add("could not detect a nearby station");
        }
    }

    /// <summary>Speech-friendly report: the verdict first, then the wind picture and the reasons.</summary>
    private static string RenderReport(FlightWeatherCore.VerdictResult result, FlightWeatherCore.WindObservation? obs, FlightWeatherCore.ModelWinds? model)
    {
        var sb = new StringBuilder();
        switch (result.Verdict)
        {
            case FlightWeatherCore.Verdict.Flyable:
                sb.Append("FLYABLE");
                break;
            case FlightWeatherCore.Verdict.Marginal:
                sb.Append("MARGINAL — flyable with caution");
                break;
            case FlightWeatherCore.Verdict.NoGo:
                sb.Append("NO-GO");
                break;
            default:
                sb.Append("NO DATA — cannot confirm flyability");
                break;
        }
        if (obs is not null)
            sb.Append(" at ").Append(obs.StationId);
        sb.Append(". ");

        var stationWindPresent = obs is not null && (obs.SustainedMph is not null || obs.GustMph is not null);
        if (stationWindPresent)
        {
            var dir = FlightWeatherCore.DirectionWord(obs!.DirectionDeg);
            sb.Append("Wind ").Append(Fmt(obs.SustainedMph)).Append(" mph");
            if (dir is not null)
                sb.Append(" from ").Append(dir);
            sb.Append(obs.GustMph is not null ? ", gusting " + Fmt(obs.GustMph) : ", no observed gust");
            if (obs.AgeMinutes >= 0)
                sb.Append(", observed ").Append(obs.AgeMinutes).Append(" minutes ago");
            sb.Append(". ");
        }
        else if (model is not null && model.HasData)
        {
            sb.Append("No usable station wind data — modeled winds used where noted. ");
        }
        else if (obs is not null)
        {
            sb.Append("The station reported no usable wind. ");
        }

        if (result.Reasons.Count > 0)
            sb.Append(string.Join(" ", result.Reasons));

        return sb.ToString().Trim();
    }

    private (double Latitude, double Longitude)? HomeCoordinates()
    {
        if (!_options.UseHomeLocation || _home is null)
            return null;
        if (_home.Latitude == 0 && _home.Longitude == 0)
            return null;
        return (_home.Latitude, _home.Longitude);
    }

    /// <summary>Extracts the station's own position from an NWS observation payload (geometry.coordinates = [lon, lat]).</summary>
    private static (double Latitude, double Longitude)? ParseStationGeometry(string? observationJson)
    {
        if (string.IsNullOrWhiteSpace(observationJson))
            return null;
        try
        {
            var node = JsonNode.Parse(observationJson);
            if (node?["geometry"]?["coordinates"] is not JsonArray coords || coords.Count < 2)
                return null;
            var lon = coords[0]?.GetValue<double?>();
            var lat = coords[1]?.GetValue<double?>();
            if (lat is null || lon is null)
                return null;
            return (lat.Value, lon.Value);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>GETs a url and returns the body, or null on any failure (logged; counted as a failure).</summary>
    private async Task<string?> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("FlightWeather");
            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Failures.Add(1);
                _logger.LogWarning("Flight weather fetch failed: {Url} -> HTTP {StatusCode}.", url, (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failures.Add(1);
            _logger.LogWarning(ex, "Flight weather fetch failed: {Url}.", url);
            return null;
        }
    }

    /// <summary>Formats a wind speed without a trailing .0 (voice friendly).</summary>
    private static string Fmt(double? value)
    {
        if (value is null)
            return "";
        return value.Value % 1 == 0 ? ((long)value.Value).ToString() : value.Value.ToString("0.#");
    }

    private static double RecordDuration(long start)
    {
        var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        DurationMs.Record(ms);
        return ms;
    }
}

public sealed class FlightWeatherPlugin : ILuciaPlugin, IPluginStatusProvider
{
    private IServiceProvider? _services;

    public string PluginId => "flight-weather";

    public string? ConfigSection => "FlightWeather";
    public string? ConfigDescription => "Flyability-check settings for the flight weather plugin. Restart Lucia after changing these so the flight-weather tool picks them up.";

    public IReadOnlyList<PluginConfigProperty> ConfigProperties =>
    [
        new("Station", "string", "Optional ICAO station code override (e.g. KOWB). Leave empty to auto-detect the nearest station to your Home Assistant home.", ""),
        new("UseHomeLocation", "string", "Use the Home Assistant home location for alerts and modeled winds ('true' or 'false')", "true"),
        new("MaxSustainedMph", "string", "Maximum sustained wind in mph you are willing to fly in (5-40)", "15"),
        new("MaxGustMph", "string", "Maximum gust in mph you are willing to fly in (8-60)", "25"),
        new("StaleAfterMinutes", "string", "Treat station observations older than this many minutes as stale (10-720)", "90"),
        new("IncludeModelWinds", "string", "Include Open-Meteo modeled winds as backup and for aloft context ('true' or 'false')", "true"),
        new("IncludeAlerts", "string", "Include NWS weather alerts in the check ('true' or 'false')", "true"),
    ];

    /// <summary>Live status for the dashboard: which station is checked, the limits, and any startup issues.</summary>
    public string? StatusLine
    {
        get
        {
            var skill = _services?.GetServices<IAgentToolProvider>().OfType<FlightWeatherSkill>().FirstOrDefault();
            return skill?.StatusLine ?? "Flight weather plugin is inactive.";
        }
    }

    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;

        var options = new FlightWeatherOptions
        {
            Station = (config["FlightWeather:Station"] ?? "").Trim(),
            UseHomeLocation = Bool(config["FlightWeather:UseHomeLocation"], true),
            MaxSustainedMph = BoundedInt(config["FlightWeather:MaxSustainedMph"], fallback: 15, min: 5, max: 40),
            MaxGustMph = BoundedInt(config["FlightWeather:MaxGustMph"], fallback: 25, min: 8, max: 60),
            StaleAfterMinutes = BoundedInt(config["FlightWeather:StaleAfterMinutes"], fallback: 90, min: 10, max: 720),
            IncludeModelWinds = Bool(config["FlightWeather:IncludeModelWinds"], true),
            IncludeAlerts = Bool(config["FlightWeather:IncludeAlerts"], true),
        };

        builder.Services.AddHttpClient("FlightWeather", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(BoundedInt(config["FlightWeather:TimeoutSeconds"], fallback: 12, min: 2, max: 60));
            // NWS returns 403 to clients without a descriptive User-Agent.
            client.DefaultRequestHeaders.Add("User-Agent", "Lucia/FlightWeatherPlugin (paramotor flyability checks)");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        builder.Services.AddSingleton<IAgentToolProvider>(sp => new FlightWeatherSkill(
            sp.GetRequiredService<IHttpClientFactory>(),
            options,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<FlightWeatherSkill>(),
            sp.GetService<IHomeAssistantClient>()));
    }

    public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _services = services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("FlightWeatherPlugin");
        logger.LogInformation("Flight weather plugin active — flyability tool registered.");
        return Task.CompletedTask;
    }

    private static int BoundedInt(string? value, int fallback, int min, int max)
    {
        var parsed = int.TryParse(value?.Trim(), out var v) ? v : fallback;
        return Math.Clamp(parsed, min, max);
    }

    private static bool Bool(string? value, bool fallback)
    {
        switch ((value ?? "").Trim().ToLowerInvariant())
        {
            case "1": case "true": case "yes": case "y": case "on": case "enabled": return true;
            case "0": case "false": case "no": case "n": case "off": case "disabled": return false;
            default: return fallback;
        }
    }
}

new FlightWeatherPlugin()
