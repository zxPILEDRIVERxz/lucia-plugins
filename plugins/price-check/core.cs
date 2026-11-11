using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

/// <summary>
/// All price-check logic in one file, in the global namespace (C# scripts cannot declare
/// namespaces). The plugin script #loads this file, and the test project
/// (tests/PriceCheck.Tests) compiles the very same file — so what is tested is
/// exactly what gets installed. Only one top-level type is exported (everything else is
/// nested), so it cannot collide with other plugins; each plugin is its own script compile.
///
/// Data source: CoinGecko /simple/price (keyless, free tier). Prices are rendered as a
/// single voice-friendly sentence; culture is invariant everywhere (deterministic output).
/// </summary>
public static class PriceCheckCore
{
    // ---------------------------------------------------------------- data types

    /// <summary>A coin known to the plugin: its CoinGecko id plus a display name.</summary>
    public sealed record CoinInfo(string Id, string Name);

    /// <summary>A single parsed price quote. Change24hPct is null when the API omitted it.</summary>
    public sealed record PriceQuote(
        string CoinId,
        string? DisplayName,
        double Price,
        double? Change24hPct,
        string Currency);

    // ---------------------------------------------------------------- coin resolution

    /// <summary>
    /// Common coins as (id, name, ticker aliases). Anything not listed here is passed
    /// through as a raw CoinGecko coin id — long-tail coins work if the user knows the id.
    /// </summary>
    private static readonly (string Id, string Name, string[] Aliases)[] Coins =
    [
        ("bitcoin", "Bitcoin", ["btc", "bit"]),
        ("ethereum", "Ethereum", ["eth"]),
        ("tether", "Tether", ["usdt"]),
        ("binancecoin", "BNB", ["bnb"]),
        ("solana", "Solana", ["sol"]),
        ("ripple", "XRP", ["xrp"]),
        ("cardano", "Cardano", ["ada"]),
        ("dogecoin", "Dogecoin", ["doge"]),
        ("avalanche-2", "Avalanche", ["avax"]),
        ("chainlink", "Chainlink", ["link"]),
        ("polkadot", "Polkadot", ["dot"]),
        ("litecoin", "Litecoin", ["ltc"]),
        ("shiba-inu", "Shiba Inu", ["shib"]),
        ("uniswap", "Uniswap", ["uni"]),
        ("aave", "Aave", []),
    ];

    /// <summary>Every coin the plugin recognizes by name or ticker (for hint messages).</summary>
    public static IReadOnlyList<CoinInfo> KnownCoins =>
        Array.ConvertAll(Coins, c => new CoinInfo(c.Id, c.Name));

    /// <summary>
    /// Resolves user input — a name ("bitcoin"), a ticker ("BTC"), or any raw CoinGecko id
    /// ("pepe") — to (id, displayName). DisplayName is null for pass-through ids. Returns
    /// null only when there is nothing to resolve (null/empty input).
    /// </summary>
    public static (string Id, string? DisplayName)? ResolveCoin(string? input)
    {
        if (input is null)
            return null;

        var raw = input.Trim().TrimStart('$').Trim();
        if (raw.Length == 0)
            return null;

        var key = Normalize(raw);
        foreach (var coin in Coins)
        {
            if (key == Normalize(coin.Id) || key == Normalize(coin.Name))
                return (coin.Id, coin.Name);
            foreach (var alias in coin.Aliases)
            {
                if (key == Normalize(alias))
                    return (coin.Id, coin.Name);
            }
        }

        // Pass-through: might be a less common CoinGecko id; the API validates it.
        return (raw.ToLowerInvariant(), null);
    }

    private static string Normalize(string value) => value.ToLowerInvariant().Replace(" ", "-");

    // ---------------------------------------------------------------- parsing

    /// <summary>
    /// Parses a CoinGecko /simple/price response for one coin:
    /// {"bitcoin":{"usd":118234.56,"usd_24h_change":2.41}}. Returns null when the coin key
    /// or a numeric price is missing, or the payload is malformed — never a guess.
    /// </summary>
    public static PriceQuote? ParseQuote(string json, string coinId, string? displayName, string currency)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;

        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root is null)
                return null;

            var coin = root[coinId] as JsonObject;
            if (coin is null)
                return null;

            var price = ReadDouble(coin[currency]);
            if (price is null)
                return null;

            return new PriceQuote(coinId, displayName, price.Value, ReadDouble(coin[$"{currency}_24h_change"]), currency);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts a human-readable message from a CoinGecko error payload:
    /// {"status":{"error_code":400,"error_message":"Invalid coin: xyz"}}. Returns null for
    /// non-error payloads or malformed input.
    /// </summary>
    public static string? ExtractErrorMessage(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject;
            if (root?["status"] is not JsonObject status)
                return null;

            var message = status["error_message"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(message))
                return null;

            return message.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static double? ReadDouble(JsonNode? node)
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

    // ---------------------------------------------------------------- formatting

    /// <summary>
    /// One voice-friendly sentence: "Bitcoin is at $118,234.56, up 2.4% over the last day."
    /// USD gets a $ prefix; other currencies get an uppercase code suffix. Sub-dollar prices
    /// keep extra decimals so small coins stay readable. Capped at <see cref="MaxSentenceChars"/>.
    /// </summary>
    public static string Format(PriceQuote quote)
    {
        const int MaxSentenceChars = 240;

        var name = string.IsNullOrWhiteSpace(quote.DisplayName) ? quote.CoinId : quote.DisplayName!;
        var sb = new StringBuilder($"{name} is at {FormatPrice(quote.Price, quote.Currency)}");

        if (quote.Change24hPct is { } change)
        {
            if (Math.Abs(change) < 0.05)
                sb.Append(", roughly flat over the last day");
            else if (change > 0)
                sb.Append(", up ").Append(FmtPct(change)).Append("% over the last day");
            else
                sb.Append(", down ").Append(FmtPct(-change)).Append("% over the last day");
        }

        sb.Append('.');
        return Truncate(sb.ToString(), MaxSentenceChars);
    }

    /// <summary>Caps text at maxChars, marking truncation with an ellipsis.</summary>
    public static string Truncate(string text, int maxChars)
        => text.Length <= maxChars ? text : text[..maxChars].TrimEnd() + "…";

    private static string FormatPrice(double price, string currency)
    {
        if (price >= 1)
        {
            var number = price.ToString("N2", CultureInfo.InvariantCulture);
            return currency == "usd" ? $"${number}" : $"{number} {currency.ToUpperInvariant()}";
        }

        var small = price.ToString("0.########", CultureInfo.InvariantCulture);
        return currency == "usd" ? $"${small}" : $"{small} {currency.ToUpperInvariant()}";
    }

    private static string FmtPct(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
