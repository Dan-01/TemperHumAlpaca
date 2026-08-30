# TemperHumAlpaca N.I.N.A. plugin (v0.6)

The v0.6 N.I.N.A. integration is a separate plugin DLL. It does not read the USB sensor itself and does not replace the ASCOM Alpaca `ObservingConditions` device.

The installed TemperHumAlpaca Windows service remains the source of truth for TEMPerHUM USB readings, calibration, dew point and dew margin, dew-risk classification, current AstroZap recommendation, overnight UKMO/Open-Meteo forecast, and the set-and-leave high-water heater recommendation.

The plugin reads the existing loopback status API at `http://127.0.0.1:11112/api/v1/status`.

## Compatibility

The v0.6 plugin targets N.I.N.A. 3.2 stable API (`NINA.Plugin 3.2.0.9001`) and the .NET 8 Windows plugin runtime. The stable v0.6 package ships TemperHumAlpaca service v0.6.0.

## Panel and alerts

The dockable **TEMPerHUM Dew Monitor** panel shows service/sensor status, temperature, humidity, dew point, dew margin and trend, dew risk, current AstroZap guidance, overnight set-and-leave guidance, conservative forecast minimum, expected worst forecast time, forecast confidence/local correction and last update/error state.

Alerts are transition-based to avoid overnight spam. Defaults warn when dew risk escalates to **High** or worse, the overnight heater recommendation rises, the sensor disconnects, or the TemperHumAlpaca service becomes unavailable. The default repeat-alert cooldown is 15 minutes.

If the panel is closed, reopen it from N.I.N.A.'s **Imaging** workspace using the top-bar **Tools** selector and choose **TEMPerHUM Dew Monitor**.

## Telegram remote delivery

v0.6 can optionally deliver the same alerts directly through a Telegram bot. You may use the same bot token and chat ID already configured for Ground Station. Telegram delivery is disabled by default.

The bot token is entered through a password-style field and stored encrypted with Windows DPAPI using `CurrentUser`; plaintext is not written into plugin settings or echoed back into the UI. The settings page provides **Test Telegram** and **Clear stored bot token**.

## Install from v0.6.0 release

The release provides two choices:

- `TemperHumAlpaca-v0.6.0-win-x64.zip` — complete Windows service package with the plugin under `NINA-Plugin\TemperHumAlpaca.NinaPlugin.dll`.
- `TemperHumAlpaca-v0.6.0-NINA-plugin.zip` — small plugin-only package.

To install the plugin, close N.I.N.A., create the plugin folder if necessary, and copy `TemperHumAlpaca.NinaPlugin.dll` into it:

```powershell
$pluginDir = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\TemperHumAlpaca'
New-Item -ItemType Directory -Path $pluginDir -Force
```

Final path:

```text
%LOCALAPPDATA%\NINA\Plugins\3.0.0\TemperHumAlpaca\TemperHumAlpaca.NinaPlugin.dll
```

Restart N.I.N.A. and enable **TEMPerHUM Dew Monitor** from the Imaging Tools selector.

## Plugin settings

The settings page exposes the local service URL, panel refresh interval, alert enable/disable, dew-risk threshold, heater/sensor/service alert toggles, cooldown, Telegram enable/disable, Telegram chat ID, encrypted Telegram bot-token entry/status, **Test Telegram**, and **Clear stored bot token**.

For a normal single-PC setup, leave the service URL at `http://127.0.0.1:11112`.

## Validation

v0.6 was physically validated inside N.I.N.A. on the imaging PC before release. The plugin loaded successfully, the dockable panel was available and live TemperHumAlpaca data displayed correctly.

The dedicated `nina-plugin` CI workflow builds against the stable N.I.N.A. 3.2 plugin package and validates assembly version `0.6.0.0`. The normal Windows build also embeds the validated plugin DLL under `NINA-Plugin` in the combined artifact. CI contains no real Telegram credentials and does not send Telegram messages.
