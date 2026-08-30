# TemperHumAlpaca

A small, self-contained Windows utility that reads **selected, explicitly supported** PCsensor TEMPerHUM/TEMPerX USB HID sensors and exposes temperature, relative humidity and calculated dew point as an ASCOM Alpaca `ObservingConditions` device.

TEMPer/TEMPerHUM branding has been used across multiple hardware and firmware revisions. TemperHumAlpaca therefore does **not** assume that every device sold as “TEMPerHUM” uses the same HID identifiers or protocol.

## Supported hardware

The currently validated device profile is:

| Profile | VID:PID | Tested interface | Expected reports | Protocol |
| --- | --- | --- | --- | --- |
| `pcsensor-413d-2107-temperx-v31` | `413D:2107` | `MI_01` preferred | input ≥9, output ≥9 | TEMPerX_V3.1-style |

This is the hardware physically validated during development. The tested Windows unit exposes two HID interfaces; `MI_01` has 9-byte input/output reports and carries the temperature/humidity measurements.

`413D:2107` is also used by other PCsensor products, so VID/PID alone is **not** considered sufficient proof of compatibility. Auto-detection additionally checks the expected HID report shape before the measurement protocol is used.

Other TEMPer-family identifiers seen in public tooling, including `1A86:E025` and `0C45:7402`, are currently treated as **diagnostic candidates only**. TemperHumAlpaca will report them in probe output but will not send the `413D:2107` measurement protocol to them until a matching device profile has been implemented and validated.

## v0.6.0

v0.6.0 adds the physically validated N.I.N.A. integration:

- separate `TemperHumAlpaca.NinaPlugin.dll` targeting the N.I.N.A. 3.2 stable plugin API
- dockable **TEMPerHUM Dew Monitor** panel in N.I.N.A.'s Imaging workspace
- live service/sensor state, temperature, humidity, dew point and dew margin
- dew-margin trend and risk display
- current AstroZap power/knob guidance
- overnight set-and-leave power/knob guidance and forecast details
- transition-based N.I.N.A. alerts for worsening dew risk, heater increases, sensor disconnect and service loss
- configurable alert threshold and repeat cooldown
- optional direct Telegram delivery using an existing bot token/chat ID
- Windows-DPAPI encrypted Telegram bot-token storage
- **Test Telegram** and clear-token controls
- release packaging that includes the plugin inside the normal Windows ZIP and also publishes a plugin-only ZIP

The N.I.N.A. plugin reads the loopback status API; it does not access the USB sensor directly. The Windows service remains the single source of truth for measurements, calibration, dew calculations and forecasting. See `docs/NINA_PLUGIN.md` for installation and settings details.

## v0.5.0

v0.5.0 adds:

- dew-risk classification
- estimated AstroZap dual-channel heater power
- approximate Low-to-High knob position
- two-hour in-memory dew-margin history and dashboard chart
- trend detection after sufficient history is collected
- modest trend-based heater adjustment
- hysteresis to reduce recommendation flicker
- optional UK Met Office 2 km overnight weather forecast via Open-Meteo
- local sensor-vs-forecast dew-margin bias correction
- optional UKMO 2 km ensemble conservative P10 scenario
- configurable extra forecast safety margin
- high-water-mark “set-and-leave” AstroZap recommendation that only rises during a session
- local machine-readable status/history/forecast APIs for N.I.N.A. plugin integration
- explicit HID device profiles and conservative auto-detection
- `--probe` / `--probe-all` hardware diagnostics for unsupported revisions
- optional explicit `--profile` selection
- existing dashboard/calibration, Alpaca and Windows-service functionality

The dashboard remains deliberately bound to loopback only because it can modify calibration and forecast settings:

```text
http://localhost:11112/dashboard
```

Local integration endpoints are:

```text
http://localhost:11112/api/v1/status
http://localhost:11112/api/v1/history
http://localhost:11112/api/v1/forecast
```

The standard Alpaca API remains on port `11111` and continues to work independently if the dashboard or external forecast service is unavailable.

## HID compatibility diagnostics

