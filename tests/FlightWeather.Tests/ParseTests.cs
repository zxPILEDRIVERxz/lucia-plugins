namespace FlightWeather.Tests;

/// <summary>
/// Parsing of the three wire formats the plugin consumes. Fixtures mirror real responses:
/// NWS observations/latest (GeoJSON-ld with unitCode), NWS alerts/active, and Open-Meteo
/// hourly winds (unixtime format, AGL levels that can be null).
/// </summary>
public sealed class ParseTests
{
    private const string KowbObservation = """
        {
          "type": "Feature",
          "geometry": { "type": "Point", "coordinates": [-87.135, 37.667] },
          "properties": {
            "@id": "https://api.weather.gov/stations/KOWB",
            "stationId": "KOWB",
            "stationName": "Owensboro, Owensboro-Daviess County Airport",
            "timestamp": "2026-11-10T14:56:00+00:00",
            "windSpeed": { "unitCode": "wmoUnit:km_h-1", "value": 7.56, "qualityControl": "V" },
            "windGust": { "unitCode": "wmoUnit:km_h-1", "value": null, "qualityControl": "Z" },
            "windDirection": { "unitCode": "wmoUnit:degree_(angle)", "value": 200, "qualityControl": "V" },
            "rawMessage": "KOWB 101456Z 20004KT 10SM CLR 13/09 A3015 RMK AO2 SLP209 T01330094 51005"
          }
        }
        """;

    [Fact]
    public void ParseNwsObservation_RealFixture_ParsesWindDirectionAndAge()
    {
        // (DateTime.Parse("…Z") would convert to local time — pin the value explicitly.)
        var now = new DateTime(2026, 11, 10, 15, 26, 0, DateTimeKind.Utc);

        var obs = FlightWeatherCore.ParseNwsObservation(KowbObservation, now);

        Assert.NotNull(obs);
        Assert.Equal("KOWB", obs!.StationId);
        Assert.Contains("Owensboro", obs.StationName);
        // 7.56 km/h -> 4.70 mph (the station reported 4 knots)
        Assert.Equal(4.70, obs.SustainedMph!.Value, precision: 2);
        Assert.Null(obs.GustMph); // null in the fixture — gusts are only reported when observed
        Assert.Equal(200, obs.DirectionDeg);
        Assert.Equal(30, obs.AgeMinutes);
        Assert.Equal("KOWB 101456Z 20004KT 10SM CLR 13/09 A3015 RMK AO2 SLP209 T01330094 51005", obs.RawMessage);
    }

    [Fact]
    public void ParseNwsObservation_MalformedJson_ReturnsNull()
    {
        Assert.Null(FlightWeatherCore.ParseNwsObservation("this is not json", DateTime.UtcNow));
    }

    [Fact]
    public void ParseNwsObservation_MissingStationIdentity_ReturnsNull()
    {
        Assert.Null(FlightWeatherCore.ParseNwsObservation("""{ "properties": { "timestamp": "2026-11-10T14:56:00Z" } }""", DateTime.UtcNow));
    }

    [Fact]
    public void ParseNwsObservation_MissingTimestamp_MarksAgeUnknown()
    {
        var json = """
            { "properties": { "stationId": "KJFS", "timestamp": null,
              "windSpeed": { "unitCode": "wmoUnit:km_h-1", "value": 8.0 } } }
            """;

        var obs = FlightWeatherCore.ParseNwsObservation(json, DateTime.UtcNow);

        Assert.NotNull(obs);
        Assert.Equal(-1, obs!.AgeMinutes); // -1 = unknown -> verdict treats as stale
    }

    private const string AlertsFixture = """
        {
          "type": "FeatureCollection",
          "features": [
            { "properties": { "event": "Severe Thunderstorm Warning", "severity": "Extreme" } },
            { "properties": { "event": "Dense Fog Advisory", "severity": "Minor" } },
            { "properties": {} }
          ]
        }
        """;

    [Fact]
    public void ParseNwsAlerts_KeepsEventsWithNames_SkipsNameless()
    {
        var alerts = FlightWeatherCore.ParseNwsAlerts(AlertsFixture);

        Assert.Equal(2, alerts.Count);
        Assert.Equal("Severe Thunderstorm Warning", alerts[0].Event);
        Assert.Equal("Extreme", alerts[0].Severity);
        Assert.Equal("Dense Fog Advisory", alerts[1].Event);
        Assert.Equal("Minor", alerts[1].Severity);
    }

    [Fact]
    public void ParseNwsAlerts_MalformedJson_ReturnsEmptyList()
    {
        Assert.Empty(FlightWeatherCore.ParseNwsAlerts("nope"));
    }

    private const string OpenMeteoFixture = """
        {
          "hourly": {
            "time": [1762803600, 1762807200, 1762810800, 1762814400],
            "wind_speed_10m": [10.2, 11.0, 12.5, 9.0],
            "wind_gusts_10m": [14.8, 15.5, null, 13.0],
            "wind_speed_80m": [12.6, 13.1, 13.9, 11.2],
            "wind_speed_120m": [14.5, null, 16.0, 12.8],
            "wind_speed_180m": [null, 15.2, 17.1, 13.5]
          }
        }
        """;

    [Fact]
    public void ParseOpenMeteoHourly_PicksCurrentHourAndAloftMax()
    {
        // now is between index 0 (1762803600) and index 2, so the current hour is index 1.
        var model = FlightWeatherCore.ParseOpenMeteoHourly(OpenMeteoFixture, 1_762_808_000);

        Assert.NotNull(model);
        Assert.True(model!.HasData);
        Assert.Equal(11.0, model.CurrentSustainedMph!.Value, precision: 2);
        Assert.Equal(15.5, model.CurrentGustMph!.Value, precision: 2);
        // max gust over current + next 3 hours (nulls ignored)
        Assert.Equal(15.5, model.NextHoursMaxGustMph!.Value, precision: 2);
        // max aloft sustained at the current hour across 80/120/180 m (nulls ignored)
        Assert.Equal(15.2, model.AloftMaxMph!.Value, precision: 2);
    }

    [Fact]
    public void ParseOpenMeteoHourly_NoTimeArray_ReturnsNull()
    {
        Assert.Null(FlightWeatherCore.ParseOpenMeteoHourly("""{ "hourly": {} }""", 1_762_808_000));
    }

    [Fact]
    public void ParseOpenMeteoHourly_MalformedJson_ReturnsNull()
    {
        Assert.Null(FlightWeatherCore.ParseOpenMeteoHourly("nope", 1_762_808_000));
    }
}
