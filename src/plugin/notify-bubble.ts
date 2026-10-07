import type { Plugin, Hooks, PluginInput } from "@opencode-ai/plugin";
import type { Event, Session, Permission } from "@opencode-ai/sdk";
import { readFileSync, existsSync, appendFileSync, openSync, writeSync, closeSync } from "node:fs";
import { join } from "node:path";
import { homedir, tmpdir } from "node:os";

/**
 * oc-notify opencode 插件（P4）
 *
 * 职责：
 * 1. 监听 5 类事件（对话完成/权限/提问/错误/子代理完成），按配置 events 开关过滤
 * 2. 非前台检测（behavior.onlyWhenInactive，默认开）：opencode/终端处于前台时不弹窗
 * 3. 经命名管道 \\.\pipe\oc-notify 发送一行 JSON 给 OcNotify.exe
 * 4. 阻止系统休眠（behavior.preventSleep，默认关）：会话忙碌期间下发 type=power 控制消息，
 *    由 exe 的 PowerGuard 申请 Windows 电源请求；会话空闲后自动释放。
 *    本插件只负责"意图"，原生电源调用与租约兜底全在 exe 侧。
 *
 * 线程模型（避免阻塞 opencode 主流程）：
 * - event 钩子同步进入后立即调度后台工作并返回，不做长 await
 * - 权限消抖用 setTimeout，不在钩子里 sleep
 * - 管道写入 fire-and-forget，失败静默
 * - 所有异常 try-catch 吞掉，通知失败绝不影响 opencode
 */

// ---------------------------------------------------------------------------
// 配置
// ---------------------------------------------------------------------------

interface EventsCfg {
  sessionIdle: boolean;
  permissionAsk: boolean;
  questionAsk: boolean;
  sessionError: boolean;
  subagentDone: boolean;
}

interface BehaviorCfg {
  onlyWhenInactive: boolean;
  /** 调试日志开关：开启后 debugLog 写入 %TEMP%\oc-notify-plugin.log */
  debug: boolean;
  /**
   * 阻止 Windows 自动休眠（默认关）。
   * 开启后：存在忙碌会话时向 exe 申请电源请求，屏蔽"无操作 N 分钟后睡眠"；
   * 全部会话空闲后自动恢复。屏幕熄灭与手动休眠不受影响。
   */
  preventSleep: boolean;
}

interface PluginCfg {
  events: EventsCfg;
  behavior: BehaviorCfg;
  /** 项目级配置路径（若存在），随消息带给 exe 做深合并 */
  projectConfigPath?: string;
}

const DEFAULT_EVENTS: EventsCfg = {
  sessionIdle: true,
  permissionAsk: true,
  questionAsk: true,
  sessionError: true,
  subagentDone: true,
};

const DEFAULT_BEHAVIOR: BehaviorCfg = {
  onlyWhenInactive: true,
  debug: false,
  preventSleep: false,
};

/**
 * 剥离 JSONC 的注释（// 与 /* *\/）并移除尾逗号，得到 JSON.parse 可接受的纯 JSON。
 *
 * 必须用状态机逐字符处理，不能用正则：字符串值里可能包含 //（如 "http://..."），
 * 正则会误删引号内的内容导致解析失败。
 * 尾逗号一并移除是因为 C# 端（ConfigService）开启了 AllowTrailingCommas，
 * 两端解析行为必须一致——否则会出现通知正常、而插件侧 events/behavior
 * 静默回退默认值的不一致坑。
 * BOM 一并剥离：C# 端 JsonDocument 能容忍 UTF-8 BOM 而 JSON.parse 不能，
 * 不剥会导致同一文件 exe 能读、插件侧却静默回退默认值。
 */
