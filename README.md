# PerfPanel — 440×1920 / 1920×440 Secondary-Screen Performance Monitor

**English** | [简体中文](README.zh-CN.md)

A dark, tech-style hardware monitoring panel built natively in WPF, designed for tall or wide USB-C portable monitors used as extended displays on Windows. The portrait 440×1920 single-column layout and the landscape 1920×440 two-column layout share the same card components, auto-switch by the display's shape, and can also be set manually.

## Screenshot

![Screenshot](image/panel-landscape.png)

## Features

- **Clock**: live time, date & weekday, uptime
- **CPU**: large usage readout + 120s history graph, frequency (LHM core clocks, falling back to WMI effective frequency that can exceed the nominal rate under boost), temperature, power draw, fan speed (desktop boards whose SuperIO chip exposes a fan sensor; some mini-PC / laptop EC fans are not readable)
- **GPU**: usage + history graph, VRAM usage (progress bar), temperature, power draw, fan (rows hide automatically when the iGPU exposes no sensor; AMD iGPU temperature comes from its VR SoC sensor)
- **Notes / todos**: a card showing a one-line sticky note and a todo list — click a row on the panel to toggle it done; rotating focus reminders (e.g. "stay on your review plan", "check the revision kanban") highlight on a 15–90 min cycle. Content persists in `notes.json` next to the exe, editable in Settings (S); landscape mode enlarges it into a big FOCUS card (todos clickable); built-in **pomodoro timer** (progress ring, work/break rotation, start/pause/reset/skip, system chime + 8-second banner on completion, durations configurable in Settings)
- **RAM**: usage percentage + used/total
- **Network**: real-time up/down speeds + download history graph, auto-selects the active adapter
- **Weather**: Open-Meteo free international source (no key; optional Amap key switches to a more stable China source), IP-based location (editable via `weather.json`)
- **Coding Plan quota**: DeepSeek balance, Zhipu GLM, Volcano Ark Agent/Coding Plan, Xiaomi MiMo (pay-as-you-go balance + Token Plan usage) remaining quota with reset countdowns
- **Graceful degradation**: without admin rights, temperature/power/fan cards hide automatically and the corner shows `BASIC`; running as admin shows `FULL`
- **Portrait/landscape layouts**: in landscape 1920×440 the clock sits centered; the top row holds compact CPU/GPU/memory tiles whose usage is a ring gauge (plus weather), the bottom row holds a large notes/todos card, network and the quota card (each provider renders one aligned nested ring gauge — 5h/week/month from outer to inner — with the tightest remaining quota in the center; balance-type providers like DeepSeek show a full ring with the amount); `auto` picks by the display's shape, or pin the direction via Settings, `config.json`, or the command line

## Run

### From source (dev/debug)

```bash
dotnet run --project src/PerfPanel                # fullscreen (restores deps & builds on first run)
dotnet run --project src/PerfPanel -- --windowed  # debug: windowed on primary screen, for machines without a secondary display
```

Requires the .NET SDK 8.0+ (newer SDKs can still build the net8.0 target). Note that `dotnet run` is a normal-privilege process, so the corner shows `BASIC` (no CPU temperature/power/fan). Run it from an admin terminal, or build the exe and right-click → *Run as administrator* for full sensors.

### Built binary

```
PerfPanel.exe            # fullscreen: locates the 440×1920 / 1920×440 secondary display (borderless fill, Esc to quit)
PerfPanel.exe --windowed # debug: windowed on primary screen, freely resizable
PerfPanel.exe --landscape / --portrait   # force landscape / portrait (auto = by display shape, the default)
```

Requires the .NET 8 desktop runtime (WindowsDesktop 8.0.x).

