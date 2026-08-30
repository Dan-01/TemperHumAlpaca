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

## v0.5.0

v0.5.0 adds:

- dew-risk classification
- estimated AstroZap dual-channel heater power
- approximate Low-to-High knob position
- two-hour in-memory dew-margin history
- trend detection after sufficient history is collected
- modest trend-based heater adjustment
- hysteresis to reduce recommendation flicker
- local machine-readable status API for future N.I.N.A. plugin integration
- explicit HID device profiles and conservative auto-detection
- `--probe` / `--probe-all` hardware diagnostics for unsupported revisions
- optional explicit `--profile` selection
- existing dashboard/calibration, Alpaca and Windows-service functionality

The dashboard remains deliberately bound to loopback only because it can modify calibration settings:

```text
http://localhost:11112/dashboard
```

The local integration endpoint is:

```text
http://localhost:11112/api/v1/status
```

The standard Alpaca API remains on port `11111` and continues to work independently if the dashboard is unavailable.

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

After at least ten minutes of readings, v0.5 estimates the rate at which dew margin is changing. A falling margin can increase the recommendation modestly; a rapidly rising margin can reduce it slightly.

This guidance is **advisory only**. The TEMPerHUM measures ambient air rather than objective temperature, and a manual AstroZap controller has no objective-temperature feedback. Radiative cooling, wind, strap placement and telescope thermal mass can all change the power actually required.

## Local status API

`GET /api/v1/status` returns JSON intended for lightweight local integrations. When connected it includes values such as:

```json
{
  "version": "0.5.0",
  "connected": true,
  "temperatureC": 10.0,
  "humidityPercent": 85.0,
  "dewPointC": 7.5,
  "dewMarginC": 2.5,
  "dewRisk": "ELEVATED",
  "recommendedHeaterPowerPercent": 35,
  "astroZapKnobPosition": "About 1/3",
  "dewMarginTrend": "Stable",
  "dewMarginTrendCPerHour": 0.0
}
```

The endpoint deliberately lives on the loopback-only dashboard listener rather than adding non-standard properties to ASCOM Alpaca `ObservingConditions`.

## Releases

Stable versions are published from `master` as tagged GitHub Releases. Download `TemperHumAlpaca-vX.Y.Z-win-x64.zip` from the repository Releases page rather than using a development Actions artifact for normal installation.

`develop` is the active development branch. Validated changes are promoted to `master`, where the release workflow reads the version from `TemperHumAlpaca.csproj`, creates the matching tag and packages the self-contained Windows build.

## Windows service / unattended operation

The bridge supports:

- native Windows Service install/uninstall
- automatic startup with Windows
- Service Control Manager restart recovery
- automatic USB reconnect after boot-time delays or later disconnect/reconnect events
- preservation of installed calibration, device profile and Alpaca UniqueID across upgrades

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
  "uniqueId": ""
}
```

Older installed configuration files that do not contain `deviceProfile` automatically default to `auto` when loaded.

`reconnectIntervalSeconds` controls how often the bridge retries a desired USB connection. A deliberate disconnect from an Alpaca client remains disconnected.

On first run, an empty `uniqueId` is replaced by a generated GUID. Keep that value stable so Alpaca clients can re-identify the installation.

Temperature and humidity offsets are applied before dew point is calculated.

## Building

Development requires the .NET 8 SDK. GitHub Actions publishes a self-contained `win-x64` executable, so the target astro PC does **not** need Visual Studio, Visual C++ build tools, Python, Node, the .NET SDK, or a separately installed .NET runtime.

```powershell
dotnet restore src/TemperHumAlpaca/TemperHumAlpaca.csproj
dotnet build src/TemperHumAlpaca/TemperHumAlpaca.csproj -c Release
```

CI launches the packaged executable and smoke-tests the Alpaca API, dashboard, local status endpoint, device-profile configuration and read-only HID probe command.

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
- **v0.5** — dew-risk/AstroZap guidance, trend analysis, local integration API and conservative HID compatibility framework
- **v0.6** — N.I.N.A. plugin panel and alert integration; add further TEMPerHUM profiles only when hardware/protocol data is validated

## License

MIT. See [LICENSE](LICENSE).