function stripJsonc(text: string): string {
  // 剥掉 UTF-8 BOM（\uFEFF），否则 JSON.parse 抛 Unexpected token
  if (text.charCodeAt(0) === 0xFEFF) text = text.slice(1);
  let out = "";
  /** out 中最近一个非字符串逗号的位置；-1 表示当前没有待判定的逗号 */
  let lastComma = -1;
  let i = 0;

  while (i < text.length) {
    const c = text[i];

    // 字符串原样拷贝到闭合引号：内部的 //、/* 只是普通字符
    if (c === '"') {
      const start = i++;
      while (i < text.length) {
        if (text[i] === "\\") {
          i += 2;
          continue;
        }
        if (text[i++] === '"') break;
      }
      out += text.slice(start, i);
      continue;
    }

    if (c === "/" && text[i + 1] === "/") {
      // 行注释：保留换行符，维持 JSON.parse 报错时的行号准确
      while (i < text.length && text[i] !== "\n") i++;
      continue;
    }

    if (c === "/" && text[i + 1] === "*") {
      i += 2;
      while (i < text.length && !(text[i] === "*" && text[i + 1] === "/")) {
        if (text[i] === "\n") out += "\n";
        i++;
      }
      i = Math.min(i + 2, text.length);
      continue;
    }

    if (c === ",") {
      lastComma = out.length;
      out += c;
      i++;
      continue;
    }

    if (c === "}" || c === "]") {
      // 逗号与其后的闭括号之间只有空白 → 判定为尾逗号，从输出中移除
      if (lastComma >= 0 && !/[^\s]/.test(out.slice(lastComma + 1))) {
        out = out.slice(0, lastComma) + out.slice(lastComma + 1);
      }
      lastComma = -1;
      out += c;
      i++;
      continue;
    }

    out += c;
    i++;
  }

  return out;
}

function readJson(path: string): Record<string, unknown> | null {
  try {
    if (!existsSync(path)) return null;
    return JSON.parse(stripJsonc(readFileSync(path, "utf8"))) as Record<string, unknown>;
  } catch {
    return null;
  }
}

/** 浅合并一段配置（undefined 字段不覆盖）。 */
function pick<T extends object>(base: T, override: unknown): T {
  if (!override || typeof override !== "object") return base;
  return { ...base, ...(override as Partial<T>) };
}

/**
 * 加载配置：全局 ~/.config/opencode/oc-notify.jsonc + 项目 directory/oc-notify.jsonc。
 * 项目级覆盖全局的同名字段（events/behavior 分段合并）。
 */
function loadConfig(directory: string): PluginCfg {
  const globalPath = join(homedir(), ".config", "opencode", "oc-notify.jsonc");
  const projectPath = join(directory, "oc-notify.jsonc");

  const globalCfg = readJson(globalPath);
  const projectCfg = readJson(projectPath);

  const events = pick(
    pick(DEFAULT_EVENTS, (globalCfg as { events?: unknown } | null)?.events),
    projectCfg?.["events"],
  );
  const behavior = pick(
    pick(DEFAULT_BEHAVIOR, (globalCfg as { behavior?: unknown } | null)?.behavior),
    projectCfg?.["behavior"],
  );

  // 同步 debug 开关（loadConfig 每次调用都会刷新）
  setDebugEnabled(behavior.debug === true);

  return {
    events,
    behavior,
    projectConfigPath: existsSync(projectPath) ? projectPath : undefined,
  };
}

// ---------------------------------------------------------------------------
// 调试日志（behavior.debug 开启后写入；默认关闭，避免无谓磁盘 IO）
// ---------------------------------------------------------------------------

const DEBUG_LOG = join(tmpdir(), "oc-notify-plugin.log");

/**
 * 模块级 debug 开关：loadConfig 后同步刷新。
 * debugLog 是同步函数、可能在 loadConfig 前被调用，故用独立变量而非每次读 cfg。
 */
let debugEnabled = false;

/** 同步 debug 开关（配置加载/刷新时调用）。 */
function setDebugEnabled(on: boolean): void {
  debugEnabled = on;
}

/**
 * 追加一行调试日志；仅 debug=true 时写入，失败静默。
 * 格式：[ISO时间] [级别] 消息
 */