**For full temperature/fan/power: right-click → "Run as administrator"**, and install the PawnIO driver once (double-click `dist/安装PawnIO驱动-解锁CPU温度.bat` — it installs the driver, swaps in the newest exe and restarts the panel). **Note: the installer itself is not committed (`.gitignore` excludes `dist/tools/`), so in a fresh clone you must first download `PawnIO_setup.exe` from the [PawnIO releases](https://github.com/namazso/PawnIO.Setup/releases) into `dist/tools/` — otherwise the script skips the install and exits right away (it flashes by and temperature still doesn't show).** Since LibreHardwareMonitor 0.9.5, CPU sensors go through [PawnIO](https://github.com/namazso/PawnIO.Setup) — a signed, Memory-Integrity(HVCI)-compatible driver that replaced the old WinRing0-style Ring0 driver Windows now blocks. Without it (or without admin), CPU temperature/power/clock rows stay hidden while NVIDIA/AMD GPU readings (user-mode APIs) keep working; the footer then says `CPU温度不可用(需安装 PawnIO 驱动)`, and the CPU frequency falls back to a WMI effective-frequency estimate that on some platforms never refreshes, so the number can stay frozen (a tell-tale sign of the degraded mode).

## App icon & desktop shortcut

The repo ships a generated multi-size icon (`src/PerfPanel/app.ico`, applied to the exe and window via `<ApplicationIcon>`). To put a shortcut on your desktop, double-click `dist/create-desktop-shortcut.bat` (or run `PerfPanel.exe --autostart-on`'s scripts below for boot autostart).

## Resource usage

Steady state on a Ryzen AI MAX+ 395 (1920×440 secondary, 125% DPI): about 2.8% of one core and ~290 MB RAM. LibreHardwareMonitor's full hardware sweep is throttled to ~1.5 s (sensor readouts are reused in between), the NIC list is refreshed every 15 s, WMI fallback queries are cached for 2 s, and all solid-color brushes are frozen and reused — UI/graphs still tick at 1 Hz.

## Autostart (built in, one-line CLI)

```
PerfPanel.exe --autostart-on       # enable: as admin → scheduled task (highest privileges, full sensors)
                                   #          normal privileges → registry Run key (basic mode)
PerfPanel.exe --autostart-off      # disable (cleans up both channels)
PerfPanel.exe --autostart-status   # show current registration state
```

The easiest way: double-click the ready-made scripts in `dist/` —

- `开机自启-开启(管理员,推荐).bat` ("Autostart on, admin, recommended"): one UAC prompt, registers a scheduled task; full sensors available right after boot
- `开机自启-开启(普通权限).bat` ("Autostart on, normal privileges"): no UAC prompt, but no CPU temperature/power/fan after boot
- `开机自启-关闭.bat` ("Autostart off")

> `dist/PerfPanel-开机自启-管理员.xml` is a template for manual import via `schtasks /create /xml ...` — rarely needed, since the scripts above register everything automatically. If you do import it manually, first change the path in its `<Command>` element to the actual location of `PerfPanel.exe` on your machine.

## Weather: custom location + refresh countdown

- The weather card shows "City · refreshes in N min" and auto-refreshes every 30 minutes
- China enhancement: Settings (`S` or ⚙) → "Data Source" — fill in an Amap *Web Service* key (free at [console.amap.com](https://console.amap.com), choose the "Web Service" type) to switch weather & geocoding to Amap: more stable inside China, with address-level precision. Leave it empty to keep Open-Meteo
- Custom city (Open-Meteo free geocoding; Chinese/pinyin/English all work):

```
PerfPanel.exe --set-city Shanghai
```

or double-click `dist/设置天气城市.bat` ("Set weather city") and type a city name; or edit `weather.json` next to the exe directly:

```json
{"lat": 31.2304, "lon": 121.4737, "city": "Shanghai"}
```

## Coding Plan quota monitoring

The CODING PLAN card shows each subscription's remaining quota and reset countdowns (auto-refresh every 15 minutes; last good values are kept on failure):

| Provider | Query | Shown |
| --- | --- | --- |
| DeepSeek | official `/user/balance` API | account balance (¥) |
| Zhipu GLM Coding Plan | unofficial API (same as CC Switch) | 5h/weekly window percentages + reset countdown |
| Volcano Ark Agent/Coding Plan | unofficial console OpenAPI (same as CC Switch) | 5h/weekly/monthly quota + reset countdown |
| Xiaomi MiMo | console API via browser login Cookie (same as CodexBar) | pay-as-you-go balance (¥) + Token Plan monthly usage % + period-end countdown |

**Where to put keys**: Settings (`S` or ⚙) → "Coding Plan Quota":

- **DeepSeek**: the API key from [platform.deepseek.com](https://platform.deepseek.com)
- **Zhipu GLM**: the API key from [open.bigmodel.cn](https://open.bigmodel.cn) (personal tier; GLM API keys not dedicated to Coding Plan also work)
- **Volcano Ark**: the **IAM AccessKey ID + Secret Access Key** from the Volcengine console ([volcengine.com](https://www.volcengine.com) → avatar at the top right → API access keys) — **not** the Ark inference API key. Quota queries go through the console-plane OpenAPI with AK/SK signing, so inference keys won't work
- **Xiaomi MiMo**: paste the **whole console Cookie header** (neither `tp-` nor `sk-` API keys can query usage — only a logged-in account session works). Log in at [platform.xiaomimimo.com](https://platform.xiaomimimo.com) → F12 → Network → refresh → click any `/api/v1` request → Request Headers → copy the full `Cookie` line (must contain `api-platform_serviceToken` and `userId`); re-copy when the session expires

Each item has "Save & Test" for instant validation; leave it empty and the panel simply skips that provider. Keys are stored in `config.json` next to the exe.

> The GLM/Volcano/MiMo integrations use unofficial APIs (mirroring CC Switch / CodexBar), so upstream field changes can break queries — on failure the panel shows the reason. The last raw Volcano response is saved to `volc-last-response.json` next to the exe, which helps when checking for missing/renamed fields.

## Notes / todos / focus reminders

The FOCUS card (a large card spanning the lower-left half in landscape) turns the secondary display into a focus board:

- **Sticky note**: your one-line current goal (e.g. "focus on Calculus chapter 3"), always visible in amber
- **Todos**: click a row right on the panel to toggle done (finished items get struck through and dimmed); pending items sort first
- **Focus reminders**: every 15/30/45/60/90 minutes one rotating message highlights for 90 seconds — defaults include "stay on your review, don't get distracted" and "check the revision kanban"; otherwise the card shows a countdown and the next message preview
- **Pomodoro timer**: an in-card progress ring counting down (mm:ss in the center, amber for work / green for break) with "▶ Start / ⏸ Pause / ↺ Reset / ⏭ Skip" — a finished work phase auto-starts the break, a finished break stops at ready-to-start; completion plays a system chime and shows an 8-second banner. Durations are chosen in Settings (15/25/45/50 min × 3/5/10/15 min) with a show/hide toggle; runtime state is memory-only (reset on restart), durations persist in `notes.json`

Edit everything in Settings (S or ⚙) → "Notes / focus": sticky note, add/remove todos, cycle length, custom messages (one per line), pomodoro durations & visibility. Content persists in `notes.json` next to the exe; untick "show notes card" to hide the whole thing.

## Build

```bash
# .NET 8 SDK required
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# output: src/PerfPanel/bin/Release/net8.0-windows/win-x64/publish/PerfPanel.exe (single file, ~3.7 MB)
```

Self-contained build (no runtime needed, ~150 MB):

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

> The repo ships no build output. The `.bat` scripts in `dist/` look for `PerfPanel.exe` in their own directory — copy the published exe into `dist/` before double-clicking them. (CLI flags such as `--autostart-on` also work with `dotnet run`; put them after `--`.)
>
> To refresh the README screenshot: put a `shot-request.txt` next to the exe (first line = output PNG path); 18 s after startup the panel renders the whole window at design size, saves the PNG and deletes the request file — unaffected by screen capture or DPI, works with or without admin.

## Architecture

```
src/PerfPanel/
├── MainWindow.xaml(.cs)        # portrait 440×1920 / landscape 1920×440 dual layout (shared cards), multi-monitor placement, 1s refresh loop
├── Controls/HistoryGraph.cs    # StreamGeometry hand-drawn history graphs (no chart library)
├── Models/SensorSnapshot.cs    # unified snapshot model (nullable fields = auto-hidden in UI)
└── Services/
    ├── HardwareMonitorService.cs  # LibreHardwareMonitor: temp/fan/power/frequency/VRAM
    ├── FallbackMonitorService.cs  # no-admin fallback: WMI localized perf classes + GlobalMemoryStatusEx
    ├── MonitorAggregator.cs       # dual-source aggregation, field-level merging
    ├── NetworkMonitorService.cs   # adapter throughput (sticky adapter choice)
    ├── WeatherService.cs          # Open-Meteo + geocoding, custom location, silent failure
    ├── AutostartService.cs        # autostart: scheduled task / registry dual channel
    └── MonitorHelper.cs           # EnumDisplayMonitors / SetWindowPos P/Invoke (no WinForms)
```

Design notes for future improvements (more sensors, appearance customization, orientation, todos, …) live in [docs/设计方案.md](docs/设计方案.md) (Chinese).

- Data sources: LibreHardwareMonitorLib **0.9.6** (CPU sensors read through the PawnIO kernel driver; historical note: 0.9.4 was once pinned because 0.9.5 read constant 0 for CPU sensors on some AMD laptops such as the 5800H — 0.9.6 replaced the Windows-blocked WinRing0 driver with PawnIO and the issue is gone; MIT) + Windows WMI
- Refresh: 1-second DispatcherTimer, sampling on background threads, render-only UI thread
- Measured footprint: ~130 MB private / ~190 MB working set (typical for WPF + LHM + WMI; about half of browser-based alternatives)

## License

[MIT](LICENSE)
