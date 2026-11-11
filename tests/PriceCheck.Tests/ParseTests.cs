namespace PriceCheck.Tests;

/// <summary>
/// Parsing CoinGecko /simple/price responses. Success shape:
/// {"bitcoin":{"usd":118234.56,"usd_24h_change":2.41}} — the 24h change is optional.
/// Error shape (HTTP 400 for unknown coins):
/// {"status":{"error_code":400,"error_message":"Invalid coin: xyz"}} — surfaced verbatim.
/// </summary>
public sealed class ParseTests
{
    [Fact]
    public void ParsesPriceAndChange()
    {
        var json = """{"bitcoin":{"usd":118234.56,"usd_24h_change":2.41}}""";

        var quote = PriceCheckCore.ParseQuote(json, "bitcoin", "Bitcoin", "usd");

        Assert.NotNull(quote);
        Assert.Equal("bitcoin", quote!.CoinId);
        Assert.Equal("Bitcoin", quote.DisplayName);
        Assert.Equal(118234.56, quote.Price);
        Assert.Equal(2.41, quote.Change24hPct);
        Assert.Equal("usd", quote.Currency);
    }

    [Fact]
    public void ChangeIsOptional()
    {
        var json = """{"ethereum":{"usd":3456.78}}""";

        var quote = PriceCheckCore.ParseQuote(json, "ethereum", "Ethereum", "usd");

        Assert.NotNull(quote);
        Assert.Equal(3456.78, quote!.Price);
        Assert.Null(quote.Change24hPct);
    }

    [Fact]
    public void ParsesNonUsdCurrency()
    {
        var json = """{"bitcoin":{"eur":95123.45,"eur_24h_change":1.0}}""";

        var quote = PriceCheckCore.ParseQuote(json, "bitcoin", "Bitcoin", "eur");

        Assert.NotNull(quote);
        Assert.Equal(95123.45, quote!.Price);
        Assert.Equal("eur", quote.Currency);
    }

    [Fact]
    public void ReturnsNull_WhenRequestedCoinKeyIsMissing()
    {
        var json = """{"ethereum":{"usd":3456.78}}""";

        Assert.Null(PriceCheckCore.ParseQuote(json, "bitcoin", "Bitcoin", "usd"));
    }

    [Fact]
    public void ReturnsNull_WhenPriceIsNotNumeric()
    {
        var json = """{"bitcoin":{"usd":"hot"}}""";

        Assert.Null(PriceCheckCore.ParseQuote(json, "bitcoin", "Bitcoin", "usd"));
    }

    [Fact]
    public void ReturnsNull_OnMalformedJson()
    {
        Assert.Null(PriceCheckCore.ParseQuote("not json", "bitcoin", "Bitcoin", "usd"));
        Assert.Null(PriceCheckCore.ParseQuote("", "bitcoin", "Bitcoin", "usd"));
    }

    [Fact]
    public void ExtractsErrorMessage_FromErrorPayload()
    {
        var json = """{"status":{"error_code":400,"error_message":"Invalid coin: not-a-coin"}}""";

        Assert.Equal("Invalid coin: not-a-coin", PriceCheckCore.ExtractErrorMessage(json));
    }

    [Fact]
    public void ErrorExtractionReturnsNull_ForNonErrorPayload()
    {
        Assert.Null(PriceCheckCore.ExtractErrorMessage("""{"bitcoin":{"usd":1}}"""));
        Assert.Null(PriceCheckCore.ExtractErrorMessage("garbage"));
    }
}