Before reporting an unsupported TEMPerHUM revision, close the vendor TEMPerHUM application and run:

```powershell
.\TemperHumAlpaca.exe --probe
```

Probe mode is intentionally read-only: it inspects HID metadata and **does not send a TEMPerHUM measurement command** to an unknown device.

For every likely TEMPer-family interface it reports:

- VID/PID
- whether the device matches a supported profile
- manufacturer/product/serial metadata when Windows exposes it
- input, output and feature report lengths
- full Windows HID device path, including interface information such as `MI_01`

If the device is not recognised as a likely candidate, inspect all HID devices:

```powershell
.\TemperHumAlpaca.exe --probe-all
```

You can filter the output by hexadecimal VID/PID:

```powershell
.\TemperHumAlpaca.exe --probe-all --vid 1A86 --pid E025
```

The existing `--list` command now lists interfaces matching supported profiles rather than claiming every matching brand/revision is compatible.

### Explicit profile selection

The default configuration is:

```json
"deviceProfile": "auto"
```

`auto` considers only implemented profiles and will not fall back to an unknown protocol.

For troubleshooting, an implemented profile can be selected explicitly:

```powershell
.\TemperHumAlpaca.exe --once --profile pcsensor-413d-2107-temperx-v31
```

An unknown profile ID is rejected. `--vid` and `--pid` are diagnostic probe filters; they do **not** force an unsupported device to use the known measurement decoder.

## Dew-risk and AstroZap guidance

TemperHumAlpaca calculates:

```text
dew margin = calibrated ambient temperature - calculated dew point
```

The initial AstroZap recommendation curve is:

| Dew margin | Risk | Base heater estimate |
| --- | --- | ---: |
| > 8 °C | Very low | 5% |
| 5–8 °C | Low | 15% |
| 3–5 °C | Moderate | 25% |
| 2–3 °C | Elevated | 35% |
| 1–2 °C | High | 50% |
| 0–1 °C | Very high | 70% |
| ≤ 0 °C | Dew likely | 95% |

The AstroZap AZ-720 dual-channel controller is documented as varying each channel from roughly 5% duty cycle at Low to roughly 95% at High. TemperHumAlpaca maps the estimate to an approximate knob position such as `About 1/3` or `About 1/2`.

After at least ten minutes of readings, v0.5 estimates the rate at which dew margin is changing. A falling margin can increase the current recommendation modestly; a rapidly rising margin can reduce it slightly.

This guidance is **advisory only**. The TEMPerHUM measures ambient air rather than objective temperature, and a manual AstroZap controller has no objective-temperature feedback. Radiative cooling, wind, strap placement and telescope thermal mass can all change the power actually required.

## Overnight set-and-leave forecast

The optional forecast is designed for a manually controlled heater: choose one conservative setting near the start of an imaging session rather than repeatedly changing the knob overnight.

When enabled, TemperHumAlpaca retrieves hourly 2 m temperature and dew point from Open-Meteo using the UK Met Office UKV 2 km model. The default forecast horizon is 12 hours.

The calculation is deliberately local-bias aware:

```text
local bias = measured dew margin now - forecast dew margin now
adjusted future margin = forecast future margin + local bias
```

For example, if the forecast currently expects an 8.0 °C dew margin but the telescope sensor measures 6.2 °C, the forecast is treated as roughly 1.8 °C too optimistic and future margins are shifted down by that amount.

When UKMO 2 km ensemble data is available, the software calculates the 10th-percentile dew margin across ensemble members at each forecast hour. This intentionally avoids sizing the heater from one extreme ensemble outlier while still representing a conservative weather scenario. The worst adjusted hour within the configured horizon is then used.

Finally, the configured extra safety margin is subtracted. The default is `0.5 °C`:

```text
conservative margin = min(current local margin, worst adjusted forecast margin) - safety margin
```

That conservative margin is mapped through the same AstroZap power curve.

