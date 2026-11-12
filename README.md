# lucia-plugins

Third-party plugins for [Lucia](https://github.com/seiggy/lucia-dotnet), the privacy-first
Home Assistant voice assistant. Each plugin is a self-contained folder with a `plugin.cs`
script (and optionally a few more files) — no build step, no API keys required unless a
plugin says so.

## Installing a plugin from this repository

In the Lucia dashboard: **Plugins → Add plugin repository**, then enter:

```
https://github.com/zxPILEDRIVERxz/lucia-plugins
```

Lucia fetches `lucia-plugins.json` (this repo's manifest), shows the plugins it lists, and
downloads the chosen plugin folder. The plugin's tools become available after **restarting
Lucia**.

> **Jetson/pod deployments:** the Lucia container declares a volume at `/app/plugins`. If
> your deployment does not attach a persistent volume there, repository-installed plugins
> are removed when the pod restarts. Make sure `/app/plugins` is backed by a persistent
> volume.

## Plugins

### Flight weather (`flight-weather`)

Answers "can I fly?" for paramotor and ultralight flights with a safety verdict:

| Verdict | Meaning |
| --- | --- |
| **FLYABLE** | Winds are within your limits and no relevant alerts are active. |
| **MARGINAL** | Flyable with caution — winds near your limits, a stale observation, or a softer alert (fog, thunderstorms, wind advisory). |
| **NO-GO** | A hard-block alert is active (tornado, severe thunderstorm, high wind, blizzard, ice storm, hurricane/tropical storm), or sustained/gusts are beyond your limits — including gusts the model expects to build within a few hours. |
| **NO DATA** | Nothing usable came back from any source. The plugin never claims flyability on nothing. |

**Data sources** (all keyless):

- **NWS** (`api.weather.gov`) — the latest observation from the nearest weather station to
  your Home Assistant home coordinates (or a station you configure by ICAO code). Observed
  gusts are preferred over modeled ones; when the station reports no gust, a modeled gust is
  used as a proxy and the verdict says so.
- **NWS active alerts** for the home point — hard-block events are an automatic NO-GO.
- **Open-Meteo** hourly winds as a backup and for aloft context (winds at 80/120/180 m AGL,
  which match typical paramotor altitudes).

**Configuration** (dashboard → Plugins → Flight weather; restart after changes):

| Setting | Default | Meaning |
| --- | --- | --- |
| `Station` | *(empty)* | ICAO override, e.g. `KOWB`. Empty = auto-detect the nearest station to your home location. |
| `UseHomeLocation` | `true` | Use Home Assistant home coordinates for alerts and modeled winds. |
| `MaxSustainedMph` | `15` | Your sustained-wind limit (mph). |
| `MaxGustMph` | `25` | Your gust limit (mph). |
| `StaleAfterMinutes` | `90` | Observations older than this count as stale (→ MARGINAL). |
| `IncludeModelWinds` | `true` | Include Open-Meteo backup/aloft winds. |
| `IncludeAlerts` | `true` | Include NWS alerts in the check. |

**Finding a station's ICAO code:** search "ICAO {your town}" (e.g. "ICAO Owensboro"), or
browse [AirNav](https://airnav.com/); US codes start with `K`.

**Safety note:** this plugin reports data and applies *your* limits; it is not a substitute
for an aeronautical weather briefing (1-800-WX-BRIEF) or your own judgment. Gusts are the
dangerous failure mode in light aircraft, so observed gusts win over modeled ones, and
modeled gusts are always labeled as such.

### WeatherAPI.com weather (`weather-api`)

Current conditions and a multi-day forecast (high/low, rain chance, alerts, AQI, pollen,
next-hours peek) from the [WeatherAPI.com](https://www.weatherapi.com/) forecast API.

**Configuration** (dashboard → Plugins → WeatherAPI.com Weather; restart after changes):

| Setting | Default | Meaning |
| --- | --- | --- |
| `ApiKey` | *(empty)* | Your WeatherAPI.com key (free tier at [weatherapi.com/my-account/apikey](https://www.weatherapi.com/my-account/apikey)). Empty = the plugin loads but registers no tool. |
| `DefaultLocation` | *(empty)* | Override for the default location — city, postal code, or lat,lon. Wins over the automatic home location. |
| `UseHomeLocation` | `true` | Use Home Assistant home coordinates when no place is named (resolved at startup; also verifies the key). |
| `DefaultDays` | `3` | Forecast days by default (1-14). |
| `Units` | `us` | `us` or `metric`. |
| `IncludeAqi` / `IncludePollen` / `IncludeAlerts` | `true` | Request those extras from the API. |
| `ShowHourlyPeek` | `6` | How many of the next 3-hour blocks to include (0 = off). |
| `MaxResponseChars` | `650` | Hard cap on what the tool returns to the agent (120-4000); hourly peek and extras are dropped first. |

The location parameter also accepts the literal word **`home`** (case-insensitive, e.g. "my home") — that always means the configured Home Assistant home, never a city geocoded from the word.

### Crypto price check (`price-check`)

Live cryptocurrency prices from [CoinGecko](https://www.coingecko.com/en/api) — **keyless**,
no configuration at all. Example: *"What's Bitcoin at?"* →
`Bitcoin is at $118,234.56, up 2.4% over the last day.`

- **Coins:** common ones work by name or ticker (`bitcoin`, `BTC`, `xrp`, `doge`, …); anything
  else is passed through as a raw CoinGecko coin id (e.g. `pepe`), so long-tail coins work too.
- **Currency:** any ISO code as a parameter (`usd` by default, `eur`, `jpy`, …).
- **Scope:** the free public API is rate-limited — fine for casual questions, not for
  high-frequency polling. One call per coin; the agent makes several calls for multi-coin
  questions.

### House briefing (`house-briefing`)

*"Anything weird in the house today?"* — one compact voice report from your Home Assistant
plus today's weather. No configuration at all: the home location and unit system are read
from HA itself, and the weather comes from [Open-Meteo](https://open-meteo.com/) (keyless).

- **House signals** (one state snapshot): unreachable entities (`unavailable`/`unknown`),
  sensors that went quiet (no update in 12+ h), thermostats off target (>2 °F / >1 °C from the
  setpoint, unit-aware), and media players stuck "playing" for 6+ h. A full report names up to
  six findings plus the true count; a quiet house gets one sentence.
- **Weather line:** high/low, precipitation odds (rain vs. snow guessed by the freezing
  point), and max wind — in °F/mph or °C/km/h per your HA unit system.
- **Failure policy:** each section degrades on its own — unreachable HA still delivers the
  weather (and says so); a dead weather call still delivers the house report. Only when both
  are missing does the tool admit it couldn't put a briefing together. It never guesses.

Example: `2 things look off. Garage door hasn't checked in for 30 hours. Upstairs thermostat
is 3° above target. Today: high 55°, low 40°, some chance of rain.`

## Repository layout

```
lucia-plugins.json            manifest listing every plugin (fetched by Lucia)
plugins/<id>/plugin.cs        the plugin script (loaded by Lucia's Roslyn script host)
plugins/<id>/core.cs          optional logic files, #loaded by plugin.cs
tests/<Name>.Tests/           unit tests — they compile the same core.cs the plugin loads
```

Each plugin's logic lives in its `core.cs` (global namespace, because C# plugin scripts
cannot declare namespaces) and the test project compiles that exact file — so what is
tested is exactly what gets installed:

```sh
dotnet test tests/FlightWeather.Tests
dotnet test tests/WeatherApi.Tests
dotnet test tests/PriceCheck.Tests
dotnet test tests/HouseBriefing.Tests
```
