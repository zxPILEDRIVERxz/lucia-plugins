namespace WeatherApi.Tests;

/// <summary>
/// Error-payload handling: the "WeatherAPI says: ..." message the skill surfaces to the agent,
/// tested against the real helper in plugins/weather-api/core.cs.
/// </summary>
public sealed class ErrorExtractionTests
{
    [Fact]
    public void ErrorPayload_ExtractsApiMessage()
    {
        // Arrange/Act
        var extracted = WeatherApiText.ExtractErrorMessage("""{"error":{"message":"Invalid API key."}}""");

        // Assert
        Assert.Equal("WeatherAPI says: Invalid API key.", extracted);
        Assert.Equal("<html>bad gateway</html>", WeatherApiText.ExtractErrorMessage("<html>bad gateway</html>"));
    }
}