function debugLog(msg: string, level: "INFO" | "WARN" | "ERROR" = "INFO"): void {
  if (!debugEnabled) return;
  try {
    appendFileSync(DEBUG_LOG, `[${new Date().toISOString()}] [${level}] ${msg}\n`);
  } catch {
    /* ignore */
  }
}

// ---------------------------------------------------------------------------
// 非前台检测（Windows：祖先进程链 + 前台窗口 PID）
// 不用 bun:ffi——原生崩溃无法 try-catch，曾导致 TUI 挂掉。
// 前台 PID 用 PowerShell 异步查询（仅 onlyWhenInactive 开启时、发通知前调用一次）。
// ---------------------------------------------------------------------------

/** opencode 及其父进程（shell/终端）的 PID 集合；前台 PID 在集合内 = 用户正在看 opencode。 */
let ancestorPids: Set<number> | null = null;

/**
 * 收集 self + 祖先进程 PID（启动时执行一次，插件 init 可 async）。
 * 失败返回仅含自身 PID 的集合（检测退化为“仅 opencode 自身前台才算 active”）。
 */
async function collectAncestorPids(): Promise<Set<number>> {
  const pids = new Set<number>([process.pid]);
  try {
    // 从当前进程沿 ParentProcessId 向上走；powershell 启动开销仅一次，可接受
    const script = [
      `$current = ${process.pid}`,
      `$guard = 0`,
      `while ($current -gt 0 -and $guard -lt 64) {`,
      `  Write-Output $current`,
      `  $p = Get-CimInstance Win32_Process -Filter "ProcessId = $current" -ErrorAction SilentlyContinue`,
      `  if ($null -eq $p) { break }`,
      `  $current = [int]$p.ParentProcessId`,
      `  $guard++`,
      `}`,
    ].join("\n");

    const proc = Bun.spawn(
      ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
      { stdout: "pipe", stderr: "ignore" },
    );
    const out = await new Response(proc.stdout).text();
    await proc.exited;
    for (const line of out.split(/\r?\n/)) {
      const n = Number.parseInt(line.trim(), 10);
      if (Number.isFinite(n) && n > 0) pids.add(n);
    }
  } catch {
    // 退化：仅自身 PID
  }
  return pids;
}

/**
 * 异步获取前台窗口 PID（PowerShell）；失败返回 0（调用方放行）。
 * 仅 onlyWhenInactive 开启时在发通知前调用，低频可接受 ~200ms 开销。
 */
async function getForegroundPidAsync(): Promise<number> {
  try {
    const script = [
      `Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);'`,
      `$h = [W.N]::GetForegroundWindow()`,
      `$p = 0`,
      `[void][W.N]::GetWindowThreadProcessId($h, [ref]$p)`,
      `Write-Output $p`,
    ].join("; ");

    const proc = Bun.spawn(
      ["powershell", "-NoProfile", "-NonInteractive", "-Command", script],
      { stdout: "pipe", stderr: "ignore" },
    );
    const out = await new Response(proc.stdout).text();
    await proc.exited;
    const pid = Number.parseInt(out.trim(), 10);
    return Number.isFinite(pid) && pid > 0 ? pid : 0;
  } catch (e) {
    debugLog(`getForegroundPidAsync failed: ${String(e)}`, "ERROR");
    return 0;
  }
}

/**
 * 是否允许弹窗（onlyWhenInactive 逻辑，异步）。
 * - 开关关 → 允许
 * - 检测失败 → 允许（宁可多弹不漏报）
 * - 前台 PID 在祖先链内（用户正看着 opencode/终端）→ 拦截
 */
async function shouldNotify(cfg: PluginCfg): Promise<boolean> {
  if (!cfg.behavior.onlyWhenInactive) return true;
  if (!ancestorPids || ancestorPids.size === 0) {
    debugLog("shouldNotify: no ancestors, allow");
    return true;
  }
  const fg = await getForegroundPidAsync();
  if (fg === 0) {
    debugLog("shouldNotify: fg=0, allow");
    return true;
  }
  const allowed = !ancestorPids.has(fg);
  debugLog(`shouldNotify: fg=${fg} inAncestors=${ancestorPids.has(fg)} allow=${allowed}`);
  return allowed;
}

