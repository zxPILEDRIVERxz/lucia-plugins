namespace PriceCheck.Tests;

/// <summary>
/// Resolving what the user said to a CoinGecko coin id. Common coins are known by name,
/// display name, or ticker alias; anything else passes through as a raw CoinGecko coin id
/// (long-tail coins work if you know the id — CoinGecko validates it for us).
/// </summary>
public sealed class CoinResolverTests
{
    [Theory]
    [InlineData("bitcoin", "bitcoin", "Bitcoin")]
    [InlineData("BTC", "bitcoin", "Bitcoin")]
    [InlineData("$eth", "ethereum", "Ethereum")]
    [InlineData("xrp", "ripple", "XRP")]
    [InlineData("avax", "avalanche-2", "Avalanche")]
    [InlineData("shiba inu", "shiba-inu", "Shiba Inu")]
    [InlineData("  SOL  ", "solana", "Solana")]
    [InlineData("doge", "dogecoin", "Dogecoin")]
    public void ResolvesCommonCoinsByNameTickerOrAlias(string input, string expectedId, string expectedName)
    {
        var resolved = PriceCheckCore.ResolveCoin(input);

        Assert.NotNull(resolved);
        Assert.Equal(expectedId, resolved.Value.Id);
        Assert.Equal(expectedName, resolved.Value.DisplayName);
    }

    [Fact]
    public void UnknownInputPassesThroughAsRawCoinGeckoId()
    {
        var resolved = PriceCheckCore.ResolveCoin("pepe");

        Assert.NotNull(resolved);
        Assert.Equal("pepe", resolved.Value.Id);
        Assert.Null(resolved.Value.DisplayName);
    }

    [Fact]
    public void EmptyInputResolvesToNothing()
    {
        Assert.Null(PriceCheckCore.ResolveCoin(null));
        Assert.Null(PriceCheckCore.ResolveCoin("   "));
        Assert.Null(PriceCheckCore.ResolveCoin("$"));
    }
}
