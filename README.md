# TemperHumAlpaca

A small, self-contained Windows utility that reads **selected, explicitly supported** PCsensor TEMPerHUM/TEMPerX USB HID sensors and exposes temperature, relative humidity and calculated dew point as an ASCOM Alpaca `ObservingConditions` device.

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

See `docs/NINA_PLUGIN.md` for installation and settings details.

## Releases

Stable versions are published from `master` as tagged GitHub Releases. From v0.6.0 onward the release provides:

- `TemperHumAlpaca-vX.Y.Z-win-x64.zip` — the complete self-contained Windows service package, including `NINA-Plugin\TemperHumAlpaca.NinaPlugin.dll`.
- `TemperHumAlpaca-vX.Y.Z-NINA-plugin.zip` — the small N.I.N.A. plugin-only package.
- a SHA-256 checksum file for each ZIP.

Use tagged Release assets for normal installation rather than development Actions artifacts.

## Existing service functionality

TemperHumAlpaca includes the v0.5 dew-risk/AstroZap guidance, two-hour dew-margin history, optional UKMO/Open-Meteo overnight forecast with local bias and ensemble P10 handling, DD/DMS location entry, conservative HID profiles/probe diagnostics, calibration dashboard, Alpaca ObservingConditions API/discovery and Windows service/autostart/reconnect support.

Dashboard:

```text
http://localhost:11112/dashboard
```

Local APIs:

```text
http://localhost:11112/api/v1/status
http://localhost:11112/api/v1/history
http://localhost:11112/api/v1/forecast
```

The standard Alpaca API is on port `11111`.

## Windows service

Install from an extracted release using Administrator PowerShell:

```powershell
.\TemperHumAlpaca.exe --install-service
```

Installed files/configuration live under:

```text
C:\ProgramData\TemperHumAlpaca
```

Check service state:

```powershell
.\TemperHumAlpaca.exe --service-status
```

Uninstall:

```powershell
.\TemperHumAlpaca.exe --uninstall-service
```

Uninstalling deliberately leaves the configuration directory in place.

## Supported hardware

Validated profile:

| Profile | VID:PID | Tested interface | Expected reports | Protocol |
| --- | --- | --- | --- | --- |
| `pcsensor-413d-2107-temperx-v31` | `413D:2107` | `MI_01` preferred | input ≥9, output ≥9 | TEMPerX_V3.1-style |

Probe diagnostics are read-only for unknown devices:

```powershell
.\TemperHumAlpaca.exe --probe
.\TemperHumAlpaca.exe --probe-all
```

## Building

Development requires the .NET 8 SDK.

```powershell
dotnet restore src/TemperHumAlpaca/TemperHumAlpaca.csproj
dotnet build src/TemperHumAlpaca/TemperHumAlpaca.csproj -c Release

dotnet restore src/TemperHumAlpaca.NinaPlugin/TemperHumAlpaca.NinaPlugin.csproj
dotnet build src/TemperHumAlpaca.NinaPlugin/TemperHumAlpaca.NinaPlugin.csproj -c Release
```

CI builds and smoke-tests the service, builds/version-validates the N.I.N.A. plugin and verifies that the combined artifact contains the plugin DLL.

## License

MIT. See [LICENSE](LICENSE).
