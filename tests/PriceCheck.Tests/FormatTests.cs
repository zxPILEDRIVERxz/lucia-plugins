namespace PriceCheck.Tests;

/// <summary>
/// The voice-friendly sentence. Shape: "{Name} is at {price}, up/down/flat {pct}% over the
/// last day." USD gets a $ prefix, other currencies get an uppercase code suffix; small
/// prices keep extra decimals so $0.01 coins stay readable; culture is invariant (deterministic).
/// </summary>
public sealed class FormatTests
{
    private static PriceCheckCore.PriceQuote Quote(
        double price,
        double? change,
        string currency = "usd",
        string coinId = "bitcoin",
        string? display = "Bitcoin") =>
        new(coinId, display, price, change, currency);

    [Fact]
    public void FormatsUsdPriceWithChange()
    {
        Assert.Equal(
            "Bitcoin is at $118,234.56, up 2.4% over the last day.",
            PriceCheckCore.Format(Quote(118234.56, 2.41)));
    }

    [Fact]
    public void FormatsSmallPriceWithExtraDecimals()
    {
        Assert.Equal(
            "Shiba Inu is at $0.01234, down 1.2% over the last day.",
            PriceCheckCore.Format(Quote(0.01234, -1.2, "usd", "shiba-inu", "Shiba Inu")));
    }

    [Fact]
    public void OmitsChangeWhenUnknown()
    {
        Assert.Equal(
            "Ethereum is at $3,456.78.",
            PriceCheckCore.Format(Quote(3456.78, null, "usd", "ethereum", "Ethereum")));
    }

    [Fact]
    public void NearZeroChangeIsRoughlyFlat()
    {
        Assert.Equal(
            "Bitcoin is at $118,234.56, roughly flat over the last day.",
            PriceCheckCore.Format(Quote(118234.56, 0.03)));
    }

    [Fact]
    public void NonUsdCurrencyUsesUppercaseCode()
    {
        Assert.Equal(
            "Bitcoin is at 95,123.45 EUR, up 1% over the last day.",
            PriceCheckCore.Format(Quote(95123.45, 1.0, "eur")));
    }

    [Fact]
    public void UnknownDisplayFallsBackToCoinId()
    {
        Assert.Equal(
            "pepe is at $0.00001234, up 5% over the last day.",
            PriceCheckCore.Format(Quote(0.00001234, 5.0, "usd", "pepe", null)));
    }

    [Fact]
    public void TruncatesOverLongTextWithEllipsis()
    {
        Assert.Equal("abcde…", PriceCheckCore.Truncate("abcdefghij", 5));
        Assert.Equal("abcd", PriceCheckCore.Truncate("abcd", 5));
    }
}