// ---------------------------------------------------------------------------
// 命名管道发送（fire-and-forget）
// ---------------------------------------------------------------------------

const PIPE_PATH = "\\\\.\\pipe\\oc-notify";

/** OcNotify.exe 在 opencode 配置目录 assets/OcNotify 下（不放 plugins，避免被当脚本加载）。 */
const OC_NOTIFY_EXE = join(homedir(), ".config", "opencode", "assets", "OcNotify", "OcNotify.exe");

/**
 * 确保 OcNotify.exe 在运行（首个 CLI 启动时拉起，后续 CLI 因管道已存在而跳过）。
 * exe 自带单实例 Mutex，竞态下多 spawn 一次也只会存活一个。
 * 空闲回收在 exe 侧：无 opencode 进程约 60s（默认 30s×2）后自动退出。
 *
 * 必须经 `cmd /c start "" /b` 跳板启动：Bun/libuv 在 Windows 上把直接
 * 子进程放进 kill-on-close Job Object，直接 spawn 的 exe 会在 opencode
 * 退出瞬间被内核连带杀死，永远活不到自己的 60s 空闲回收（Bun #31603，
 * `detached: true` 无效）。跳板产生的孙进程因 Job 带 SILENT_BREAKAWAY_OK
 * 而脱离 Job，可独立存活，由 exe 自己的空闲回收决定退出时机。
 */
async function ensureOcNotifyRunning(): Promise<void> {
  try {
    if (existsSync(PIPE_PATH)) {
      debugLog("ensureOcNotify: pipe already up, skip spawn");
      return;
    }
    if (!existsSync(OC_NOTIFY_EXE)) {
      debugLog(`ensureOcNotify: exe missing at ${OC_NOTIFY_EXE}`, "WARN");
      return;
    }
    // await cmd 退出：确保 start 已真正拉起 exe 后插件初始化才继续；
    // exe 是长驻进程，由 start 放后台（/b），cmd 立即返回
    const boot = Bun.spawn(["cmd", "/c", "start", "", "/b", OC_NOTIFY_EXE], {
      stdin: "ignore",
      stdout: "ignore",
      stderr: "ignore",
      windowsHide: true,
    });
    await boot.exited;
    debugLog(`ensureOcNotify: spawned via start /b ${OC_NOTIFY_EXE}`);
  } catch (e) {
    debugLog(`ensureOcNotify failed: ${String(e)}`, "ERROR");
  }
}

/**
 * 发送一行 JSON 到 OcNotify 管道。
 * 用 fs 打开命名管道（Windows 上 \\.\pipe\name 可当文件写），
 * 不用 node:net——Bun 的 net.connect 对命名管道不可靠（ECONNREFUSED）。
 * 首个 CLI 刚拉起 exe 时管道可能尚未就绪，失败后最多重试 2 次（间隔 500ms）。
 */
function sendPipe(jsonLine: string, attempt = 0): void {
  debugLog(`sendPipe → ${jsonLine}`);
  let fd: number | null = null;
  try {
    fd = openSync(PIPE_PATH, "w");
    writeSync(fd, jsonLine + "\n");
    debugLog("sendPipe ✓ written");
  } catch (e) {
    debugLog(`sendPipe ✗ attempt=${attempt} ${String(e)}`, "ERROR");
    // 管道未就绪时短暂重试（exe 刚 spawn 的冷启动窗口）
    if (attempt < 2) {
      setTimeout(() => sendPipe(jsonLine, attempt + 1), 500);
    }
  } finally {
    if (fd !== null) {
      try {
        closeSync(fd);
      } catch {
        /* ignore */
      }
    }
  }
}

// ---------------------------------------------------------------------------
// 会话信息缓存（title / parentID）
// ---------------------------------------------------------------------------

