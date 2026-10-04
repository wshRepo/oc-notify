# oc-notify

**English** | [简体中文](README.md)

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Platform](https://img.shields.io/badge/platform-Windows%20only-blue)
![.NET](https://img.shields.io/badge/.NET-8.0-512BD4)
![opencode](https://img.shields.io/badge/opencode-v1.18%2B-000000)

**Windows-only** desktop bubble notifications for **[opencode](https://opencode.ai) CLI**: pop modern toast-style bubbles in the corner of your screen when a reply finishes, a permission is requested, AI asks a question, a session errors, or a subagent completes.

> ⚠️ **Windows 10/11 only** — built on WPF / named pipes / PowerShell. **macOS and Linux are not supported** and are not planned.

- **✨ WinUI 3 style design** — Microsoft Fluent Design language: rounded cards, smooth animations, frosted-glass look
- **🚫 No Windows Toast dependency** — fully custom WPF transparent window; free-form styling, fluid animations, no system notification limits
- **🧠 Smart, non-intrusive** — only pops when opencode is not in the foreground; short-task filtering and permission debounce keep it quiet
- **⚡ Zero-config setup** — one-click deploy, hot-reloadable config that applies on save

![Bubble preview](docs/Example.png)

## Table of Contents

- [Features](#features)
- [Requirements](#requirements)
- [Quick Start](#quick-start)
- [Usage](#usage)
- [Configuration Reference](#configuration-reference)
- [Troubleshooting](#troubleshooting)
- [Architecture](#architecture)
- [Development](#development)
- [Project Structure](#project-structure)
- [License](#license)

---

## Features

### Five notification events (each independently toggleable)

| type | When it fires | Dark-theme accent |
|------|---------------|-------------------|
| `sessionIdle` | AI finishes a reply (`session.idle`) | Green `#4ADE80` |
| `permissionAsk` | Permission approval required (`permission.asked`) | Amber `#FBBF24` |
| `questionAsk` | AI asks you a question (`question.asked`) | Purple `#A78BFA` |
| `sessionError` | Session error (`session.error`) | Red `#F87171` |
| `subagentDone` | Subagent / subtask done (idle with `parentID`) | Blue `#38BDF8` |

Labels follow `style.language`: Chinese by default (对话完成 / 权限请求 / …); with `en` they show Done / Permission / Question / Error / Subagent Done.

### Bubble UI (WinUI 3 / Fluent Design)

- **Polished cards**: accent bar + label + session title + rounded corners + shadow
- **Corner docking**: `top-left` / `top-right` (default) / `bottom-left` / `bottom-right`
- **Stacking**: top corners stack downward (old ones slide up when removed); bottom corners stack upward
- **Smooth animations**: slide-in fade on enter → fade-and-collapse on timeout/click
- **Dual themes**: `light` (default) / `dark`, with accent colors adjusted for contrast
- **Frosted glass look**: translucent card + bright border (the window itself stays fully transparent)
- **Multiple bubbles**: up to `maxVisible` on screen, oldest evicted beyond that; click to dismiss

### Smart behavior

- **Foreground-only suppression** (`onlyWhenInactive`, default on): no popups while opencode/terminal is focused
- **Anti-noise**: no double popup when idle follows an error within 2s; busy→idle under 2s treated as a short task; 300ms debounce on permission requests
- **Automatic lifecycle**: first CLI spawns `OcNotify.exe`; exits ~60s after all CLIs close; single-instance Mutex
- **Hot-reload config**: edit JSON and it applies immediately — no restarts

---

## Requirements

| Item | Requirement |
|------|-------------|
| OS | **Windows 10/11 x64 only** (macOS / Linux not supported; transparency/positioning work without Win11, frosted glass & system corners look best on Win11) |
| .NET | .NET 8 **Desktop Runtime** (x64) |
| opencode | v1.18+ (v1 plugin API `@opencode-ai/plugin`) |

The plugin runs inside opencode's bundled Bun — **no** separate Node/Bun install needed.

---

## Quick Start

### Option A: One-click deploy (recommended)

The repo's [`deploy/`](deploy/) directory ships a complete deployment package:

```
deploy/
├── install.bat                 ← double-click to run
├── merge-config.ps1            ← smart config merge script (called by install.bat)
└── files/                      ← everything needed to deploy
    ├── notify-bubble.ts        plugin
    ├── oc-notify.default.jsonc  default config template
    └── OcNotify/               bubble app (exe + dll + deps)
```

1. **Make sure .NET 8 Desktop Runtime (x64) is installed**
   - The script auto-detects it and exits with a download link if missing
   - Download: <https://dotnet.microsoft.com/download/dotnet/8.0>
   - Pick **".NET Desktop Runtime 8.0.x (x64)"** (not ASP.NET Runtime, not plain Runtime)
2. **Double-click `deploy\install.bat`**
3. The script detects the runtime → stops any running `OcNotify.exe` → copies plugin / app → smart-merges the config → starts the new `OcNotify.exe` (if one was running) → prints a deployment report
4. **Restart the opencode CLI** (plugins load only at startup)
5. Verify with [Usage](#usage) below

**Target locations** (created automatically):

| Source | Destination |
|--------|-------------|
| `files\notify-bubble.ts` | `%USERPROFILE%\.config\opencode\plugins\notify-bubble.ts` |
| `files\OcNotify\*` | `%USERPROFILE%\.config\opencode\assets\OcNotify\` |
| `files\oc-notify.default.jsonc` | `%USERPROFILE%\.config\opencode\oc-notify.jsonc` |

> **Config protection (smart merge)**: if `oc-notify.jsonc` already exists, the script **adds but never removes**: new template fields are appended (with their new comments), while your values, line comments, and fields/sections deleted from the template are kept as-is; the original is backed up to `oc-notify.jsonc.bak` before merging (if the file cannot be parsed, it is backed up first and rebuilt from the template — never silently overwritten).

**Upgrade**: double-click `install.bat` again to overwrite plugin and app files and merge the config, then restart opencode.

### Option B: Manual deploy (PowerShell)

From the repo root:

```powershell
$cfg  = "$env:USERPROFILE\.config\opencode"
$exe  = "$cfg\assets\OcNotify"
$plug = "$cfg\plugins"

New-Item -ItemType Directory -Force -Path $exe, $plug | Out-Null

# 1. Publish the bubble app
dotnet publish src\OcNotify\OcNotify.csproj -c Release -o $exe /p:DebugType=none

# 2. Deploy the plugin
Copy-Item src\plugin\notify-bubble.ts $plug -Force

# 3. Config (creates on first run; smart-merges if present: add-only, user values win, auto-backup .bak)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File deploy\merge-config.ps1 `
  -TemplatePath config\oc-notify.default.jsonc `
  -TargetPath "$cfg\oc-notify.jsonc"
```

### Verify the deployment

```powershell
# Start the app and confirm the pipe exists
Start-Process "$env:USERPROFILE\.config\opencode\assets\OcNotify\OcNotify.exe"
[System.IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -like "*oc-notify*" }

# Send one message without the plugin — a bubble should pop immediately
.\scripts\Send-TestNotification.ps1 -Title "Deploy check"
```

Direct send works → exe/pipe are fine; still nothing → the issue is on the plugin side (restart opencode).

### Uninstall

```powershell
Get-Process OcNotify -ErrorAction SilentlyContinue | Stop-Process -Force
$cfg = "$env:USERPROFILE\.config\opencode"
Remove-Item "$cfg\plugins\notify-bubble.ts" -Force -ErrorAction SilentlyContinue
Remove-Item "$cfg\assets\OcNotify" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$cfg\oc-notify.jsonc" -Force -ErrorAction SilentlyContinue   # also removes config; drop this line to keep it
Remove-Item "$cfg\oc-notify.jsonc.bak" -Force -ErrorAction SilentlyContinue   # backup created during smart merge
```

---

## Usage

### Day-to-day

After install + opencode restart, **nothing else to do**:

1. Chat in opencode as usual
2. Bubbles pop when a reply finishes, permission is needed, AI asks a question, etc.
3. Bubbles auto-dismiss after **5 seconds** by default, or **click to close** immediately
4. ~**60 seconds** after you close all opencode windows, `OcNotify.exe` exits; next launch auto-spawns it again

### When it pops vs. stays silent

| Scenario | Pops? |
|----------|-------|
| You switched away / minimized opencode, reply finished | ✅ Yes |
| You are focused on opencode (`onlyWhenInactive=true`, default) | ❌ No |
| Permission auto-approved within 300ms | ❌ No (debounced) |
| Error just happened, idle follows immediately | ❌ No (no double popup) |
| busy→idle under 2 seconds (short task) | ❌ No (noise filter) |
| Corresponding `events.xxx` turned off | ❌ No |

Want popups even in the foreground? Set `behavior.onlyWhenInactive` to `false` — applies on save.

### Config tuning example

```jsonc
// %USERPROFILE%\.config\opencode\oc-notify.jsonc — applies on save
{
  "style": {
    "position": "bottom-right",  // dock bottom-right
    "theme": "dark",             // dark theme
    "language": "en",            // English UI
    "opacity": 0.90              // slightly transparent
  },
  "behavior": {
    "durationMs": 8000,          // stay 8 seconds
    "onlyWhenInactive": true
  }
}
```

**Project-level config**: drop an `oc-notify.jsonc` in a project root; write only the sections you want to override (`style` / `behavior` / `events`). It merges with global config at runtime and applies only to that project.

Full fields: [Configuration Reference](#configuration-reference).

### Manual testing (no opencode required)

```powershell
# Single message (pick a category)
.\scripts\Send-TestNotification.ps1 -Type permissionAsk -Title "Manual test"

# Send 7 in a row: watch stacking, eviction, enter/exit animations
.\scripts\Send-StackTest.ps1 -Count 7

# type: sessionIdle | permissionAsk | questionAsk | sessionError | subagentDone
```

---

## Configuration Reference

**Global config**: `%USERPROFILE%\.config\opencode\oc-notify.jsonc`
**Project config**: `<project root>\oc-notify.jsonc` (overrides global per section)

Saving **hot-reloads immediately** — no process restarts. The file is jsonc: `//` and `/* */` comments and trailing commas are allowed.

### Full example

```jsonc
{
  "style": {
    "opacity": 0.90,             // card background alpha 0.0~1.0 (1.0 = opaque)
    "glassEffect": true,         // frosted look: true = translucent + bright border; false = solid dark
    "accentColor": "#7C9CFF",    // accent fallback for unknown categories
    "cornerRadius": 14,          // card corner radius (DIP)
    "position": "top-right",     // corner: top-left | top-right | bottom-left | bottom-right
    "theme": "light",            // theme: light | dark
    "language": "zh"             // UI language: zh (default) | en
  },
  "behavior": {
    "durationMs": 5000,          // auto-dismiss time (ms)
    "maxVisible": 5,             // max simultaneous bubbles; oldest evicted
    "clickToDismiss": true,      // click a bubble to close it
    "onlyWhenInactive": true,    // only pop when opencode is not focused (recommended)
    "debug": false,              // debug logging
    "idleCheckIntervalMs": 30000,// idle check interval (ms)
    "idleRetry": 2               // exit after N consecutive checks with no opencode
  },
  "events": {
    "sessionIdle": true,         // reply finished
    "permissionAsk": true,       // permission requested
    "questionAsk": true,         // AI question
    "sessionError": true,        // session error
    "subagentDone": true         // subagent done
  }
}
```

### Field reference

#### style

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `opacity` | number | `0.90` | Card background alpha |
| `glassEffect` | bool | `true` | Frosted-glass look (card-level, not window backdrop) |
| `accentColor` | string | `#7C9CFF` | Accent color fallback |
| `cornerRadius` | number | `14` | Corner radius |
| `position` | string | `top-right` | `top-left` / `top-right` / `bottom-left` / `bottom-right` |
| `theme` | string | `light` | `light` / `dark` |
| `language` | string | `zh` | UI language: `zh` / `en` (`en-xx` prefixes also work) |

#### behavior

| Field | Type | Default | Description |
|-------|------|---------|-------------|
| `durationMs` | int | `5000` | Auto-dismiss milliseconds |
| `maxVisible` | int | `5` | Max bubbles on screen |
| `clickToDismiss` | bool | `true` | Click to close |
| `onlyWhenInactive` | bool | `true` | Only pop when not focused (detection is plugin-side) |
| `debug` | bool | `false` | Debug logging |
| `idleCheckIntervalMs` | int | `30000` | exe idle check interval |
| `idleRetry` | int | `2` | Consecutive misses before exit; total grace ≈ interval × retry (~60s default) |

#### events

Five booleans, one per notification type; filtered plugin-side — disabled events never reach the pipe.

### File roles after deploy

```
plugins\notify-bubble.ts      → auto-loaded at opencode startup (listens, spawns exe on demand)
assets\OcNotify\OcNotify.exe  → the bubble app (single instance, idle-exits after ~60s)
oc-notify.jsonc               → global config (hot-reloads on save)
```

---

## Troubleshooting

| Symptom | What to check |
|---------|---------------|
| No popups at all | ① Is `OcNotify.exe` running ② Pipe exists: `[System.IO.Directory]::GetFiles("\\.\pipe\") \| ? { $_ -like "*oc-notify*" }` ③ Did you restart opencode (plugin loads at startup only) ④ With `debug=true`, check `%TEMP%\oc-notify-plugin.log` for `plugin init` |
| Should pop but doesn't | Enable `debug`: is it `event` missing, `skip: short task`, `skip: recent error`, or `emit skip ... foreground` (foreground block is expected) |
| Pops but wrong content | Check `sessionTitle`; log line `emit [type] title=...` |
| Wrong position | Verify `style.position`; check work area on multi-monitor; send a new notification after config hot-reload |
| exe doesn't exit | Any opencode process left (including unclosed CLIs)? Lower `idleCheckIntervalMs`/`idleRetry` to verify |
| exe doesn't auto-start | Log `ensureOcNotify: spawned` / `exe missing` / `pipe already up`; confirm `assets\OcNotify\OcNotify.exe` exists |
| Installer says runtime missing | Install **.NET Desktop Runtime 8.0.x x64**, re-run `install.bat` |
| Plugin changes not applied | Plugins load at startup only → **restart opencode** |
| Build errors | Run `dotnet build -warnaserror` and read the first error; confirm .NET 8 SDK |

**Quick diagnostic sequence**:

```powershell
# 1. Processes and pipe
Get-Process OcNotify, opencode -ErrorAction SilentlyContinue
[System.IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -like "*oc-notify*" }

# 2. Direct send (bypasses the plugin)
.\scripts\Send-TestNotification.ps1 -Title "Diagnostic"

# 3. Plugin log (requires debug=true)
Get-Content "$env:TEMP\oc-notify-plugin.log" -Tail 50
```

Direct send works → problem is plugin/config; direct send fails → problem is exe/pipe.

### Debug logs

With `"behavior": { "debug": true }`:

| Side | Log path |
|------|----------|
| Plugin | `%TEMP%\oc-notify-plugin.log` |
| exe errors | `%TEMP%\oc-notify-error.log` |

Format: `[ISO time] [INFO|ERROR] message`. Covers init/config snapshot, event arrival, short-task & error suppression, foreground-block reasons, pipe I/O, permission debounce, and all exceptions. `debug` hot-reloads.

---

## Architecture

### Overview

```
┌─────────────────────────────┐
│  opencode.exe (CLI/TUI)     │
│  ┌───────────────────────┐  │
│  │ notify-bubble.ts      │  │  events / filters / focus check
│  └──────────┬────────────┘  │
└─────────────┼───────────────┘
              │ named pipe \\.\pipe\oc-notify
              │ one JSON line per message
              ▼
┌─────────────────────────────┐
│  OcNotify.exe (WPF resident)│
│  ├ PipeServer    receive    │
│  ├ ConfigService hot reload │
│  ├ NotificationManager queue│
│  └ MainWindow   transparent │
│       └ NotificationCard×N  │  cards + animations
└─────────────────────────────┘
              ▼  bubbles in screen corners
```

**Process model**:

| Process | Count | Lifecycle |
|---------|-------|-----------|
| opencode.exe | one per CLI | opened/closed by user |
| OcNotify.exe | always 1 (Mutex) | spawned by first CLI's plugin → exits ~60s after last CLI closes |

### Event flow (「reply finished」 example)

1. `session.status=busy` → plugin records the **first** busy timestamp
2. Reply ends → `session.idle`
3. Filters: within 2s of an error? busy→idle < 2s? event disabled? focused on opencode? → skip on any match
4. Fetch session title (cached; with `parentID` classified as `subagentDone`)
5. One JSON line written to `\\.\pipe\oc-notify` (retry 2× × 500ms on failure)
6. exe enqueues → evicts oldest if over limit → creates card → enter animation → dock to anchor
7. Timeout/click → fade-and-collapse → remove → hide window when empty

### Foreground detection (onlyWhenInactive)

- On startup, collect the ancestor PID chain **opencode → shell → terminal** (once)
- Before each notification, read `GetForegroundWindow` PID:
  - In the ancestor chain → user is looking at opencode → don't pop
  - Detection failed → **allow** (better an extra popup than a missed one)
- No `bun:ffi` for Win32 (native crashes can't be try-caught; once broke the TUI)

### Single instance & idle exit

- **Single instance**: `Mutex("Local\OcNotify.SingleInstance")`; second instance exits immediately
- **Spawn**: plugin init checks the pipe; if absent, launches the exe via a `cmd /c start "" /b` trampoline (escapes Bun's kill-on-close Job so it isn't killed when opencode exits)
- **Idle exit**: `DispatcherTimer` (default 30s × 2 ≈ 60s) with no `opencode` process → exit; interval/count configurable and hot-reloadable

### Plugin threading model (never blocks opencode)

- `event` hook dispatches synchronously and returns immediately; async work is fully detached
- Permission debounce uses `setTimeout` — no sleeps in hooks
- Pipe writes are short sync ops + try-catch + bounded retries
- All exceptions swallowed and logged as `ERROR` — never leak into opencode

### Plugin event table

| Event | Handling |
|-------|----------|
| `session.created` / `session.updated` | cache title / parentID |
| `session.status` | record **first** busy timestamp (never overwrite) |
| `session.idle` | error suppression → short-task filter → fetch title → emit |
| `session.error` | record timestamp + emit immediately |
| `permission.updated` / `permission.asked` | emit after 300ms debounce |
| `question.asked` | fetch title → emit |

### Named pipe protocol

- **Pipe name**: `oc-notify` (`\\.\pipe\oc-notify`)
- **Format**: UTF-8, one JSON per line, `\n`-terminated; 4096-byte max per message
- **Concurrency**: up to 16 server instances

```json
{
  "type": "sessionIdle",
  "sessionID": "ses_xxxxxxxx",
  "sessionTitle": "My project",
  "timestamp": 1790000000000,
  "configPath": "D:\\proj\\oc-notify.jsonc"
}
```

| Field | Required | Description |
|-------|----------|-------------|
| `type` | ✅ | one of the five types (see Features) |
| `sessionID` | ✅ | opencode session ID |
| `sessionTitle` | ✅ | bubble title |
| `timestamp` | ✅ | Unix milliseconds |
| `configPath` | ❌ | project-level config path; exe merges with global |

**PowerShell example**:

```powershell
$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'oc-notify', 'Out')
$c.Connect(2000)
$w = New-Object System.IO.StreamWriter($c)
$w.Write('{"type":"sessionIdle","sessionID":"ses_t","sessionTitle":"hello","timestamp":1790000000000}' + "`n")
$w.Flush(); $c.Dispose()
```

**Node.js example**:

```js
const fs = require("fs");
const fd = fs.openSync("\\\\.\\pipe\\oc-notify", "w");
fs.writeSync(fd, JSON.stringify({ type: "questionAsk", sessionID: "s", sessionTitle: "hi", timestamp: Date.now() }) + "\n");
fs.closeSync(fd);
```

> Bun's `net.connect` is unreliable for Windows named pipes (ECONNREFUSED) — use `fs.openSync`.

---

## Development

### Build & run

```powershell
# Compile (warn-aserror)
dotnet build -warnaserror

# Run locally (Debug)
dotnet run --project src\OcNotify

# Publish into the deploy package (for install.bat)
dotnet publish src\OcNotify\OcNotify.csproj -c Release -o deploy\files\OcNotify /p:DebugType=none

# Sync plugin and config template into the deploy package
Copy-Item src\plugin\notify-bubble.ts deploy\files\ -Force
Copy-Item config\oc-notify.default.jsonc deploy\files\ -Force
```

### Standard workflow after changes

```powershell
# 1. Edit code
# 2. Build
dotnet build -warnaserror

# 3. Update deploy package + install (or copy only changed files)
dotnet publish src\OcNotify -c Release -o deploy\files\OcNotify /p:DebugType=none
Copy-Item src\plugin\notify-bubble.ts deploy\files\ -Force
# Double-click deploy\install.bat, or copy manually

# 4. Restart OcNotify.exe (plugin changes also need an opencode restart)
Get-Process OcNotify -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "$env:USERPROFILE\.config\opencode\assets\OcNotify\OcNotify.exe"
```

### Acceptance checklist (before opening a PR)

- [ ] Single message: slides in from anchor → fades and collapses after 5s
- [ ] Seven messages: stable at maxVisible, oldest evicted
- [ ] Click dismisses immediately; when empty, window hides (no taskbar icon, no focus steal)
- [ ] No popup while opencode is focused; pops after switching away
- [ ] Changing `theme`/`position`/`durationMs`/`language` takes effect on the next notification
- [ ] Two CLIs open → closing one doesn't kill the exe; closing all → exits after ~60s
- [ ] Launching the exe twice → only one instance survives

### Key design conventions

1. **Units**: `SystemParameters.WorkArea` and window coordinates are all **DIP** — never multiply by DPI again (once caused offset bugs)
2. **Window**: always `AllowsTransparency=true` + `Background=null`; draw cards only, no window-level backdrop (avoids gray rectangles)
3. **Anchors**: card spacing goes on the side away from the anchor (top corners: below; bottom corners: above); the fixed edge only counts the 16px config margin
4. **Plugin**: no `bun:ffi`, no `node:net` for the pipe; focus detection and spawning go through PowerShell / `fs.openSync`
5. **Eviction**: `Items.RemoveAt(0)` first to free a slot, then trigger the animation (otherwise infinite `while` loop)
6. **Busy timestamp**: record only the first; repeated busy near idle must not overwrite

### Code style

- **C#**: full XML doc comments in Chinese (explain *why*, not *what*); file I/O/network must try-catch + retry 3×
- **TS**: `debugLog` on critical paths; all exceptions caught and logged as `ERROR`
- **DRY**: category colors/labels live in `CategoryInfo`; config reads live in `ConfigService` / `loadConfig`

---

## Project Structure

```
oc-notify/
├── oc-notify.slnx                    # solution
├── deploy/
│   ├── install.bat                   # one-click deploy script
│   ├── merge-config.ps1              # smart config merge script
│   └── files/                        # distributable package
│       ├── notify-bubble.ts
│       ├── oc-notify.default.jsonc
│       └── OcNotify/                 # exe + dll + deps
├── config/
│   └── oc-notify.default.jsonc     # default config template (source)
├── docs/
│   └── Example.png                   # README screenshot (five bubble types)
├── scripts/
│   ├── Send-TestNotification.ps1     # single pipe test
│   └── Send-StackTest.ps1            # burst test (stacking/eviction)
├── src/
│   ├── OcNotify/                     # C# WPF bubble app
│   │   ├── OcNotify.csproj           # net8.0-windows / UseWPF
│   │   ├── App.xaml(.cs)             # single instance, wiring, idle exit
│   │   ├── MainWindow.xaml(.cs)      # transparent host, corner anchors, DPI
│   │   ├── Controls/
│   │   │   └── NotificationCard.xaml(.cs)  # card style + animations
│   │   ├── Models/                   # NotifyConfig / NotifyMessage / NotificationItem
│   │   ├── Services/                 # PipeServer / ConfigService / NotificationManager / DwmHelper
│   │   └── Helpers/                  # CategoryInfo (labels/colors, bilingual + dual theme)
│   └── plugin/
│       └── notify-bubble.ts          # opencode plugin
└── .gitignore
```

### Known optional enhancements

- `dotnet publish /p:PublishSingleFile=true --self-contained` to collapse into a single exe (size vs. convenience; build it yourself)

---

## License

This project is licensed under the [MIT License](LICENSE).
