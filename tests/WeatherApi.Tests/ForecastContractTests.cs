using System.Net;
using System.Text;
using System.Text.Json;

namespace WeatherApi.Tests;

/// <summary>
/// Contract tests for the WeatherAPI.com forecast.json exchange: the request query and the
/// two response shapes the service has served (classic current_condition[] / forecast[] and
/// the current spec's current object / forecast.forecastday[]). The normalizer and DTOs used
/// here come from plugins/weather-api/core.cs — the very file the plugin #loads.
/// </summary>
public sealed class ForecastContractTests
{
    /// <summary>Asserts a nullable double holds an approximate value, with a clear message when null.</summary>
    private static void AssertDouble(double expected, double? actual) =>
        Assert.True(actual.HasValue && Math.Abs(actual.Value - expected) < 1e-9,
            $"expected {expected}, got {(actual.HasValue ? actual.Value.ToString() : "<null>")}");

    [Fact]
    public async Task ForecastRequest_UrlCarriesAllParameters()
    {
        // Arrange — mirrors the query construction in WeatherApiSkill.GetWeatherAsync
        HttpRequestMessage? capturedRequest = null;
        var handler = new FakeHttpMessageHandler(req =>
        {
            capturedRequest = req;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(MinimalJson, Encoding.UTF8, "application/json")
            };
        });
        var httpClient = new HttpClient(handler);

        const string location = "Chicago, IL";
        const int days = 3;
        var query = new StringBuilder()
            .Append("q=").Append(Uri.EscapeDataString(location))
            .Append("&days=").Append(days)
            .Append("&units=us")
            .Append("&aqi=yes")
            .Append("&alerts=yes")
            .Append("&pollen=yes")
            .Append("&key=").Append(Uri.EscapeDataString("test-api-key"));

        // Act
        var response = await httpClient.GetAsync($"https://api.weatherapi.com/v1/forecast.json?{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // Assert
        Assert.NotNull(capturedRequest);
        var uri = capturedRequest.RequestUri!.AbsoluteUri;
        Assert.Contains("/v1/forecast.json", uri);
        Assert.Contains(Uri.EscapeDataString(location), uri);
        Assert.Contains("days=3", uri);
        Assert.Contains("units=us", uri);
        Assert.Contains("aqi=yes", uri);
        Assert.Contains("alerts=yes", uri);
        Assert.Contains("pollen=yes", uri);
        Assert.Contains("key=test-api-key", uri);
    }

    [Fact]
    public void ClassicShape_FullForecast_NormalizesAndDeserializes()
    {
        // Arrange — classic shape: current_condition[] with string numerics and
        // capitalized temp_C/temp_F, forecast[] array, numeric hour times,
        // per-day aqi / alerts.event[] / pollen list.
        var result = Deserialize(WeatherResponseNormalizer.Normalize(ClassicFullJson));

        // Assert location + current conditions (string numerics, weatherDesc list)
        Assert.NotNull(result);
        Assert.Equal("Chicago", result.Location!.Name);
        Assert.Equal("Illinois", result.Location.Region);
        Assert.Single(result.CurrentCondition);
        var cur = result.CurrentCondition[0];
        AssertDouble(57.0, cur.TempF);
        AssertDouble(13.9, cur.TempC);
        AssertDouble(55.9, cur.FeelslikeF);
        AssertDouble(43, cur.Humidity);
        AssertDouble(8.1, cur.WindMph);
        Assert.Equal("Partly cloudy", cur.ConditionText());
        Assert.Equal("SE", cur.WindDirection);

        // Assert daily entries (string numerics hoisted from day{} where needed)
        Assert.Equal(2, result.Forecast.Count);
        var today = result.Forecast[0];
        Assert.Equal("2026-11-09", today.Date);
        AssertDouble(61.2, today.MaxTempF);
        AssertDouble(48.0, today.MinTempF);
        AssertDouble(35, today.DailyChanceOfRain());
        Assert.NotNull(today.Aqi);
        AssertDouble(42, today.Aqi!.AvgAqi);

        // Alerts: { event: [...] } unwrapped to a bare list
        Assert.NotNull(today.Alerts);
        Assert.Single(today.Alerts!);
        Assert.Equal("Severe Thunderstorm Warning until Mon, 09 Nov 2026 05:00 PM CST", today.Alerts[0].DisplayText());

        // Pollen list with string counts
        Assert.NotNull(today.Pollen);
        Assert.Equal(2, today.Pollen!.Count);
        Assert.Equal("Grass", today.Pollen[0].Name);
        AssertDouble(102, today.Pollen[0].Count);

        // Hourly entries: numeric time normalized to string, chanceofrain parsed
        Assert.NotNull(today.Hour);
        Assert.Equal(8, today.Hour!.Count);
        Assert.Equal("900", today.Hour[3].Time);
        AssertDouble(55.8, today.Hour[3].TempF);
        AssertDouble(30, today.Hour[3].ChanceOfRain());
        Assert.Equal("Partly cloudy", today.Hour[3].ConditionText());
    }

