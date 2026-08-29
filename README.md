# TemperHumAlpaca

A small, self-contained Windows utility that reads selected PCsensor TEMPerHUM/TEMPerX USB HID sensors and exposes temperature, relative humidity and calculated dew point as an ASCOM Alpaca `ObservingConditions` device.

Initial target hardware:

- USB VID: `0x413D`
- USB PID: `0x2107`
- Tested Windows HID interface: `MI_01`
- Known compatible TEMPerX/TEMPerHUM protocol family

## v0.3

v0.3 adds unattended Windows operation on top of the working v0.2 Alpaca bridge:

- install/uninstall as a native Windows Service
- automatic startup with Windows
- service restart recovery configured through the Windows Service Control Manager
- automatic TEMPerHUM USB reconnect attempts after boot-time delays or later USB disconnect/reconnect events
- installed calibration and Alpaca UniqueID preserved across upgrades

The service runs the same Alpaca server used in interactive console mode.

## ObservingConditions support

Implemented values:

- Temperature (degrees C)
- Humidity (% RH)
- DewPoint (degrees C, calculated after calibration)
- AveragePeriod (`0.0`, instantaneous readings)
- SensorDescription
- TimeSinceLastUpdate
- Refresh
- DeviceState
- Connected / Connecting / Connect / Disconnect

Unsupported weather values such as pressure, cloud cover, rain and wind correctly return the ASCOM `NotImplemented` error rather than fabricated data.

The server also provides:

- Alpaca management endpoints
- IPv4 Alpaca discovery on UDP `32227`
- HTTP setup/status page
- persistent Alpaca UniqueID generated on first run

## Recommended installation on the astro PC

1. Download the latest `TemperHumAlpaca-win-x64` artifact from GitHub Actions.
2. Extract it anywhere convenient.
3. Close the vendor TEMPerHUM application.
4. Open **PowerShell as Administrator** in the extracted folder.
5. Run:

```powershell
.\TemperHumAlpaca.exe --install-service
```

The installer copies the executable to:

```text
C:\ProgramData\TemperHumAlpaca
```

and registers **TemperHumAlpaca ASCOM Alpaca Bridge** as an automatically-starting Windows Service. It starts the service immediately.

The installed configuration lives at:

```text
C:\ProgramData\TemperHumAlpaca\temperhum.json
```

That file is deliberately preserved when you install a newer build so calibration offsets and the Alpaca UniqueID remain stable.

Check service state with:

```powershell
.\TemperHumAlpaca.exe --service-status
```

Remove the service with an Administrator PowerShell:

```powershell
.\TemperHumAlpaca.exe --uninstall-service
```

Uninstalling the service leaves `C:\ProgramData\TemperHumAlpaca` in place so configuration is not accidentally lost.

## Interactive operation

You can still run the Alpaca bridge directly for troubleshooting:

```powershell
.\TemperHumAlpaca.exe
```

By default the server listens on:

- Alpaca HTTP: `http://localhost:11111`
- setup/status page: `http://localhost:11111/setup`
- discovery: UDP `32227`
- device: `ObservingConditions` number `0`

N.I.N.A. supports direct ASCOM Alpaca discovery. In N.I.N.A.'s Weather / Observing Conditions device selection, refresh/discover Alpaca devices and select **TEMPerHUM Observing Conditions**.

Windows may display a firewall prompt the first time the interactive server listens for Alpaca traffic. Allow it on your private network if you want discovery/network access.

## USB diagnostics

For a single direct sensor reading without starting Alpaca:

```powershell
.\TemperHumAlpaca.exe --once
```

To continuously monitor direct sensor readings:

```powershell
.\TemperHumAlpaca.exe --monitor
```

To list matching HID interfaces:

```powershell
.\TemperHumAlpaca.exe --list
```

## Configuration and calibration

The configuration file contains:

```json
{
  "temperatureOffsetC": 0.0,
  "humidityOffsetPercent": 0.0,
  "pollIntervalSeconds": 1,
  "reconnectIntervalSeconds": 5,
  "alpacaPort": 11111,
  "discoveryEnabled": true,
  "discoveryPort": 32227,
  "autoConnect": true,
  "uniqueId": ""
}
```

`reconnectIntervalSeconds` controls how often the bridge retries a desired USB connection after the sensor is unavailable. A deliberate disconnect from an Alpaca client stays disconnected; reconnect recovery is for a connection that is intended to be active.

On first run, an empty `uniqueId` is replaced with a generated GUID and written back to the file. Keep that value stable for the installation so Alpaca clients can re-identify the device.

Temperature and humidity offsets are applied before dew point is calculated.

## Building

Development builds require the .NET 8 SDK, but GitHub Actions publishes a self-contained `win-x64` executable. The target mini-PC does **not** need Visual Studio, Visual C++ build tools, Python, Node, the .NET SDK, or a separately installed .NET runtime.

```powershell
dotnet restore src/TemperHumAlpaca/TemperHumAlpaca.csproj
dotnet build src/TemperHumAlpaca/TemperHumAlpaca.csproj -c Release
```

## Protocol notes

The `413D:2107` identifier is shared by more than one PCsensor product, so VID/PID alone is not sufficient to identify a sensor. The tested Windows unit exposes two HID interfaces; `MI_01` has 9-byte input/output reports and carries the TEMPerHUM measurements.

The implementation was informed by the publicly documented behaviour in the MIT-licensed [`urwen/temper`](https://github.com/urwen/temper) and [`mreymann/temperx`](https://github.com/mreymann/temperx) projects. No external native executable is bundled.

## Roadmap

- **v0.1** — Windows HID readout and calibration
- **v0.2** — ASCOM Alpaca `ObservingConditions` HTTP API and discovery
- **v0.3** — Windows service/autostart and unattended USB reconnect recovery
- **v0.4** — improved configuration/status UX and broader conformance testing

## License

MIT. See [LICENSE](LICENSE).
