#load "core.cs"

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;

/// <summary>
/// House briefing: "anything weird in the house today?" plus today's weather. One tool, no
/// parameters (voice-tuned). House signals come from one Home Assistant state snapshot
/// (unreachable entities, silent sensors, off-target thermostats, stuck media players); the
/// weather line comes from Open-Meteo (keyless) for the HA home location, in the HA unit system.
/// All analysis/formatting lives in HouseBriefingCore (plugins/house-briefing/core.cs), which
/// the test project compiles verbatim, so the contract is covered by unit tests. No configuration:
/// everything is derived from Home Assistant itself.
/// </summary>
public sealed class HouseBriefingSkill : IAgentToolProvider, IPluginStatusProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;
    private readonly IHomeAssistantClient? _homeAssistant;

    private string? _locationName;
    private double? _latitude;
    private double? _longitude;
    private string _temperatureUnit = "F";
    private string _statusLine = "House briefing is starting up.";

    private static readonly ActivitySource ActivitySource = new("Lucia.Skills.HouseBriefing", "1.0.0");
    private static readonly Meter Meter = new("Lucia.Skills.HouseBriefing", "1.0.0");
    private static readonly Counter<long> Briefings = Meter.CreateCounter<long>("housebriefing.briefings", "{count}", "Number of house briefings generated.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("housebriefing.failures", "{count}", "Number of fully failed house briefings (both sections missing).");
    private static readonly Histogram<double> DurationMs = Meter.CreateHistogram<double>("housebriefing.duration", "ms", "Duration of house briefings.");

    public string Name => "House briefing";

    public string Description => "Daily house health signals plus today's weather — 'anything weird in the house?' in one voice-friendly report";

    public string? InstructionHint => "when asked for a briefing, what's going on in the house, or whether anything is weird today, call the get_briefing tool and answer with its report; never guess at the state of the house or the weather";

    public string? StatusLine => _statusLine;

    public HouseBriefingSkill(IHttpClientFactory httpClientFactory, ILogger logger, IHomeAssistantClient? homeAssistant = null)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _homeAssistant = homeAssistant;
    }

    public IList<AITool> GetTools() => [AIFunctionFactory.Create(GetBriefingAsync, new AIFunctionFactoryOptions { Name = "get_briefing" })];

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_homeAssistant is null)
        {
            _statusLine = "No Home Assistant client available — the house section is unavailable.";
            _logger.LogWarning("House briefing started without a Home Assistant client; only the weather section may work.");
            return;
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(10));
            var config = await _homeAssistant.GetConfigAsync(cts.Token).ConfigureAwait(false);

            _locationName = config.LocationName;
            if (config.Latitude != 0 || config.Longitude != 0)
            {
                _latitude = config.Latitude;
                _longitude = config.Longitude;
            }
            _temperatureUnit = config.UnitSystem?.Temperature == "C" ? "C" : "F";

            _statusLine = $"Watching the house from Home Assistant (home: {config.LocationName}).";
            _logger.LogInformation("House briefing skill initialized (home: {Home}, unit system: {Unit}).", config.LocationName, _temperatureUnit);
        }
        catch (Exception ex)
        {
            _statusLine = "Home Assistant was unreachable at startup — the house section retries on each briefing.";
            _logger.LogWarning(ex, "House briefing could not read the Home Assistant config at startup.");
        }
    }

    [Description("Produce today's house briefing: house health signals (unreachable devices, sensors that went quiet, thermostats off target, media players stuck) plus today's weather. Takes no parameters — just call it. The report names anything that is missing instead of skipping it; relay what it says plainly.")]
    public async Task<string> GetBriefingAsync(CancellationToken cancellationToken = default)
    {
        Briefings.Add(1);
        var start = Stopwatch.GetTimestamp();
        using var activity = ActivitySource.StartActivity("housebriefing.run");

        HouseBriefingCore.HouseReport? house = null;
        if (_homeAssistant is not null)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(15));
                var states = (await _homeAssistant.GetAllEntityStatesAsync(cts.Token).ConfigureAwait(false)).ToList();

                house = HouseBriefingCore.AnalyzeHouse(
                    states.Select(MapSnapshot).Where(s => s is not null).Select(s => s!).ToList(),
                    new HouseBriefingCore.HouseAnalysisOptions { NowUtc = DateTime.UtcNow, TemperatureUnit = _temperatureUnit });
            }
            catch (Exception ex)
            {
                activity?.SetTag("housebriefing.house", "unreachable");
                _logger.LogWarning(ex, "House briefing could not read Home Assistant states.");
            }
        }

        var weatherLine = await FetchWeatherLineAsync(cancellationToken).ConfigureAwait(false);

        if (house is null && weatherLine is null)
        {
            Failures.Add(1);
            activity?.SetTag("housebriefing.outcome", "empty");
        }
        else
        {
            activity?.SetStatus(ActivityStatusCode.Ok);
        }

        var ms = RecordDuration(start);
        _logger.LogInformation("House briefing in {Ms}ms: house {House}, weather {Weather}.",
            ms, house is null ? "missing" : $"{house.Signals.Count} signals / {house.WatchedCount} watched", weatherLine is null ? "missing" : "ok");

        return HouseBriefingCore.FormatBriefing(house, weatherLine);
    }

    /// <summary>Today's Open-Meteo forecast for the HA home location, or null (logged) on any failure.</summary>
    private async Task<string?> FetchWeatherLineAsync(CancellationToken cancellationToken)
    {
        if (_latitude is not { } lat || _longitude is not { } lon)
            return null;

        var units = _temperatureUnit == "C"
            ? "temperature_unit=celsius&wind_speed_unit=kmh"
            : "temperature_unit=fahrenheit&wind_speed_unit=mph";
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={lat.ToString(CultureInfo.InvariantCulture)}&longitude={lon.ToString(CultureInfo.InvariantCulture)}" +
                  $"&daily=temperature_2m_max,temperature_2m_min,precipitation_probability_max,wind_speed_10m_max&timezone=auto&forecast_days=1&{units}";

        try
        {
            var client = _httpClientFactory.CreateClient("HouseBriefing");
            var json = await client.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var forecast = HouseBriefingCore.ParseWeatherDaily(json, _temperatureUnit);
            if (forecast is null)
            {
                _logger.LogWarning("Open-Meteo returned no usable daily forecast.");
                return null;
            }
            return HouseBriefingCore.FormatWeatherLine(forecast);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "House briefing weather fetch failed: {Url}.", url);
            return null;
        }
    }

    /// <summary>Reduces an HA state to what the analysis needs; null for unmonitored domains.</summary>
    private static HouseBriefingCore.HouseSnapshot? MapSnapshot(HomeAssistantState state)
    {
        var dot = state.EntityId.IndexOf('.');
        if (dot <= 0)
            return null;
        var domain = state.EntityId[..dot];

        // HA timestamps carry a UTC offset; the deserialized DateTime may be Kind=Local — normalize.
        var lastUpdated = state.LastUpdated.ToUniversalTime();
        var lastChanged = state.LastChanged.ToUniversalTime();

        double? currentTemp = null, targetTemp = null;
        if (domain == "climate" && state.Attributes is { } attrs)
        {
            currentTemp = AttrDouble(attrs, "current_temperature");
            targetTemp = AttrDouble(attrs, "temperature");
        }

        return new HouseBriefingCore.HouseSnapshot(state.EntityId, domain, state.State ?? "", lastUpdated, lastChanged, currentTemp, targetTemp);
    }

    private static double? AttrDouble(Dictionary<string, object> attrs, string key)
    {
        if (!attrs.TryGetValue(key, out var value) || value is null)
            return null;
        try
        {
            return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double RecordDuration(long start)
    {
        var ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
        DurationMs.Record(ms);
        return ms;
    }
}

public sealed class HouseBriefingPlugin : ILuciaPlugin, IPluginStatusProvider
{
    private IServiceProvider? _services;

    public string PluginId => "house-briefing";

    /// <summary>Live status for the dashboard: whether the skill registered and what it is watching.</summary>
    public string? StatusLine
    {
        get
        {
            var skill = _services?.GetServices<IAgentToolProvider>().OfType<HouseBriefingSkill>().FirstOrDefault();
            return skill?.StatusLine ?? "House briefing plugin is inactive.";
        }
    }

    /// <summary>No configuration section — location and unit system come from Home Assistant itself.</summary>
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient("HouseBriefing", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.Add("User-Agent", "Lucia/HouseBriefingPlugin (home weather briefing)");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        builder.Services.AddSingleton<IAgentToolProvider>(sp => new HouseBriefingSkill(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<HouseBriefingSkill>(),
            sp.GetService<IHomeAssistantClient>()));
    }

    public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _services = services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HouseBriefingPlugin");
        logger.LogInformation("House briefing plugin active — get_briefing tool registered (Home Assistant + Open-Meteo).");
        return Task.CompletedTask;
    }
}

new HouseBriefingPlugin()
