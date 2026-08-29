# TemperHumAlpaca

A small Windows utility for reading selected PCsensor TEMPerHUM/TEMPerX USB HID sensors and, in a later milestone, exposing their measurements as an ASCOM Alpaca `ObservingConditions` device.

The initial target hardware is:

- USB VID: `0x413D`
- USB PID: `0x2107`
- Known firmware: `TEMPerX_V3.1`
- Preferred HID interface on Windows: `MI_01`

## Current milestone: v0.1 USB reader

v0.1 deliberately does **not** implement ASCOM Alpaca yet. Its job is to prove that a self-contained Windows executable can reliably read the sensor on the acquisition mini-PC without Visual Studio, Visual C++ build tools, Python, Node, or a .NET SDK installed there.

It reads:

- ambient temperature
- relative humidity
- calculated dew point
- optional temperature and humidity calibration offsets

## Running on the astro PC

1. Download the `TemperHumAlpaca-win-x64` artifact produced by GitHub Actions.
2. Extract it to a folder on the Windows mini-PC.
3. Close the vendor TEMPerHUM application so it does not hold the HID device open.
4. Run `TemperHumAlpaca.exe`.
5. Press `Ctrl+C` to stop.

For a single reading:

```powershell
.\TemperHumAlpaca.exe --once
```

To list matching HID interfaces:

```powershell
.\TemperHumAlpaca.exe --list
```

## Calibration

Copy/edit `temperhum.json` beside the executable:

```json
{
  "temperatureOffsetC": 0.0,
  "humidityOffsetPercent": 0.0,
  "pollIntervalSeconds": 1
}
```

Corrections are applied before dew point is calculated.

## Building

Development builds require the .NET 8 SDK, but the release artifact is published as a self-contained `win-x64` executable so the target mini-PC does not need the SDK or runtime installed.

```powershell
dotnet restore src/TemperHumAlpaca/TemperHumAlpaca.csproj
dotnet build src/TemperHumAlpaca/TemperHumAlpaca.csproj -c Release
```

## Protocol notes

The `413D:2107` identifier is shared by more than one PCsensor product, so VID/PID alone is not sufficient to identify a sensor. This project initially targets the TEMPerHUM/TEMPerX layout observed on `TEMPerX_V3.1`, where interface 1 accepts an 8-byte HID query and returns temperature and humidity as signed big-endian hundredths.

The implementation was informed by the publicly documented behaviour in the MIT-licensed [`urwen/temper`](https://github.com/urwen/temper) project and the [`mreymann/temperx`](https://github.com/mreymann/temperx) project. No external native executable is bundled.

## Roadmap

- **v0.1** — robust Windows HID readout and calibration
- **v0.2** — ASCOM Alpaca `ObservingConditions` HTTP API and discovery
- **v0.3** — Windows service/autostart, health/status and configuration UX

## License

MIT. See [LICENSE](LICENSE).