The dashboard exposes both the instantaneous recommendation and the **Overnight heater** recommendation. The overnight recommendation is a session high-water mark: if a later forecast becomes worse it can rise, but it does not automatically fall when conditions improve. This supports setting the manual controller once and leaving it alone. The high-water mark can be reset manually from the dashboard and is naturally reset when the service restarts.

If ensemble data is unavailable, TemperHumAlpaca falls back to the deterministic UKV forecast plus local bias and safety margin. If all forecast access fails, the sensor, dashboard, Alpaca device and current heater guidance continue operating; the last forecast error is shown separately.

Forecasting is disabled by default. Latitude/longitude are not committed to this repository. They are stored only in the local installed `temperhum.json` and are sent to Open-Meteo only when forecasting is enabled.

Forecast data is provided through Open-Meteo using UK Met Office model data. UKMO UKV provides hourly 2 km forecast coverage for the UK and Ireland.

## Local APIs

`GET /api/v1/status` returns current calibrated environmental values, dew guidance, history count and the current forecast outlook.

`GET /api/v1/history` returns the in-memory two-hour dew-margin history.

`GET /api/v1/forecast` returns the overnight forecast state, local bias, conservative minimum dew margin, expected worst time, forecast recommendation and session high-water-mark recommendation.

These endpoints deliberately live on the loopback-only dashboard listener rather than adding non-standard properties to ASCOM Alpaca `ObservingConditions`.

## Releases

Stable versions are published from `master` as tagged GitHub Releases.

For v0.6.0 and later, the release provides:

- `TemperHumAlpaca-vX.Y.Z-win-x64.zip` — the complete self-contained Windows service package, including `NINA-Plugin\TemperHumAlpaca.NinaPlugin.dll` and its plugin README.
- `TemperHumAlpaca-vX.Y.Z-NINA-plugin.zip` — the small N.I.N.A. plugin-only package.
- a SHA-256 checksum file for each ZIP.

Use the tagged Release assets for normal installation rather than development Actions artifacts.

`develop` is the active development branch. Validated changes are promoted to `master`, where the release workflow reads the version from `TemperHumAlpaca.csproj`, validates both the service and N.I.N.A. plugin, creates the matching tag and publishes both packages.

## Windows service / unattended operation

The bridge supports:

- native Windows Service install/uninstall
- automatic startup with Windows
- Service Control Manager restart recovery
- automatic USB reconnect after boot-time delays or later disconnect/reconnect events
- preservation of installed calibration, device profile, forecast settings and Alpaca UniqueID across upgrades

Install from an extracted release/development build using Administrator PowerShell:

```powershell
.\TemperHumAlpaca.exe --install-service
```

The installed files/configuration live under:

```text
C:\ProgramData\TemperHumAlpaca
```

Check service state:

```powershell
.\TemperHumAlpaca.exe --service-status
```

Uninstall the service:

```powershell
.\TemperHumAlpaca.exe --uninstall-service
```

Uninstalling deliberately leaves the configuration directory in place.

## ObservingConditions support

Implemented ASCOM values/functions include:

- Temperature (°C)
- Humidity (% RH)
- DewPoint (°C, calculated after calibration)
- AveragePeriod (`0.0`, instantaneous readings)
- SensorDescription
- TimeSinceLastUpdate
- Refresh
- DeviceState
- Connected / Connecting / Connect / Disconnect

Unsupported weather values such as pressure, cloud cover, rain and wind correctly return ASCOM `NotImplemented` rather than fabricated values.

The server also provides Alpaca management endpoints, IPv4 discovery on UDP `32227`, and a persistent Alpaca UniqueID.

## Calibration workflow

Open:

```text
http://localhost:11112/dashboard
```

Place a trusted reference thermometer/hygrometer beside the USB sensor, allow both to stabilise, and enter the reference values under **Calibrate against reference thermometer**.

TemperHumAlpaca derives the current raw reading by backing out existing offsets, then calculates:

```text
temperature offset = reference temperature - raw USB temperature
humidity offset    = reference humidity - raw USB humidity
```

Repeated calibration therefore does not compound previous corrections. Prefer several stabilised side-by-side checks before treating a large offset as permanent.

## Interactive operation

Run the Alpaca bridge directly:

```powershell
.\TemperHumAlpaca.exe
```

Direct sensor test:

```powershell
.\TemperHumAlpaca.exe --once
```

Continuous direct monitor:

```powershell
.\TemperHumAlpaca.exe --monitor
```

Supported-profile HID interfaces:

```powershell
.\TemperHumAlpaca.exe --list
```

N.I.N.A. can discover the device as **TEMPerHUM Observing Conditions** through ASCOM Alpaca.

## Configuration

Sample configuration:

```json
{
  "temperatureOffsetC": 0.0,
  "humidityOffsetPercent": 0.0,
  "pollIntervalSeconds": 1,
  "reconnectIntervalSeconds": 5,
  "alpacaPort": 11111,
  "dashboardPort": 11112,
  "discoveryEnabled": true,
  "discoveryPort": 32227,
  "autoConnect": true,
  "deviceProfile": "auto",
  "forecastEnabled": false,
  "forecastLatitude": null,
  "forecastLongitude": null,
  "forecastHours": 12,
  "forecastRefreshMinutes": 30,
  "forecastSafetyMarginC": 0.5,
  "forecastUseEnsemble": true,
  "uniqueId": ""
}
```

Older installed configuration files that do not contain the newer device/forecast fields use the in-code defaults when loaded. Forecasting therefore remains disabled until explicitly configured.

`reconnectIntervalSeconds` controls how often the bridge retries a desired USB connection. A deliberate disconnect from an Alpaca client remains disconnected.

On first run, an empty `uniqueId` is replaced by a generated GUID. Keep that value stable so Alpaca clients can re-identify the installation.

Temperature and humidity offsets are applied before dew point is calculated.

## Building

Development requires the .NET 8 SDK. GitHub Actions publishes a self-contained `win-x64` service executable plus the N.I.N.A. plugin, so the target astro PC does **not** need Visual Studio, Visual C++ build tools, Python, Node, the .NET SDK, or a separately installed .NET runtime.

```powershell
dotnet restore src/TemperHumAlpaca/TemperHumAlpaca.csproj
dotnet build src/TemperHumAlpaca/TemperHumAlpaca.csproj -c Release

dotnet restore src/TemperHumAlpaca.NinaPlugin/TemperHumAlpaca.NinaPlugin.csproj
dotnet build src/TemperHumAlpaca.NinaPlugin/TemperHumAlpaca.NinaPlugin.csproj -c Release
```

CI launches the packaged executable and smoke-tests the Alpaca API, dashboard, local status/history/forecast endpoints, device-profile configuration and read-only HID probe command. It also builds and version-validates the N.I.N.A. plugin and verifies that the combined artifact actually contains the plugin DLL. Forecast network access is deliberately disabled in CI so external weather-service availability cannot make package validation flaky.

## Protocol notes

The known `413D:2107` reader sends the TEMPerX_V3.1-style command:

```text
01 80 33 01 00 00 00 00
```

On Windows/HidSharp it is written with the leading HID report-ID byte. Temperature and humidity are decoded from the known response layout only after the device matches the supported profile/interface requirements.

The implementation was informed by publicly documented behaviour in the MIT-licensed [`urwen/temper`](https://github.com/urwen/temper) and [`mreymann/temperx`](https://github.com/mreymann/temperx) projects. No external native executable is bundled.

## Roadmap

- **v0.1** — Windows HID readout and calibration
- **v0.2** — ASCOM Alpaca `ObservingConditions` HTTP API and discovery
- **v0.3** — Windows service/autostart and unattended USB reconnect recovery
- **v0.4** — local environment dashboard, reference-sensor calibration and tagged release packaging
- **v0.5** — dew-risk/AstroZap guidance, dew history, overnight set-and-leave forecast, local integration APIs and conservative HID compatibility framework
- **v0.6** — N.I.N.A. dockable monitor, transition alerts, Telegram remote delivery and unified plugin/service release packaging

Further TEMPerHUM profiles will only be added when hardware/protocol data is validated.

## License

MIT. See [LICENSE](LICENSE).