    [Fact]
    public void NewSpecShape_FullForecast_NormalizesAndDeserializes()
    {
        // Arrange — current spec shape: `current` object, forecast.forecastday[]
        // array, numeric temps, condition.text, chance_of_rain, top-level
        // alerts.alert[], pollen object, max/min temps only inside day{}.
        var result = Deserialize(WeatherResponseNormalizer.Normalize(NewSpecFullJson));

        // Assert current conditions wrapped from the `current` object
        Assert.NotNull(result);
        Assert.Single(result.CurrentCondition);
        var cur = result.CurrentCondition[0];
        AssertDouble(57.0, cur.TempF);
        Assert.Equal("Partly cloudy", cur.ConditionText());
        Assert.Equal("SE", cur.WindDirection);

        // Assert top-level alerts.alert[] unwrapped
        Assert.NotNull(result.Alerts);
        Assert.Single(result.Alerts!);
        Assert.Equal("Severe Thunderstorm Warning until 2026-11-09 17:00:00", result.Alerts[0].DisplayText());

        // Assert daily entries with temps hoisted from the day{} sub-object
        var today = result.Forecast[0];
        AssertDouble(61.2, today.MaxTempF);
        AssertDouble(48.0, today.MinTempF);
        AssertDouble(35, today.DailyChanceOfRain());

        // Pollen object converted to list; zero and null counts dropped
        Assert.NotNull(today.Pollen);
        var pollenNames = today.Pollen!.Select(p => p.Name).ToList();
        Assert.Contains("Grass", pollenNames);
        Assert.Contains("Tree", pollenNames);
        Assert.DoesNotContain("Oak", pollenNames);
        Assert.DoesNotContain("Birch", pollenNames);

        // Hourly entries: ISO string time preserved, chance_of_rain parsed
        Assert.NotNull(today.Hour);
        Assert.Equal("2026-11-09T09:00:00", today.Hour![0].Time);
        AssertDouble(30, today.Hour[0].ChanceOfRain());
        Assert.Equal("Partly cloudy", today.Hour[0].ConditionText());
    }

    [Fact]
    public void MinimalShape_OptionalFieldsAreNull_NoThrow()
    {
        var result = Deserialize(WeatherResponseNormalizer.Normalize(MinimalJson));

        Assert.NotNull(result);
        Assert.Equal("Chicago", result.Location!.Name);
        Assert.Empty(result.CurrentCondition);
        Assert.Empty(result.Forecast);
        Assert.Null(result.Alerts);
    }

    private const string MinimalJson = """
        {
            "location": { "name": "Chicago" }
        }
        """;

    private const string ClassicFullJson = """
        {
            "location": {
                "name": "Chicago",
                "region": "Illinois",
                "country": "United States",
                "localtime": "2026-11-09 10:30 AM"
            },
            "current_condition": [
                {
                    "cloudcover": "0",
                    "humidity": "43",
                    "temp_C": "13.9",
                    "temp_F": "57.0",
                    "feelslike_C": "13.3",
                    "feelslike_F": "55.9",
                    "wind_mph": "8.1",
                    "wind_kmph": "13.0",
                    "wind_dir_16_point": "SE",
                    "weatherDesc": [ { "value": "Partly cloudy" } ]
                }
            ],
            "forecast": [
                {
                    "date": "2026-11-09",
                    "maxtemp_C": "16.2",
                    "maxtemp_F": "61.2",
                    "mintemp_C": "8.9",
                    "mintemp_F": "48.0",
                    "day": {
                        "avghumidity": 69,
                        "daily_chance_of_rain": "35"
                    },
                    "aqi": { "maxAqi": 61, "avgAqi": 42 },
                    "alerts": {
                        "event": [
                            { "event": "Severe Thunderstorm Warning", "expires": "Mon, 09 Nov 2026 05:00 PM CST" }
                        ]
                    },
                    "pollen": [
                        { "name": "Grass", "count": "102" },
                        { "name": "Tree", "count": "31" }
                    ],
                    "hour": [
                        { "time": 0,    "temp_C": "9.1",  "temp_F": "48.4", "chanceofrain": "10", "condition": { "text": "Clear", "code": 113 } },
                        { "time": 300,  "temp_C": "8.9",  "temp_F": "48.0", "chanceofrain": "20", "condition": { "text": "Clear", "code": 113 } },
                        { "time": 600,  "temp_C": "10.5", "temp_F": "50.9", "chanceofrain": "35", "condition": { "text": "Partly cloudy", "code": 116 } },
                        { "time": 900,  "temp_C": "13.2", "temp_F": "55.8", "chanceofrain": "30", "condition": { "text": "Partly cloudy", "code": 116 } },
                        { "time": 1200, "temp_C": "15.1", "temp_F": "59.2", "chanceofrain": "25", "condition": { "text": "Sunny", "code": 113 } },
                        { "time": 1500, "temp_C": "16.2", "temp_F": "61.2", "chanceofrain": "15", "condition": { "text": "Sunny", "code": 113 } },
                        { "time": 1800, "temp_C": "14.0", "temp_F": "57.2", "chanceofrain": "10", "condition": { "text": "Clear", "code": 113 } },
                        { "time": 2100, "temp_C": "11.3", "temp_F": "52.3", "chanceofrain": "5",  "condition": { "text": "Clear", "code": 113 } }
                    ]
                },
                {
                    "date": "2026-11-10",
                    "maxtemp_C": "14.0",
                    "maxtemp_F": "57.2",
                    "mintemp_C": "7.0",
                    "mintemp_F": "44.6",
                    "day": { "daily_chance_of_rain": "0" },
                    "hour": [
                        { "time": 0,   "temp_C": "7.1",  "temp_F": "44.8", "chanceofrain": "0",  "condition": { "text": "Clear", "code": 113 } },
                        { "time": 300, "temp_C": "6.9",  "temp_F": "44.4", "chanceofrain": "0",  "condition": { "text": "Clear", "code": 113 } },
                        { "time": 600, "temp_C": "8.2",  "temp_F": "46.8", "chanceofrain": "5",  "condition": { "text": "Clear", "code": 113 } },
                        { "time": 900, "temp_C": "11.0", "temp_F": "51.8", "chanceofrain": "10", "condition": { "text": "Sunny", "code": 113 } },
                        { "time": 1200,"temp_C": "13.1", "temp_F": "55.6", "chanceofrain": "15", "condition": { "text": "Sunny", "code": 113 } },
                        { "time": 1500,"temp_C": "14.0", "temp_F": "57.2", "chanceofrain": "10", "condition": { "text": "Partly cloudy", "code": 116 } },
                        { "time": 1800,"temp_C": "11.2", "temp_F": "52.2", "chanceofrain": "5",  "condition": { "text": "Clear", "code": 113 } },
                        { "time": 2100,"temp_C": "9.4",  "temp_F": "48.9", "chanceofrain": "0",  "condition": { "text": "Clear", "code": 113 } }
                    ]
                }
            ]
        }
        """;

