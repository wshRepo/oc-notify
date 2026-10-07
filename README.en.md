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
- [Known Limitations](#known-limitations)
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

- **Foreground-only suppression** (`onlyWhenInactive`, default on): no popups while opencode/terminal is focused (tabs within one WT window are not distinguished — see [Known Limitations](#known-limitations))
- **Anti-noise**: no double popup when idle follows an error within 2s; busy→idle under 2s treated as a short task; 300ms debounce on permission requests
- **Automatic lifecycle**: first CLI spawns `OcNotify.exe`; exits ~60s after all CLIs close; single-instance Mutex
- **Sleep prevention** (`preventSleep`, default off): suppresses "sleep after inactivity" while any opencode session is busy; restores automatically once idle
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
3. The script detects the runtime → stops any running `OcNotify.exe` → copies plugin / app → smart-merges the config → prints a deployment report
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
| Focus is on **another tab of the same WT window**, and that tab's opencode finished | ❌ No ([known limitation](#known-limitations)) |

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
    "onlyWhenInactive": true,
    "preventSleep": true         // don't let the machine sleep while you work (strongly recommended for long builds / installs)
  }
}
```

**Project-level config**: drop an `oc-notify.jsonc` in a project root; write only the fields you want to override (e.g. just `{"behavior":{"preventSleep":true}}`). It merges with global config **field by field** at runtime and applies only to that project.

Full fields: [Configuration Reference](#configuration-reference).

### Manual testing (no opencode required)

```powershell
# Single message (pick a category)
.\scripts\Send-TestNotification.ps1 -Type permissionAsk -Title "Manual test"

# Send 7 in a row: watch stacking, eviction, enter/exit animations
.\scripts\Send-StackTest.ps1 -Count 7

# type: sessionIdle | permissionAsk | questionAsk | sessionError | subagentDone

# Sleep prevention: simulate "session busy", then run `powercfg /requests` as admin
.\scripts\Send-PowerTest.ps1 -Hold
.\scripts\Send-PowerTest.ps1          # restore sleep
```

---

## Configuration Reference

**Global config**: `%USERPROFILE%\.config\opencode\oc-notify.jsonc`
**Project config**: `<project root>\oc-notify.jsonc` (overrides global field by field)

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
    "sticky": false,             // sticky bubble: stays until clicked (durationMs ignored, clickToDismiss forced on)
    "onlyWhenInactive": true,    // only pop when opencode is not focused (recommended)
    "debug": false,              // debug logging
    "idleCheckIntervalMs": 30000,// idle check interval (ms)
    "idleRetry": 2,              // exit after N consecutive checks with no opencode
    "preventSleep": false        // block Windows from auto-sleeping (default off)
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
| `sticky` | bool | `false` | Sticky bubble: never auto-dismisses, must click to close (`durationMs` ignored, `clickToDismiss` forced on) |
| `onlyWhenInactive` | bool | `true` | Only pop when not focused (detection is plugin-side) |
| `debug` | bool | `false` | Debug logging |
| `idleCheckIntervalMs` | int | `30000` | exe idle check interval |
| `idleRetry` | int | `2` | Consecutive misses before exit; total grace ≈ interval × retry (~60s default) |
| `preventSleep` | bool | `false` | Block system auto-sleep while any session is busy, restore when idle (see [Sleep prevention](#sleep-prevention-preventsleep)) |

#### events

Five booleans, one per notification type; filtered plugin-side — disabled events never reach the pipe.

### Sleep prevention `preventSleep`

Windows' "sleep after 10 minutes of inactivity" regularly puts a machine to sleep while a long build, dependency install or model training is still running. Turn this on and it won't:

```jsonc
// %USERPROFILE%\.config\opencode\oc-notify.jsonc
{ "behavior": { "preventSleep": true } }
```

| Moment | Behavior |
|--------|----------|
| Any session goes busy (AI starts working) | Requests a system power request — **blocks automatic sleep/hibernate** |
| All sessions go idle (AI finished) | Releases it; the machine sleeps normally per your existing "sleep after inactivity" setting |
| You set `preventSleep` back to `false` while running | Released within 1 second — no event needed |

**What it blocks, what it doesn't**

- ✅ Blocks: automatic sleep/hibernate from inactivity timers — exactly the thing you want gone
- ❌ Doesn't block: display power-off (no `ES_DISPLAY_REQUIRED` is requested; the screen still turns off per system settings)
- ❌ Doesn't block: manual sleep/shutdown (`Win+C` → Sleep, the power button) — **deliberate**, the user always keeps final control

**How it works**: a dedicated thread inside the exe calls `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` to reset the system idle timer, and `SetThreadExecutionState(ES_CONTINUOUS)` to clear it. `powercfg` power plans are never touched, so global settings stay pristine. Verify with:

```powershell
# elevated PowerShell
powercfg /requests   # should list [PROCESS] OcNotify.exe while a session is busy
```

**Four layers guaranteeing it can never get stuck awake**

| Layer | Trigger | Upper bound |
|-------|---------|-------------|
| exe lease | No heartbeat from the plugin for 120s | **120s** (hard guarantee, independent of any process state) |
| Plugin heartbeat | Idempotent re-send every 60s | 60s |
| exe idle exit | No opencode process ×2 | ~60s |
| Process death | opencode / exe exits | Immediate (the kernel reclaims all power requests held by that process) |

**Multiple opencode instances**: the request is held while *any* session is busy, and released only when *all* sessions are idle.

> This toggle follows opencode's **session busy/idle state** only — it does not track whether you're typing. If you want "never sleep while opencode is open", weigh that carefully: the machine would then never sleep.

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
| Multiple tabs in one WT window, only one ever pops | Hits the [known limitation](#known-limitations): `onlyWhenInactive` can tell windows apart, not tabs |
| Pops but wrong content | Check `sessionTitle`; log line `emit [type] title=...` |
| Wrong position | Verify `style.position`; check work area on multi-monitor; send a new notification after config hot-reload |
| exe doesn't exit | Any opencode process left (including unclosed CLIs)? Lower `idleCheckIntervalMs`/`idleRetry` to verify |
| exe doesn't auto-start | Log `ensureOcNotify: spawned` / `exe missing` / `pipe already up`; confirm `assets\OcNotify\OcNotify.exe` exists |
| Sleep prevention not working | ① Confirm `preventSleep: true` is saved ② Run `powercfg /requests` as admin and look for `OcNotify.exe` ③ Enable `debug` and check `%TEMP%\oc-notify-error.log` for `PowerGuard: sleep blocked/released` |
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
| exe errors | `%TEMP%\oc-notify-error.log` (`PowerGuard` blocked/released entries land here too) |

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
│  ├ PowerGuard    power req  │  consumes type=power control messages
│  └ MainWindow   transparent │
│       └ NotificationCard×N  │  cards + animations
└─────────────────────────────┘
          ├─→ bubbles in screen corners
          └─→ SetThreadExecutionState (block / restore auto-sleep)
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

- Resolution stops at the **window** level — tabs sharing one WT window cannot be told apart → see [Known Limitations](#known-limitations)

### Sleep prevention — division of labour

- **Decided plugin-side**: the desired state is computed **live from the `inflight` set** on every call (`busy → add`, `idle → delete`). No second counter exists, so it can never drift from the busy/idle state
- **Call sites cover exactly every mutation of `inflight`**: the busy branch, `onIdle` right after `delete` and *before* every early `return`, the 60s `gcTimer` heartbeat, and `dispose`
- **The heartbeat is an idempotent re-send**: when the desired state is unchanged the exe doesn't call the Win32 API at all, so a re-send can **never** release a still-running long task
- **Executed exe-side**: one dedicated thread calls `SetThreadExecutionState` (it's a thread-level API — the acquiring thread and the clearing thread must be the same one)
- **Independent exe-side veto**: even if the plugin still requests it, flipping the config to `false` releases immediately (driven by `ConfigService.ConfigChanged`)

Config and usage: [Sleep prevention `preventSleep`](#sleep-prevention-preventsleep).

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
| `session.status` | record **first** busy timestamp (never overwrite) + `syncPower()` to request sleep prevention |
| `session.idle` | `syncPower()` to release sleep prevention → error suppression → short-task filter → fetch title → emit |
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
| `type` | ✅ | one of the five notification types (see Features), or the `power` control message |
| `sessionID` | ✅ | opencode session ID (may be omitted for `power`) |
| `sessionTitle` | ✅ | bubble title (may be omitted for `power`) |
| `timestamp` | ✅ | Unix milliseconds |
| `configPath` | ❌ | project-level config path; exe merges with global |
| `hold` | ❌ | only meaningful for `type: "power"`: `true` = request blocking auto-sleep, `false` = restore |

**Control message** (`type: "power"`): produces no bubble; consumed by `PowerGuard` inside the exe, which only cares about `hold` and `configPath`.

```json
{ "type": "power", "hold": true, "timestamp": 1790000000000 }
```

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
- [ ] `preventSleep: true` + busy session → `powercfg /requests` lists `[PROCESS] OcNotify.exe`
- [ ] After idle the entry disappears within 120s; flipping back to `false` → gone within 1s
- [ ] exe CPU stays low while sleep prevention is on (the PowerGuard thread polls every 5s — it must never spin)

### Key design conventions

1. **Units**: `SystemParameters.WorkArea` and window coordinates are all **DIP** — never multiply by DPI again (once caused offset bugs)
2. **Window**: always `AllowsTransparency=true` + `Background=null`; draw cards only, no window-level backdrop (avoids gray rectangles)
3. **Anchors**: card spacing goes on the side away from the anchor (top corners: below; bottom corners: above); the fixed edge only counts the 16px config margin
4. **Plugin**: no `bun:ffi`, no `node:net` for the pipe; focus detection and spawning go through PowerShell / `fs.openSync`
5. **Eviction**: `Items.RemoveAt(0)` first to free a slot, then trigger the animation (otherwise infinite `while` loop)
6. **Busy timestamp**: record only the first; repeated busy near idle must not overwrite
7. **Config merging**: must happen field by field at the `JsonNode` level. Once deserialized into a POCO you can no longer tell "field absent" from "field happens to equal its default", so any "use whichever section project provided" approach silently resets whole sections to defaults
8. **Wake primitives**: use a counting `SemaphoreSlim` for cross-thread wakeups — **not** `ManualResetEventSlim`. It's latching: after `Set()` without a `Reset()` the waiter spins at 100% CPU
9. **Native calls must be idempotent**: skip the Win32 call when the state hasn't changed. A repeated heartbeat that actually calls every time causes needless state churn

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
│   ├── Send-StackTest.ps1            # burst test (stacking/eviction)
│   └── Send-PowerTest.ps1            # sleep-prevention control test (pair with powercfg /requests)
├── src/
│   ├── OcNotify/                     # C# WPF bubble app
│   │   ├── OcNotify.csproj           # net8.0-windows / UseWPF
│   │   ├── App.xaml(.cs)             # single instance, wiring, idle exit, pipe routing
│   │   ├── MainWindow.xaml(.cs)      # transparent host, corner anchors, DPI
│   │   ├── Controls/
│   │   │   └── NotificationCard.xaml(.cs)  # card style + animations
│   │   ├── Models/                   # NotifyConfig / NotifyMessage / NotificationItem
│   │   ├── Services/                 # PipeServer / ConfigService / NotificationManager / PowerGuard / DwmHelper
│   │   └── Helpers/                  # CategoryInfo (labels/colors, bilingual + dual theme) / DiagLog (exe-side log)
│   └── plugin/
│       └── notify-bubble.ts          # opencode plugin
└── .gitignore
```

### Known optional enhancements

- `dotnet publish /p:PublishSingleFile=true --self-contained` to collapse into a single exe (size vs. convenience; build it yourself)

---

## Known Limitations

These are inherent boundaries of the current design, not misconfiguration.

### Tabs within a single Windows Terminal window don't notify each other

`onlyWhenInactive` decides "is the user looking at opencode" by checking whether the foreground window PID falls inside opencode's ancestor process chain. Windows Terminal is a multi-window, multi-process host: **each window is its own `WindowsTerminal.exe` process, and all tabs in that window share one window handle**.

The ancestor chain therefore resolves to the **window**, never to the **tab**:

| Scenario | Result |
|----------|--------|
| You work in tab A, and **tab B**'s opencode finishes | ❌ No popup (misread as "opencode is foreground") |
| Focus sits on any WT window → **every** tab's opencode in that window finishing | ❌ No popups at all |
| Focus left all WT windows (e.g. switched to a browser) | ✅ Pops normally |
| Focus in WT window 1, opencode in window 2 finishes (`Ctrl+Shift+N`, a **separate process**) | ✅ Pops normally |

To confirm this is what blocked you, look for `shouldNotify: fg=<pid> inAncestors=true allow=false` in the debug log.

**Why there is no per-tab detection**

Each CLI runs its own plugin process, and the plugin can tell exactly which tab it belongs to via the `WT_SESSION` environment variable. But the check needs the mapping in the **opposite** direction — "which tab does the current foreground window correspond to" — and that mapping has no solution:

- `GetForegroundWindow()` returns only the top-level window handle; WT keeps its `HWND → active tab` relation entirely internal and **exposes no API for it**
- ConPTY is headless and has no notion of visibility, so a shell cannot detect which tab holds focus
- The only viable route, UI Automation, is tightly coupled to WT versions and adds 50–200ms per check — the stability and performance cost outweighs the benefit

**Workarounds**

- Set `behavior.onlyWhenInactive` to `false` and switch to "better too many than too few"
- Or spread concurrent work across **separate WT windows** rather than tabs in one window

### `onlyWhenInactive` also blocks permission requests

`permissionAsk` / `questionAsk` share the same foreground filter as the other events. If focus is on another app while an opencode inside some WT window is waiting for you to approve a permission, the bubble is swallowed — you have to switch back to that window to see the prompt.

### Ancestor PIDs are collected only once at plugin startup

`collectAncestorPids` runs during plugin init only. If the terminal window hosting opencode is rebuilt (WT closed and reopened, terminal host restart, …), the PID set goes stale and detection degrades to "only opencode's own PID counts as foreground", which **allows** in most cases — i.e. noisier than expected rather than quieter. Restarting opencode restores it.

### Sleep prevention follows session busy/idle, not your keystrokes

`preventSleep` is driven purely by opencode's `session.status` (busy / idle) and **does not observe Windows-level user input**. Hence these boundaries:

| Scenario | Result |
|----------|--------|
| AI is running a long build and you're watching it | ✅ stays awake (the target case) |
| AI finished, you walk to the kitchen, machine sleeps after 10 min | ✅ sleeps normally (as intended) |
| AI is idle but you're running a long `!npm run build` from the TUI that **doesn't count as session busy** | ❌ not held — the machine still sleeps per system settings |
| Laptop on Modern Standby with a long session open | ⚠️ drains the battery fast (won't sleep even with the lid closed) |

The third row is the only real gap: whether `!`-prefixed commands advance the session state depends on your opencode version. If your long work is mostly driven by `!` commands, lengthen the system sleep timeout instead, or keep the machine awake manually during that window.

Also note `ES_SYSTEM_REQUIRED` only blocks **automatic** sleep. If the hidden power setting `AllowSystemRequired` (`powercfg` alias `SYSTEMREQUIRED`) is set to 0, application requests are ignored by the system; its default is 1, so normal machines are unaffected.

---

## License

This project is licensed under the [MIT License](LICENSE).
