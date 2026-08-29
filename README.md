# TemperHumAlpaca

A small, self-contained Windows utility that reads selected PCsensor TEMPerHUM/TEMPerX USB HID sensors and exposes temperature, relative humidity and calculated dew point as an ASCOM Alpaca `ObservingConditions` device.

Initial target hardware:

- USB VID: `0x413D`
- USB PID: `0x2107`
- Tested Windows HID interface: `MI_01`
- Known compatible TEMPerX/TEMPerHUM protocol family

## v0.2

v0.2 adds an ASCOM Alpaca `ObservingConditions` server around the proven v0.1 HID reader.

Implemented ObservingConditions values:

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

## Running on the astro PC

1. Download the latest `TemperHumAlpaca-win-x64` artifact from GitHub Actions.
2. Extract it to a permanent folder on the Windows mini-PC.
3. Close the vendor TEMPerHUM application so it does not hold the HID interface open.
4. Run `TemperHumAlpaca.exe` with no arguments.
5. Leave the process running while N.I.N.A. is using the weather device.

By default the server listens on:

- Alpaca HTTP: `http://localhost:11111`
- setup/status page: `http://localhost:11111/setup`
- discovery: UDP `32227`
- device: `ObservingConditions` number `0`

N.I.N.A. supports direct ASCOM Alpaca discovery. In N.I.N.A.'s Weather / Observing Conditions device selection, refresh/discover Alpaca devices and select **TEMPerHUM Observing Conditions**.

Windows may display a firewall prompt the first time the server listens for Alpaca traffic. Allow it on your private network if you want discovery/network access.

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

Edit `temperhum.json` beside the executable:

```json
{
  "temperatureOffsetC": 0.0,
  "humidityOffsetPercent": 0.0,
  "pollIntervalSeconds": 1,
  "alpacaPort": 11111,
  "discoveryEnabled": true,
  "discoveryPort": 32227,
  "autoConnect": true,
  "uniqueId": ""
}
```

On first run, an empty `uniqueId` is replaced with a generated GUID and written back to this file. Keep that value stable for the installation so Alpaca clients can re-identify the device.

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
- **v0.3** — Windows service/autostart, improved configuration UX and conformance testing

## License

MIT. See [LICENSE](LICENSE).
