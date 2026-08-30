# TemperHumAlpaca N.I.N.A. plugin (v0.6 development)

The v0.6 N.I.N.A. integration is a separate plugin DLL. It does not read the USB sensor itself and does not replace the ASCOM Alpaca `ObservingConditions` device.

The installed TemperHumAlpaca Windows service remains the source of truth for:

- TEMPerHUM USB readings
- calibration
- dew point and dew margin
- dew-risk classification
- current AstroZap recommendation
- overnight UKMO/Open-Meteo forecast
- set-and-leave high-water heater recommendation

The plugin reads the existing loopback status API at `http://127.0.0.1:11112/api/v1/status`.

## Compatibility

The development plugin targets:

- N.I.N.A. 3.2 stable API (`NINA.Plugin 3.2.0.9001`)
- .NET 8 Windows plugin runtime
- TemperHumAlpaca service v0.5.0 or later

The astro PC does not need the .NET SDK or Visual Studio. N.I.N.A. supplies the runtime/API used by the plugin.

## Panel

The dockable **TEMPerHUM Dew Monitor** panel shows:

- TemperHumAlpaca service/sensor status
- temperature
- relative humidity
- dew point
- dew margin and trend
- dew-risk level
- current AstroZap power estimate and knob guidance
- overnight set-and-leave power and knob guidance
- conservative forecast minimum dew margin
- expected worst forecast time
- forecast confidence and local sensor correction
- last update/error state

The panel normally polls the local service every 5 seconds. It also has **Refresh** and **Open dashboard** buttons.

## Alerts

Alerts use N.I.N.A.'s normal notification system and are transition-based to avoid overnight spam.

Defaults:

- alerts enabled
- warn when dew risk escalates to **High** or worse
- warn if the overnight set-and-leave heater recommendation increases
- warn if the TEMPerHUM sensor disconnects
- warn if the TemperHumAlpaca service becomes unavailable after previously being reachable
- 15-minute repeat-alert cooldown

The plugin establishes an initial baseline silently, so opening N.I.N.A. while conditions are already poor does not immediately generate a burst of startup warnings.

### Telegram remote delivery

v0.6 can optionally deliver the same transition-based alerts directly through a Telegram bot. This is independent of Ground Station, so no sequencer failure is manufactured merely to trigger a remote notification.

You may use the same Telegram bot token and chat ID already configured for Ground Station. The plugin sends plain text through Telegram's HTTPS Bot API `sendMessage` method.

Telegram delivery is disabled by default. When enabled, each alert that passes the existing alert rule/cooldown also produces a Telegram message. The current alert types are:

- dew-risk escalation to the configured threshold or worse
- overnight set-and-leave AstroZap recommendation increase
- TEMPerHUM sensor disconnect
- TemperHumAlpaca service loss after previously being reachable

The Telegram bot token is treated as a secret. It is entered through a password-style field and stored encrypted with Windows DPAPI using `CurrentUser`, so the plaintext token is not written into the plugin settings. The token is not echoed back into the settings UI after entry. The chat ID is stored as a normal plugin setting.

The settings page provides **Test Telegram** to send a harmless test message using the same delivery code as live alerts, plus **Clear stored bot token**.

Do not commit or paste a real bot token into repository files, issue reports, logs, screenshots, or chat transcripts.

## Install a development build

1. Keep the released TemperHumAlpaca service installed and running normally.
2. In GitHub Actions, open the latest successful **nina-plugin** run for the `develop` branch.
3. Download the `TemperHumAlpaca-NINA-plugin` artifact and extract it.
4. Close N.I.N.A.
5. In PowerShell, create the plugin directory if required:

```powershell
$pluginDir = Join-Path $env:LOCALAPPDATA 'NINA\Plugins\3.0.0\TemperHumAlpaca'
New-Item -ItemType Directory -Path $pluginDir -Force
```

6. Copy `TemperHumAlpaca.NinaPlugin.dll` into that directory. The `.pdb` is optional for normal testing.
7. Start N.I.N.A. again.

The resulting path should be:

```text
%LOCALAPPDATA%\NINA\Plugins\3.0.0\TemperHumAlpaca\TemperHumAlpaca.NinaPlugin.dll
```

N.I.N.A.'s Imaging workspace uses dockable windows that can be enabled from its top-bar Info/Tools controls and arranged like the built-in panels. Look for **TEMPerHUM Dew Monitor** among the available tool panels.

## Plugin settings

The plugin settings page exposes:

- local service URL (default `http://127.0.0.1:11112`)
- panel refresh interval (2–60 seconds, default 5)
- alert enable/disable
- dew-risk alert threshold
- heater-increase alert enable/disable
- sensor-disconnect alert enable/disable
- service-loss alert enable/disable
- repeat-alert cooldown
- Telegram delivery enable/disable
- Telegram chat ID
- encrypted Telegram bot-token entry/status
- **Test Telegram**
- **Clear stored bot token**

For the normal single-PC Astrobox setup, leave the service URL at its loopback default.

## First hardware test

Before changing any alert settings, verify that:

1. the plugin loads without a N.I.N.A. startup error;
2. **TEMPerHUM Dew Monitor** appears as an Imaging tool panel;
3. the panel reports the same temperature/humidity/dew margin as `http://localhost:11112/dashboard`;
4. the current AstroZap estimate matches the dashboard;
5. the overnight set-and-leave recommendation matches the dashboard;
6. **Open dashboard** opens the local dashboard;
7. stopping the TemperHumAlpaca service changes the panel to `service unavailable`, then recovery occurs after restarting the service.

For Telegram, copy your bot token and chat ID from your existing Ground Station configuration into TemperHumAlpaca's N.I.N.A. plugin settings, enable Telegram delivery, and click **Test Telegram**. A successful test confirms the bot credentials, target chat and outbound HTTPS access from the imaging PC without needing to manufacture a dew-risk event.

Do not deliberately manipulate real dew/heater conditions merely to test alert thresholds. Alert transition testing can be added to automated/plugin integration tests separately.

## Development build

Developers can build the plugin independently of the Windows service:

```powershell
dotnet restore src/TemperHumAlpaca.NinaPlugin/TemperHumAlpaca.NinaPlugin.csproj
dotnet build src/TemperHumAlpaca.NinaPlugin/TemperHumAlpaca.NinaPlugin.csproj -c Release
```

The dedicated `nina-plugin` GitHub Actions workflow builds against the stable N.I.N.A. 3.2 plugin package, validates assembly version `0.6.0.0`, and uploads a manual-test artifact. CI does not send Telegram messages and contains no real Telegram credentials.

## Release plan

The v0.6 development plugin remains on `develop` until it is physically validated inside N.I.N.A. on the imaging PC. Before the stable v0.6.0 release, the core TemperHumAlpaca application version and release packaging will be promoted to 0.6.0 and the release workflow will include the N.I.N.A. plugin artifact alongside the Windows service package.
