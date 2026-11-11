#load "core.cs"

using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.Metrics;

/// <summary>
/// Live crypto prices from CoinGecko (keyless, free tier). One tool: get_price(coin, currency)
/// — a name ("bitcoin"), ticker ("BTC"), or any raw CoinGecko coin id works. The response is a
/// single voice-friendly sentence with the price and 24h change. All parsing/formatting logic
/// lives in PriceCheckCore (plugins/price-check/core.cs), which the test project compiles
/// verbatim, so the contract is covered by unit tests. No configuration: nothing to set up.
/// </summary>
public sealed class PriceCheckSkill : IAgentToolProvider, IPluginStatusProvider
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger _logger;

    private static readonly ActivitySource ActivitySource = new("Lucia.Skills.PriceCheck", "1.0.0");
    private static readonly Meter Meter = new("Lucia.Skills.PriceCheck", "1.0.0");
    private static readonly Counter<long> Checks = Meter.CreateCounter<long>("pricecheck.checks", "{count}", "Number of crypto price lookups.");
    private static readonly Counter<long> Failures = Meter.CreateCounter<long>("pricecheck.failures", "{count}", "Number of failed crypto price lookups.");
    private static readonly Histogram<double> DurationMs = Meter.CreateHistogram<double>("pricecheck.duration", "ms", "Duration of crypto price lookups.");

    public string Name => "Crypto price check";

    public string Description => "Live cryptocurrency prices from CoinGecko (keyless)";

    public string? InstructionHint => "when asked about a crypto or coin price ('what's bitcoin at', 'how much is ethereum'), call the get_price tool and answer with its line; never quote a price from memory";

    public string? StatusLine => "Keyless CoinGecko prices — no setup required.";

    public PriceCheckSkill(IHttpClientFactory httpClientFactory, ILogger logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public IList<AITool> GetTools() => [AIFunctionFactory.Create(GetPriceAsync, new AIFunctionFactoryOptions { Name = "get_price" })];

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Crypto price check skill initialized (CoinGecko, keyless — no setup required).");
        return Task.CompletedTask;
    }

    [Description("Look up the current price of a cryptocurrency. coin: a name ('bitcoin'), ticker ('BTC'), or any CoinGecko coin id ('pepe'). currency: ISO currency code for the quote, default 'usd'. Returns one compact line with the price and 24-hour change. For several coins, call once per coin. If the lookup fails, say so plainly instead of guessing a price.")]
    public async Task<string> GetPriceAsync(
        [Description("Coin to look up — name ('bitcoin'), ticker ('BTC'), or any CoinGecko coin id ('pepe').")] string coin = "bitcoin",
        [Description("ISO currency code for the quote (e.g. 'usd', 'eur', 'jpy'). Default 'usd'.")] string currency = "usd",
        CancellationToken cancellationToken = default)
    {
        Checks.Add(1);
        var start = Stopwatch.GetTimestamp();
        using var activity = ActivitySource.StartActivity("pricecheck.lookup");

        var resolved = PriceCheckCore.ResolveCoin(coin);
        if (resolved is null)
            return "No coin was given — ask for a specific one.";

        var (id, display) = resolved.Value;
        var quoteCurrency = (currency ?? "").Trim().ToLowerInvariant();
        if (quoteCurrency.Length == 0)
            quoteCurrency = "usd";

        var url = $"https://api.coingecko.com/api/v3/simple/price?ids={Uri.EscapeDataString(id)}&vs_currencies={Uri.EscapeDataString(quoteCurrency)}&include_24hr_change=true";
        var json = await FetchTextAsync(url, cancellationToken).ConfigureAwait(false);
        if (json is null)
            return "Could not reach CoinGecko right now — try again in a moment.";

        if (PriceCheckCore.ExtractErrorMessage(json) is { } error)
        {
            Failures.Add(1);
            activity?.SetTag("pricecheck.outcome", "error");
            _logger.LogWarning("CoinGecko rejected coin '{Coin}': {Error}", id, error);
            return $"Could not find a price for '{coin}' — CoinGecko says: {error}";
        }

        var quote = PriceCheckCore.ParseQuote(json, id, display, quoteCurrency);
        if (quote is null)
        {
            Failures.Add(1);
            activity?.SetTag("pricecheck.outcome", "no-data");
            return "CoinGecko returned no usable price for that coin.";
        }

        activity?.SetTag("pricecheck.coin", id);
        activity?.SetStatus(ActivityStatusCode.Ok);
        var ms = RecordDuration(start);
        _logger.LogInformation("Price check for {Coin}: {Price} {Currency} in {Ms}ms.", id, quote.Price, quoteCurrency, ms);

        return PriceCheckCore.Format(quote);
    }

    /// <summary>GETs a url and returns the body, or null on any failure (logged; counted as a failure).</summary>
    private async Task<string?> FetchTextAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("PriceCheck");
            var response = await client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Failures.Add(1);
                _logger.LogWarning("Price check fetch failed: {Url} -> HTTP {StatusCode}.", url, (int)response.StatusCode);
                return null;
            }
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Failures.Add(1);
            _logger.LogWarning(ex, "Price check fetch failed: {Url}.", url);
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

public sealed class PriceCheckPlugin : ILuciaPlugin, IPluginStatusProvider
{
    private IServiceProvider? _services;

    public string PluginId => "price-check";

    /// <summary>Live status for the dashboard: whether the skill registered its tool.</summary>
    public string? StatusLine
    {
        get
        {
            var skill = _services?.GetServices<IAgentToolProvider>().OfType<PriceCheckSkill>().FirstOrDefault();
            return skill?.StatusLine ?? "Crypto price check plugin is inactive.";
        }
    }

    /// <summary>No configuration section — CoinGecko needs no key and the tool takes everything as parameters.</summary>
    public void ConfigureServices(IHostApplicationBuilder builder)
    {
        builder.Services.AddHttpClient("PriceCheck", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(12);
            client.DefaultRequestHeaders.Add("User-Agent", "Lucia/PriceCheckPlugin (crypto price checks)");
            client.DefaultRequestHeaders.Add("Accept", "application/json");
        });

        builder.Services.AddSingleton<IAgentToolProvider>(sp => new PriceCheckSkill(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PriceCheckSkill>()));
    }

    public Task ExecuteAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        _services = services;
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("PriceCheckPlugin");
        logger.LogInformation("Crypto price check plugin active — get_price tool registered (CoinGecko, keyless).");
        return Task.CompletedTask;
    }
}

new PriceCheckPlugin()