    private const string NewSpecFullJson = """
        {
            "location": {
                "name": "Chicago",
                "region": "Illinois",
                "country": "United States",
                "localtime": "2026-11-09 10:30 AM"
            },
            "current": {
                "last_updated": "2026-11-09 10:30 AM",
                "temp_c": 13.9,
                "temp_f": 57.0,
                "feelslike_c": 13.3,
                "feelslike_f": 55.9,
                "humidity": 43,
                "wind_mph": 8.1,
                "wind_kmph": 13.0,
                "wind_dir": "SE",
                "condition": { "text": "Partly cloudy", "icon": "//cdn.weatherapi.com/weather/64x64/day/116.png", "code": 116 }
            },
            "alerts": {
                "alert": [
                    {
                        "headline": "Severe Thunderstorm Warning for Cook County IL",
                        "event": "Severe Thunderstorm Warning",
                        "severity": "severe",
                        "expires": "2026-11-09 17:00:00"
                    }
                ]
            },
            "forecast": {
                "forecastday": [
                    {
                        "date": "2026-11-09",
                        "day": {
                            "maxtemp_c": 16.2,
                            "maxtemp_f": 61.2,
                            "mintemp_c": 8.9,
                            "mintemp_f": 48.0,
                            "daily_chance_of_rain": 35
                        },
                        "air_quality": { "pm2_5": 11.2, "us-epa-index": 2 },
                        "pollen": { "Grass": 102, "Tree": 31, "Oak": 0, "Birch": null },
                        "hour": [
                            { "time": "2026-11-09T09:00:00", "temp_c": 13.2, "temp_f": 55.8, "chance_of_rain": 30, "condition": { "text": "Partly cloudy", "code": 116 } },
                            { "time": "2026-11-09T12:00:00", "temp_c": 15.1, "temp_f": 59.2, "chance_of_rain": 25, "condition": { "text": "Sunny", "code": 113 } },
                            { "time": "2026-11-09T15:00:00", "temp_c": 16.2, "temp_f": 61.2, "chance_of_rain": 15, "condition": { "text": "Sunny", "code": 113 } }
                        ]
                    },
                    {
                        "date": "2026-11-10",
                        "day": {
                            "maxtemp_c": 14.0,
                            "maxtemp_f": 57.2,
                            "mintemp_c": 7.0,
                            "mintemp_f": 44.6,
                            "daily_chance_of_rain": 0
                        },
                        "hour": [
                            { "time": "2026-11-10T09:00:00", "temp_c": 11.0, "temp_f": 51.8, "chance_of_rain": 10, "condition": { "text": "Sunny", "code": 113 } },
                            { "time": "2026-11-10T12:00:00", "temp_c": 13.1, "temp_f": 55.6, "chance_of_rain": 15, "condition": { "text": "Sunny", "code": 113 } },
                            { "time": "2026-11-10T15:00:00", "temp_c": 14.0, "temp_f": 57.2, "chance_of_rain": 10, "condition": { "text": "Partly cloudy", "code": 116 } }
                        ]
                    }
                ]
            }
        }
        """;

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    private static WeatherForecastResponse? Deserialize(string normalizedJson) =>
        JsonSerializer.Deserialize<WeatherForecastResponse>(normalizedJson, Options);
}
