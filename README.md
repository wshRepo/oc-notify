# oc-notify

为 **opencode CLI** 定制的桌面气泡通知工具：对话完成、权限请求、AI 提问、会话错误、子代理完成时，在屏幕角落弹出现代化气泡提醒，支持多气泡堆叠、四角停靠、浅色/深色主题、毛玻璃观感与丰富的 JSON 配置。

> **不使用 Windows 自带通知（Toast）**，完全自绘 WPF 透明窗口，样式自由、动画流畅。

---

## 目录

- [功能特性](#功能特性)
- [架构总览](#架构总览)
- [环境要求](#环境要求)
- [快速开始](#快速开始)
- [配置说明](#配置说明)
- [工作原理](#工作原理)
- [opencode 插件](#opencode-插件)
- [命名管道协议](#命名管道协议)
- [项目结构](#项目结构)
- [开发指南](#开发指南)
- [测试与调试](#测试与调试)
- [部署与运维](#部署与运维)
- [故障排查](#故障排查)
- [路线图](#路线图)

---

## 功能特性

### 提醒事件（5 类，均可单独开关）

| type | 触发时机 | 默认分类色（深色主题） |
|------|----------|------------------------|
| `sessionIdle` | AI 一轮回复完成（session.idle） | 绿 `#4ADE80` |
| `permissionAsk` | 需要用户批准权限（permission.asked） | 琥珀 `#FBBF24` |
| `questionAsk` | AI 向你提问（question.asked） | 紫 `#A78BFA` |
| `sessionError` | 会话出错（session.error） | 红 `#F87171` |
| `subagentDone` | 子代理/子任务完成（idle 且存在 parentID） | 蓝 `#38BDF8` |

### 气泡 UI

- **现代化卡片**：分类色条 + 标签 + 会话标题 + 圆角 + 阴影
- **四角停靠**：`top-left` / `top-right`（默认）/ `bottom-left` / `bottom-right`
- **堆叠规则**：
  - 上方两角：最高点固定，新气泡向下叠加，旧气泡消失后其余**上移**
  - 下方两角：最低点固定，新气泡向上叠加，旧气泡消失后其余**下移**
- **动画**：进入滑入淡入 → 超时/点击渐隐收拢，动作连贯
- **主题**：`light`（默认）/ `dark`，分类色自动切换保证对比度
- **毛玻璃观感**：卡片半透明 + 亮描边（窗口本体始终全透明，无灰色底板）
- **多气泡**：同时最多 `maxVisible` 条，超出挤出最旧；卡片间距方向远离锚点边
- **点击关闭**：可配置

### 智能行为

- **仅非前台弹窗**（`onlyWhenInactive`，默认开）：opencode/终端处于前台时不打扰，切走或最小化后才弹
- **反误报**：
  - error 后 2s 内的 idle 不再二次弹窗
  - busy→idle 不足 2s 视为短任务跳过
  - 权限请求 300ms 消抖（自动批准的不弹）
- **生命周期**：首个 opencode CLI 启动时自动拉起 `OcNotify.exe`；所有 CLI 退出后约 60s 自动关闭；单实例 Mutex 防重复
- **全配置热更新**：改 JSON 即时生效，无需重启

---

## 架构总览

```
┌─────────────────────────────┐
│  opencode.exe (CLI/TUI)     │
│  ┌───────────────────────┐  │
│  │ notify-bubble.ts 插件  │  │  事件监听 / 过滤 / 前台检测
│  └──────────┬────────────┘  │
└─────────────┼───────────────┘
              │ 命名管道 \\.\pipe\oc-notify
              │ 一行 JSON / 次
              ▼
┌─────────────────────────────┐
│  OcNotify.exe (WPF 常驻)    │
│  ├ PipeServer   接收消息    │
│  ├ ConfigService 配置热更   │
│  ├ NotificationManager 队列 │
│  └ MainWindow   透明堆叠窗  │
│       └ NotificationCard×N  │  气泡卡片 + 动画
└─────────────────────────────┘
              │
              ▼  屏幕四角气泡
```

**进程模型**：

| 进程 | 数量 | 生命周期 |
|------|------|----------|
| opencode.exe | N 个 CLI 各一个 | 用户手动开关 |
| OcNotify.exe | 恒为 1（Mutex 单实例） | 首个 CLI 插件拉起 → 最后一个 CLI 退出后 idle 检测自动退出 |

---

## 环境要求

- **OS**：Windows 10/11（毛玻璃/去边框等 DWM 特性在 Win11 上最佳；定位与透明不依赖 Win11）
- **.NET**：.NET 8 Desktop Runtime（framework-dependent 发布；本机装有 .NET SDK 亦可）
- **opencode**：v1.18+（使用 v1 插件接口 `@opencode-ai/plugin`）
- **运行时宿主**：插件在 opencode 内置 Bun 中执行，使用 `node:fs` / `Bun.spawn` 等

---

## 快速开始

### 1. 构建气泡程序

```powershell
# 在仓库根目录
dotnet build -c Release

# 发布到 opencode 配置目录（固定路径，插件按此路径拉起）
dotnet publish src\OcNotify\OcNotify.csproj -c Release `
  -o "$env:USERPROFILE\.config\opencode\assets\OcNotify"
```

产物：

```
~\.config\opencode\assets\OcNotify\
├── OcNotify.exe              # 启动器（必须）
├── OcNotify.dll              # 业务代码（必须）
├── OcNotify.runtimeconfig.json
├── OcNotify.deps.json
└── OcNotify.pdb              # 调试符号（可删，仅影响崩溃堆栈可读性）
```

> 不要放在 `plugins\` 目录——那是 opencode 自动加载 JS/TS 插件的地方。

### 2. 部署插件

```powershell
Copy-Item src\plugin\notify-bubble.ts `
  "$env:USERPROFILE\.config\opencode\plugins\notify-bubble.ts" -Force
```

全局插件目录：`~\.config\opencode\plugins\`（所有项目生效）。

### 3. 写入默认配置

```powershell
Copy-Item config\oc-notify.default.json `
  "$env:USERPROFILE\.config\opencode\oc-notify.json" -Force
```

### 4. 使用

1. **重启 opencode**（插件仅在启动时加载）
2. 发一条消息，等 AI 回复完成
3. **切到其他窗口或最小化 opencode** → 右上角应弹出「对话完成」气泡
4. 关闭所有 opencode 后约 60 秒，`OcNotify.exe` 自动退出

### 不依赖 opencode 的快速验证

```powershell
# 确保 OcNotify.exe 在跑（或手动启动）
Start-Process "$env:USERPROFILE\.config\opencode\assets\OcNotify\OcNotify.exe"

# 单条测试
.\scripts\Send-TestNotification.ps1 -Type permissionAsk -Title "手动测试"

# 连发 7 条（验证堆叠/挤出/动画）
.\scripts\Send-StackTest.ps1 -Count 7
```

---

## 配置说明

**全局配置**：`~\.config\opencode\oc-notify.json`  
**项目级配置**：`<项目根>\oc-notify.json`（存在时随消息的 `configPath` 传给 exe，按段覆盖全局）

保存后**即时热更新**，无需重启任何进程。

### 完整示例

```jsonc
{
  "style": {
    "opacity": 1.0,              // 卡片背景不透明度 0.0~1.0，1.0=完全不透明
    "glassEffect": true,         // 毛玻璃观感：true=半透明底+亮描边；false=纯色深底
    "accentColor": "#7C9CFF",    // 未知分类时的强调色（色条/标签回退）
    "cornerRadius": 14,          // 卡片圆角（DIP）
    "position": "top-right",     // 停靠角：top-left | top-right | bottom-left | bottom-right
    "theme": "light"             // 主题：light | dark
  },
  "behavior": {
    "durationMs": 5000,          // 气泡自动消失时间（毫秒）
    "maxVisible": 5,             // 最大同时显示条数，超出挤出最旧
    "clickToDismiss": true,      // 点击气泡立即关闭
    "onlyWhenInactive": true,    // 仅 opencode 非前台时弹窗（推荐保持 true）
    "debug": false,              // 调试日志开关，见「测试与调试」
    "idleCheckIntervalMs": 30000,// 空闲检测间隔（毫秒）
    "idleRetry": 2               // 连续 N 次未发现 opencode 才退出
  },
  "events": {
    "sessionIdle": true,         // 对话完成
    "permissionAsk": true,       // 权限请求
    "questionAsk": true,         // AI 提问
    "sessionError": true,        // 会话错误
    "subagentDone": true         // 子代理完成
  }
}
```

### 字段速查

#### style

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `opacity` | number | `1.0` | 卡片背景 alpha，直接生效 |
| `glassEffect` | bool | `true` | 毛玻璃观感开关（卡片级样式，非窗口 backdrop） |
| `accentColor` | string | `#7C9CFF` | 强调色回退 |
| `cornerRadius` | number | `14` | 圆角半径 |
| `position` | string | `top-right` | 四角停靠 |
| `theme` | string | `light` | `light` / `dark` |

#### behavior

| 字段 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `durationMs` | int | `5000` | 自动消失毫秒数 |
| `maxVisible` | int | `5` | 同屏最大气泡数 |
| `clickToDismiss` | bool | `true` | 点击关闭 |
| `onlyWhenInactive` | bool | `true` | 非前台才弹（检测在插件侧） |
| `debug` | bool | `false` | 调试日志 |
| `idleCheckIntervalMs` | int | `30000` | exe 空闲检查间隔 |
| `idleRetry` | int | `2` | 连续未发现次数，总宽限 ≈ interval × retry |

#### events

五个 bool，对应五类提醒的启用开关，插件侧过滤，关闭后完全不发送管道消息。

---

## 工作原理

### 事件流（以「对话完成」为例）

1. opencode 产生 `session.status=busy` → 插件记录首次 busy 时间戳
2. 回复结束产生 `session.idle`
3. 插件检查：
   - error 后 2s 内？→ 跳过（避免错误双弹）
   - busy→idle < 2s？→ 跳过（短任务降噪）
   - `events.sessionIdle=false`？→ 跳过
   - `onlyWhenInactive` 且前台是自己/终端？→ 跳过
4. 拉取 session title（带缓存；有 `parentID` 则归为 `subagentDone`）
5. 组装一行 JSON，写入 `\\.\pipe\oc-notify`（失败自动重试 2 次 × 500ms）
6. `OcNotify.exe` 的 `PipeServer` 收到 → `NotificationManager` 入队
7. 超出 `maxVisible` 先挤出最旧 → 创建卡片 → 播放进入动画 → 贴锚点
8. 到时/点击 → 渐隐 + 收拢 → 移除 → 空则隐藏窗口

### 非前台检测（onlyWhenInactive）

插件启动时用 PowerShell 沿 `ParentProcessId` 收集 **opencode → shell → 终端** 的 PID 集合（一次）；每次发通知前用 PowerShell 取 `GetForegroundWindow` 的 PID：

- 前台 PID ∈ 祖先链 → 用户正看着 opencode → 不弹
- 检测失败 → **放行**（宁可多弹不漏报）

> 不使用 `bun:ffi` 直调 Win32——原生崩溃无法 try-catch，曾导致 TUI 挂掉。

### 单实例与空闲退出

- **单实例**：`Mutex("Local\OcNotify.SingleInstance")`，第二个实例立即退出
- **拉起**：插件 init 时检查管道是否存在，不存在则 `Bun.spawn` exe；管道已存在则跳过
- **空闲退出**：exe 内 `DispatcherTimer`（默认 30s）检查 `Process.GetProcessesByName("opencode")`，连续 2 次为 0（默认约 60s 宽限）后 `Shutdown`；间隔/次数支持配置热更

### 线程模型（插件侧，不阻塞 opencode）

- `event` 钩子**同步 dispatch 后立即返回**，async 工作全部 `void` 掉
- 权限消抖用 `setTimeout(300ms)`，钩子里不 sleep
- 管道写入同步短操作 + try-catch + 有界重试
- 所有异常吞掉并记 `[ERROR]` 日志，绝不外溢到 opencode

---

## opencode 插件

**文件**：`src/plugin/notify-bubble.ts` → 部署至 `~\.config\opencode\plugins\notify-bubble.ts`

### 监听的事件

| 事件 | 处理 |
|------|------|
| `session.created` / `session.updated` | 缓存 session title / parentID |
| `session.status` | `busy` 记录首次时间戳（不覆盖，防短任务误判） |
| `session.idle` | 主路径：error 抑制 → 短任务过滤 → 取 title → emit |
| `session.error` | 记 error 时间戳 + 立即 emit |
| `permission.updated` / `permission.asked` | 300ms 消抖后 emit |
| `question.asked` | 取 title 后 emit |

### 与配置的交互

- 每次 `emit` 前 `loadConfig` 重读（文件极小），实现 events/behavior/debug 热更
- 项目根若有 `oc-notify.json`，路径放入消息 `configPath`，exe 侧做段级合并

### 手动更新插件

```powershell
Copy-Item src\plugin\notify-bubble.ts `
  "$env:USERPROFILE\.config\opencode\plugins\notify-bubble.ts" -Force
# 重启 opencode 生效
```

---

## 命名管道协议

- **管道名**：`oc-notify`（完整路径 `\\.\pipe\oc-notify`）
- **方向**：客户端 → 服务端，UTF-8，**一行一条 JSON**，以 `\n` 结尾
- **单条上限**：4096 字节；坏消息静默丢弃
- **并发**：服务端最多 16 个实例，支持多 session 同时写入

### 消息示例

```json
{
  "type": "sessionIdle",
  "sessionID": "ses_xxxxxxxx",
  "sessionTitle": "我的项目",
  "timestamp": 1790000000000,
  "configPath": "D:\\proj\\oc-notify.json"
}
```

| 字段 | 必填 | 说明 |
|------|------|------|
| `type` | ✅ | `sessionIdle` / `permissionAsk` / `questionAsk` / `sessionError` / `subagentDone` |
| `sessionID` | ✅ | opencode 会话 ID |
| `sessionTitle` | ✅ | 气泡上显示的标题 |
| `timestamp` | ✅ | Unix 毫秒 |
| `configPath` | ❌ | 项目级配置绝对路径，exe 读取后与全局合并 |

### 任何语言一行发送示例

**PowerShell**

```powershell
$c = New-Object System.IO.Pipes.NamedPipeClientStream('.', 'oc-notify', 'Out')
$c.Connect(2000)
$w = New-Object System.IO.StreamWriter($c)
$w.Write('{"type":"sessionIdle","sessionID":"ses_t","sessionTitle":"hello","timestamp":1790000000000}' + "`n")
$w.Flush(); $c.Dispose()
```

**Node.js**

```js
const fs = require("fs");
const fd = fs.openSync("\\\\.\\pipe\\oc-notify", "w");
fs.writeSync(fd, JSON.stringify({ type: "questionAsk", sessionID: "s", sessionTitle: "hi", timestamp: Date.now() }) + "\n");
fs.closeSync(fd);
```

> **注意**：Bun 的 `net.connect` 对 Windows 命名管道不可靠（ECONNREFUSED），请用 `fs.openSync` 方式。

---

## 项目结构

```
oc-notify\
├── oc-notify.slnx                    # 解决方案
├── config\
│   └── oc-notify.default.json        # 默认配置模板
├── scripts\
│   ├── Send-TestNotification.ps1     # 单条管道测试
│   └── Send-StackTest.ps1            # 连发测试（堆叠/挤出）
├── src\
│   ├── OcNotify\                     # C# WPF 气泡程序
│   │   ├── OcNotify.csproj           # net8.0-windows / UseWPF
│   │   ├── App.xaml(.cs)             # 单实例、服务编排、空闲自退
│   │   ├── MainWindow.xaml(.cs)      # 透明容器窗、四角锚点、DPI 定位
│   │   ├── Controls\
│   │   │   └── NotificationCard.xaml(.cs)  # 卡片样式 + 进出动画
│   │   ├── Models\
│   │   │   ├── NotifyConfig.cs       # 配置模型（style/behavior/events）
│   │   │   ├── NotifyMessage.cs      # 管道消息模型
│   │   │   └── NotificationItem.cs   # 运行时通知项
│   │   ├── Services\
│   │   │   ├── PipeServer.cs         # 命名管道服务端
│   │   │   ├── ConfigService.cs      # 配置加载 + 热更新
│   │   │   ├── NotificationManager.cs# 队列/过期/挤出
│   │   │   └── DwmHelper.cs          # DWM P/Invoke（边框抑制等）
│   │   └── Helpers\
│   │       └── CategoryInfo.cs       # 分类色/标签映射（双主题）
│   └── plugin\                       # opencode 插件（TS）
│       ├── notify-bubble.ts          # 主插件
│       └── bun-ffi.d.ts              # bun:ffi 类型声明（编辑器用）
└── .gitignore
```

---

## 开发指南

### 构建与运行

```powershell
# 编译（warn-aserror）
dotnet build -warnaserror

# 本地运行（Debug）
dotnet run --project src\OcNotify

# 发布
dotnet publish src\OcNotify\OcNotify.csproj -c Release `
  -o "$env:USERPROFILE\.config\opencode\assets\OcNotify"
```

### 关键设计约定

1. **单位**：`SystemParameters.WorkArea` 与窗口 `Left/Top/Width/Height` 均为 **DIP**，禁止再乘 DPI（曾因混用导致定位偏移）
2. **窗口**：始终 `AllowsTransparency=true` + `Background=null`，只画卡片，无窗口级 backdrop（避免灰色矩形）
3. **锚点**：间距（8px）加在远离锚点的一侧（上角在下、下角在上），固定边只算 16px 配置边距
4. **插件**：不用 `bun:ffi`、不用 `node:net` 连管道；前台检测与拉起 exe 走 PowerShell / `fs`
5. **挤出**：先从 `Items.RemoveAt(0)` 腾槽位再触发动画，否则 `while` 死循环
6. **busy 时间戳**：只记首次，临近 idle 的重复 busy 不覆盖

### 代码风格

- C#：完整 XML 中文注解（写「为什么」而非「做什么」）；文件 IO/网络必须 try-catch + 重试 3 次
- TS：关键路径 `debugLog`；异常一律 catch 并标记 `ERROR` 级别
- DRY：分类色/标签集中在 `CategoryInfo`；配置读取集中在 `ConfigService` / `loadConfig`

### 修改后的标准流程

```powershell
# 1. 改代码
# 2. 构建
dotnet build -warnaserror

# 3. 部署（按需）
dotnet publish src\OcNotify -c Release -o "$env:USERPROFILE\.config\opencode\assets\OcNotify"
Copy-Item src\plugin\notify-bubble.ts "$env:USERPROFILE\.config\opencode\plugins\" -Force

# 4. 重启 OcNotify.exe（插件变更还需重启 opencode）
Get-Process OcNotify -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Process "$env:USERPROFILE\.config\opencode\assets\OcNotify\OcNotify.exe"
```

---

## 测试与调试

### 调试日志

配置：

```jsonc
"behavior": { "debug": true }
```

| 端 | 日志路径 |
|----|----------|
| 插件 | `%TEMP%\oc-notify-plugin.log` |
| exe 错误 | `%TEMP%\oc-notify-error.log` |

日志格式：`[ISO时间] [INFO|ERROR] 消息`，覆盖：init/配置快照、事件到达、短任务与 error 抑制、前台拦截原因、管道收发成败、权限消抖、全部异常。

`debug` 支持热更，改完配置下次事件即生效。

### 手动管道测试

```powershell
# 单条
.\scripts\Send-TestNotification.ps1 -Type sessionError -Title "错误测试"

# 连发验证堆叠/动画/挤出
.\scripts\Send-StackTest.ps1 -Count 7 -IntervalMs 300
```

### 事件类型参数

`sessionIdle` | `permissionAsk` | `questionAsk` | `sessionError` | `subagentDone`

### 推荐验收清单

- [ ] 单条：从锚点方向滑入 → 5s 渐隐收拢消失
- [ ] 连发 7 条：旧上新下（或按锚点相反），第 6 条起挤出最旧，稳定在 maxVisible
- [ ] 消失动画：先渐隐占位，再收拢，其余平滑移动
- [ ] 点击气泡立即消失
- [ ] 全部消失后窗口隐藏（任务栏无图标、不抢焦点）
- [ ] opencode 前台时完成对话 → **不弹**；切走后完成 → **弹**
- [ ] 改 `theme`/`position`/`durationMs` → 下一条通知即生效
- [ ] 开两个 opencode，关掉一个 → exe 不退；全关 → 约 60s 后 exe 退出
- [ ] 重复启动 exe → 只存活一个进程

---

## 部署与运维

### 标准部署（PowerShell 一键）

```powershell
# 从仓库根执行
$cfg = "$env:USERPROFILE\.config\opencode"
$assets = "$cfg\assets\OcNotify"
$plugins = "$cfg\plugins"

New-Item -ItemType Directory -Force -Path $assets, $plugins | Out-Null

dotnet publish src\OcNotify\OcNotify.csproj -c Release -o $assets
Copy-Item src\plugin\notify-bubble.ts $plugins -Force
Copy-Item config\oc-notify.default.json "$cfg\oc-notify.json" -Force   # 已有配置请勿覆盖
```

### 升级

1. 更新仓库代码
2. 重新 `dotnet publish` + 复制插件
3. 重启 `OcNotify.exe` 与 opencode

### 卸载

```powershell
Get-Process OcNotify -ErrorAction SilentlyContinue | Stop-Process -Force
$cfg = "$env:USERPROFILE\.config\opencode"
Remove-Item "$cfg\plugins\notify-bubble.ts" -Force -ErrorAction SilentlyContinue
Remove-Item "$cfg\assets\OcNotify" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "$cfg\oc-notify.json" -Force -ErrorAction SilentlyContinue
```

### 文件角色速记

```
plugins\notify-bubble.ts     → opencode 自动加载的插件（监听事件、拉起 exe）
assets\OcNotify\OcNotify.exe → 气泡程序本体（单实例、空闲自退）
oc-notify.json               → 全局配置（热更新）
```

---

## 故障排查

| 现象 | 排查 |
|------|------|
| 完全不弹窗 | ① `OcNotify.exe` 是否在跑（任务管理器）② 管道是否存在：`[System.IO.Directory]::GetFiles("\\.\pipe\") \| ? { $_ -like "*oc-notify*" }` ③ 插件是否加载：开 `debug` 看 `%TEMP%\oc-notify-plugin.log` 是否有 `plugin init` ④ 是否因 `onlyWhenInactive` 在前台被拦截（日志 `emit skip ... foreground`） |
| 弹了但内容不对 | 检查 `sessionTitle`；插件日志 `emit [type] title=...` |
| 该弹没弹（非前台） | 开 `debug`：看是 `event` 未到、`skip: short task`、`skip: recent error` 还是 `skip ... disabled` |
| 气泡位置不对 | 确认 `style.position`；多显示器时检查工作区；改完热更后发一条新通知 |
| exe 不自动退出 | `opencode` 进程是否仍在（含未关的 CLI）；调小 `idleCheckIntervalMs`/`idleRetry` 验证 |
| exe 不自动启动 | 插件 `ensureOcNotify` 日志（`spawned` / `exe missing` / `pipe already up`）；确认 `assets\OcNotify\OcNotify.exe` 路径 |
| 修改插件不生效 | 插件仅启动加载 → **必须重启 opencode** |
| TUI 曾崩溃 | 确认插件无 `bun:ffi` 实际调用；用当前 `fs.openSync` 管道方案 |
| 构建报错 | `dotnet build -warnaserror` 看首条错误；确认 .NET 8 SDK |

**快速诊断序列**：

```powershell
# 1. 进程与管道
Get-Process OcNotify, opencode -ErrorAction SilentlyContinue
[System.IO.Directory]::GetFiles("\\.\pipe\") | ? { $_ -like "*oc-notify*" }

# 2. 直发一条（绕过插件）
.\scripts\Send-TestNotification.ps1 -Title "诊断"

# 3. 插件日志
Get-Content "$env:TEMP\oc-notify-plugin.log" -Tail 50
```

若直发能弹 → 问题在插件/配置；直发不弹 → 问题在 exe/管道。

---

## 路线图

- [x] P1 骨架：WPF + 配置 + 命名管道
- [x] P2 视觉：透明窗、四角定位、双主题、毛玻璃观感
- [x] P3 动画：堆叠、进出动画、maxVisible 挤出、间距
- [x] P4 插件：5 类事件、非前台过滤、反误报、自动拉起/空闲自退
- [ ] P5 全链路联调固化
- [ ] P6 打磨：单文件发布、可选托盘图标、README 微调

### 已知可选增强

- `dotnet publish /p:PublishSingleFile=true --self-contained` 收敛为单 exe（体积换便利）
- 托盘图标：手动退出/查看状态（当前空闲自退已覆盖大多数场景）
- exe 侧也按 `debug` 输出诊断日志（配置字段已预留）

---

## 许可

本仓库未声明许可证；如需开源分发请自行补充 LICENSE。