interface SessionBrief {
  title: string;
  parentID?: string;
}

const sessionCache = new Map<string, SessionBrief>();

function cacheSession(id: string, brief: SessionBrief): void {
  sessionCache.set(id, brief);
}

function applySession(s: Session): SessionBrief {
  const brief: SessionBrief = {
    title: s.title || "OpenCode",
    parentID: s.parentID,
  };
  cacheSession(s.id, brief);
  return brief;
}

// ---------------------------------------------------------------------------
// 主插件
// ---------------------------------------------------------------------------

export const NotifyBubblePlugin: Plugin = async (input: PluginInput): Promise<Hooks> => {
  const { client, directory } = input;
  debugLog(`plugin init pid=${process.pid} dir=${directory}`);

  // 初始化：配置 + 祖先 PID（均为一次性，且在插件加载阶段，不占事件路径）
  let cfg = loadConfig(directory);
  debugLog(
    `config loaded: onlyWhenInactive=${cfg.behavior.onlyWhenInactive} debug=${cfg.behavior.debug} ` +
      `preventSleep=${cfg.behavior.preventSleep} ` +
      `events=${JSON.stringify(cfg.events)} projectCfg=${cfg.projectConfigPath ?? "(none)"}`,
  );
  ancestorPids = await collectAncestorPids();
  debugLog(`ancestors=[${[...ancestorPids].join(",")}] count=${ancestorPids.size}`);

  // 确保气泡服务在跑：首个 CLI 拉起，后续检测到管道直接跳过
  await ensureOcNotifyRunning();

  // --- 反误报状态 ---
  /** sessionID → 本轮首次 busy 时间（短任务过滤用） */
  const busyAt = new Map<string, number>();
  /**
   * 当前处于 busy 态的 sessionID 集合（busy 时加入，idle 时移除）。
   * GC 只清理不在此集合内的 busyAt 条目——否则跑过 60s 的长任务会被
   * 按时间误判为陈旧数据删掉，收尾前的二次 busy 又被当成"首次 busy"
   * 重新记时间，导致 idle 时 elapsed 近乎 0、短任务过滤误杀（已踩坑）。
   */
  const inflight = new Set<string>();
  /** sessionID → 最近一次 error 时间（error 后 2s 内的 idle 抑制） */
  const errorAt = new Map<string, number>();
  /** sessionID → 权限消抖定时器 */
  const permTimers = new Map<string, ReturnType<typeof setTimeout>>();
  /** 已 dispose */
  let disposed = false;
  /**
   * 上一次已下发给 exe 的「阻止休眠」期望值；null=从未下发过。
   * 只用于去重与"开关被关时补发一次释放"，不是状态源——
   * 真正的状态源是 inflight，见 syncPower 的注释。
   */
  let powerHold: boolean | null = null;

  /** 每次发消息前重读配置（文件很小，失败保留旧配置）。 */
  function refreshConfig(): void {
    try {
      cfg = loadConfig(directory);
    } catch {
      /* keep old */
    }
  }

  /**
   * 下发一条电源控制消息（type=power）。
   * 不走 emit()：这不是提醒，没有气泡，也不该被 events 开关或 onlyWhenInactive 过滤。
   */
  function writePower(hold: boolean): void {
    if (disposed) return;
    debugLog(`writePower → type=power hold=${hold}`);
    sendPipe(
      JSON.stringify({
        type: "power",
        hold,
        timestamp: Date.now(),
        configPath: cfg.projectConfigPath,
      }),
    );
  }

  /**
   * 把「是否阻止系统休眠」同步给 exe。
   *
   * 期望值**每次都从 inflight 现场求值**，而不是从 powerHold 递推——
   * 这是本函数最关键的设计。心跳重发时看到的长任务仍在 inflight 里，
   * 求出来的必然还是 hold=true，绝不会因为"重发"而把还在跑的任务误释放。
   *
   * 调用点正好覆盖 inflight 的全部变更处（外加 gcTimer 心跳）：
   * - session.status=busy：inflight.add 之后 → 请求阻止
   * - session.idle：inflight.delete 之后、所有提前 return 之前 → 请求释放
   * - gcTimer tick：force=true 重发，用于修复丢失的消息（幂等）
   * - dispose：显式释放一次
   *
   * @param force true=忽略去重强制重发（心跳用）
   */
  function syncPower(force = false): void {
    refreshConfig();
    if (cfg.behavior.preventSleep !== true) {
      // 开关关闭：只需在"曾经 hold 过"时补发一次释放，之后彻底静默
      if (powerHold === true) {
        powerHold = false;
        writePower(false);
      }
      return;
    }

    const want = inflight.size > 0;
    if (want === powerHold && !force) return;
    powerHold = want;
    writePower(want);
  }

  /**
   * 组装并发送一条通知（已含事件开关 + 非前台过滤）。
   * 异步：前台检测走 PowerShell；调用方 void 掉，不阻塞事件钩子。
   * type: sessionIdle | permissionAsk | questionAsk | sessionError | subagentDone
   */
  async function emit(type: keyof EventsCfg, sessionID: string, sessionTitle: string): Promise<void> {
    if (disposed) return;
    refreshConfig();
    if (!cfg.events[type]) {
      debugLog(`emit skip [${type}]: event disabled in config`);
      return;
    }
    if (!(await shouldNotify(cfg))) {
      debugLog(`emit skip [${type}]: opencode is foreground (onlyWhenInactive)`);
      return;
    }

    const payload = {
      type,
      sessionID,
      sessionTitle: sessionTitle || "OpenCode",
      timestamp: Date.now(),
      configPath: cfg.projectConfigPath,
    };
    debugLog(`emit [${type}] title="${sessionTitle}" sid=${sessionID}`);
    sendPipe(JSON.stringify(payload));
  }

  /**
   * 拉取会话信息（title + parentID）。带内存缓存；失败回退空 brief。
   * 异步执行，调用方在 async 路径 await。
   */
  async function getSessionBrief(sessionID: string): Promise<SessionBrief> {
    const cached = sessionCache.get(sessionID);
    if (cached) return cached;
    try {
      const res = await client.session.get({ path: { id: sessionID } });
      const data = (res as { data?: Session }).data;
      if (data && typeof data === "object") {
        return applySession(data);
      }
    } catch {
      /* ignore */
    }
    const fallback: SessionBrief = { title: "OpenCode" };
    cacheSession(sessionID, fallback);
    return fallback;
  }

  /**
   * session.idle 处理（后台异步）：
   * - error 后 2s 内抑制（错误通知已发，避免 idle 二次弹）
   * - 观测到 busy 且距 idle < 2s → 短任务跳过（降噪）
   * - parentID 存在 → subagentDone，否则 sessionIdle
   */
  async function onIdle(sessionID: string): Promise<void> {
    try {
      // 先摘掉 busy 态标记（放在所有 return 之前，保证每条退出路径都清理）
      inflight.delete(sessionID);
      // 同步解除休眠阻止：同样必须早于所有 return，否则短路分支会漏掉释放
      syncPower();
      const errTs = errorAt.get(sessionID);
      if (errTs !== undefined && Date.now() - errTs < 2000) {
        debugLog(`onIdle skip: recent session.error (suppress duplicate) sid=${sessionID}`);
        errorAt.delete(sessionID);
        return;
      }

      const busyTs = busyAt.get(sessionID);
      busyAt.delete(sessionID);
      if (busyTs !== undefined && Date.now() - busyTs < 2000) {
        debugLog(
          `onIdle skip: short task elapsed=${Date.now() - busyTs}ms (<2000ms) sid=${sessionID}`,
        );
        return; // 刚 busy 就 idle，视为无实质工作的短任务
      }

      const brief = await getSessionBrief(sessionID);
      const kind = brief.parentID ? "subagentDone" : "sessionIdle";
      debugLog(
        `onIdle → [${kind}] title="${brief.title}" sid=${sessionID}` +
          (busyTs ? ` busyFor=${Date.now() - busyTs}ms` : " busyTs=none"),
      );
      await emit(kind, sessionID, brief.title);
    } catch (e) {
      debugLog(`onIdle error: ${String(e)}`, "ERROR");
    }
  }

  /** session.error：记录时间戳并立即（异步）通知。 */
  async function onError(sessionID: string | undefined): Promise<void> {
    try {
      if (sessionID) {
        errorAt.set(sessionID, Date.now());
        const brief = await getSessionBrief(sessionID);
        debugLog(`onError → [sessionError] title="${brief.title}" sid=${sessionID}`);
        await emit("sessionError", sessionID, brief.title);
      } else {
        debugLog("onError → [sessionError] no sessionID");
        await emit("sessionError", "", "OpenCode");
      }
    } catch (e) {
      debugLog(`onError error: ${String(e)}`, "ERROR");
    }
  }

  /**
   * 权限请求：300ms 消抖（自动批准的请求往往在数百毫秒内被 replied 掉），
   * 用 setTimeout 调度，不在钩子里阻塞等待。
   */
  function schedulePermission(sessionID: string): void {
    if (disposed) return;
    const prev = permTimers.get(sessionID);
    if (prev) {
      clearTimeout(prev);
      debugLog(`permission debounce restart sid=${sessionID}`);
    } else {
      debugLog(`permission debounce start (300ms) sid=${sessionID}`);
    }

    const timer = setTimeout(() => {
      permTimers.delete(sessionID);
      if (disposed) return;
      void (async () => {
        try {
          const brief = await getSessionBrief(sessionID);
          debugLog(`permission debounce fire → title="${brief.title}" sid=${sessionID}`);
          await emit("permissionAsk", sessionID, brief.title);
        } catch (e) {
          debugLog(`permission emit error: ${String(e)}`, "ERROR");
        }
      })();
    }, 300);
    permTimers.set(sessionID, timer);
  }

  /** 问题询问。 */
  async function onQuestion(sessionID: string): Promise<void> {
    try {
      const brief = await getSessionBrief(sessionID);
      debugLog(`onQuestion → title="${brief.title}" sid=${sessionID}`);
      await emit("questionAsk", sessionID, brief.title);
    } catch (e) {
      debugLog(`onQuestion error: ${String(e)}`, "ERROR");
    }
  }

  /**
   * 事件分发：同步 switch，重活全部 void 掉（不阻塞钩子返回）。
   * v1 SDK 事件名为 permission.updated；文档/v2 为 permission.asked，双写兼容。
   * question.asked 在部分版本 Event 联合中缺失，用字符串比较 + 宽松取属性。
   */
  function dispatch(event: Event): void {
    const type = (event as { type?: string }).type ?? "";
    // 仅记录关心的事件，避免日志爆炸
    if (
      type === "session.idle" ||
      type === "session.status" ||
      type === "session.error" ||
      type === "permission.updated" ||
      type === "permission.asked" ||
      type === "question.asked"
    ) {
      const sid =
        (event as { properties?: { sessionID?: string } }).properties?.sessionID ?? "";
      const st =
        type === "session.status"
          ? ` status=${(event as { properties?: { status?: { type?: string } } }).properties?.status?.type ?? "?"}`
          : "";
      debugLog(`event: ${type}${st} sid=${sid}`);
    }

    const props = ((event as { properties?: Record<string, unknown> }).properties ?? {}) as {
      sessionID?: string;
      status?: { type?: string };
      info?: Session;
      id?: string;
      questions?: unknown;
      error?: unknown;
    };

    switch (type) {
      case "session.created":
      case "session.updated": {
        const info = (props as { info?: Session }).info;
        if (info?.id) applySession(info);
        break;
      }

      case "session.status": {
        const st = (props as { status?: { type?: string } }).status;
        if (st?.type === "busy" && props.sessionID) {
          // 标记 busy 态：GC 见到仍在 inflight 的 sid 不会清理其 busyAt，
          // 保证跑过 60s 的长任务不会因 GC 丢掉首次 busy 时间戳（已踩坑）
          inflight.add(props.sessionID);
          // 只记录首次 busy：临近 idle 可能再发一次 busy，
          // 若覆盖会把 elapsed 压到几毫秒，误判为短任务（已踩坑）
          if (!busyAt.has(props.sessionID)) {
            busyAt.set(props.sessionID, Date.now());
          }
          // 请求阻止系统休眠（多会话时任一会话忙碌即保持）
          syncPower();
        }
        break;
      }

      case "session.idle": {
        const sid = props.sessionID;
        if (sid) void onIdle(sid);
        break;
      }

      case "session.error": {
        void onError(props.sessionID);
        break;
      }

      case "permission.updated":
      case "permission.asked": {
        // v1: properties 即 Permission；v2: properties 含 sessionID
        const perm = props as unknown as Partial<Permission> & { sessionID?: string };
        const useSid = perm.sessionID ?? "";
        if (useSid) schedulePermission(useSid);
        break;
      }

      case "question.asked": {
        const sid = props.sessionID ?? "";
        if (sid) void onQuestion(sid);
        break;
      }

      default:
        break;
    }
  }

  // 定期清理过期的 error/busy 时间戳，防 Map 无限增长（低频即可）
  const gcTimer = setInterval(
    () => {
      const now = Date.now();
      for (const [k, t] of errorAt) if (now - t > 60_000) errorAt.delete(k);
      // busyAt 只清"已不在 busy 态"的条目：仍在 inflight 的说明本轮还在跑，
      // 哪怕超过 60s 也不能删，否则长任务收尾会被误判成短任务而漏弹（已踩坑）。
      // 能留到现在说明 idle 事件丢了，属于脏数据，清掉是对的。
      for (const [k, t] of busyAt) {
        if (!inflight.has(k) && now - t > 60_000) busyAt.delete(k);
      }
      if (sessionCache.size > 500) sessionCache.clear();
      // busy 态标记兜底：进程生命周期有限，集合不会涨；异常情况下清空避免泄漏
      if (inflight.size > 500) inflight.clear();
      // 电源心跳（force=true 忽略去重）：修复管道写入失败丢失的消息。
      // 丢失 hold 会让长任务中途被系统休眠，丢失 release 会让机器空耗一晚上，
      // 两个方向都靠这次幂等重发兜底；期望值现场从 inflight 求值，不会误释放长任务。
      syncPower(true);
    },
    60_000,
  );
  // 不阻止进程退出
  if (typeof gcTimer.unref === "function") gcTimer.unref();

  return {
    /**
     * 事件入口：同步 dispatch 后立即返回。
     * 内部 async 工作均 void 掉，避免 opencode 等待插件。
     */
    event: ({ event }: { event: Event }) => {
      try {
        dispatch(event);
      } catch (e) {
        debugLog(`event handler error: ${String(e)}`, "ERROR");
      }
    },

    dispose: async () => {
      debugLog("plugin dispose");
      disposed = true;
      // 退出前显式释放一次休眠阻止（正常路径的显式复位）。
      // 不能走 syncPower：它在 disposed 后不再发消息，而这里必须发出最后一条。
      // 只在确实 hold 过时才发：开关从未开启的用户不该每次退出都白写一条管道消息。
      // 若此前那条 release 已丢失，也不用在这里补——exe 侧 120s 租约会兜住。
      if (powerHold === true) {
        powerHold = false;
        debugLog("dispose: releasing sleep hold");
        sendPipe(JSON.stringify({ type: "power", hold: false, timestamp: Date.now() }));
      }
      clearInterval(gcTimer);
      for (const t of permTimers.values()) clearTimeout(t);
      permTimers.clear();
      errorAt.clear();
      busyAt.clear();
      inflight.clear();
      sessionCache.clear();
    },
  };
};

export default NotifyBubblePlugin;
