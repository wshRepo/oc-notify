import { Plugin } from "@opencode/plugin/tui";
import type { Cleanup, Context } from "@opencode/plugin/tui/plugin";
import {
  appendFileSync,
  closeSync,
  existsSync,
  mkdirSync,
  openSync,
  readdirSync,
  readFileSync,
  statSync,
  unlinkSync,
  writeSync,
} from "node:fs";
import { execFile, spawn } from "node:child_process";
import { join } from "node:path";
import { homedir, tmpdir } from "node:os";

/**
 * oc-notify opencode2 TUI 插件（与 src/plugin/notify-bubble-v1.ts 平行的独立实现）
 *
 * 为什么是 TUI 插件而不是 Service 插件：
 * - V2 的 server 是全局共享后台服务，进程祖先链不属于任何终端窗口，
 *   onlyWhenInactive 的前台判定（祖先 PID 链）会全部失效；
 *   TUI 插件跑在 CLI 进程里，前台判定语义与 V1 完全一致。
 * - OcNotify.exe 的拉起/生命周期跟随 CLI 更自然。
 *
 * 事件源（V2 运行时实测 + 源码核实，注意与 V1 的差异）：
 * - V2 的 session.idle / session.status 只有类型声明、无运行时发布（issue #55358），
 *   完成信号一律取 session.execution.{succeeded,failed,interrupted}，
 *   一次 busy 周期恰好产出一对 started → 终态事件，与 V1 的 busy→idle 配对语义一一对应。
 * - V1 的 question.asked 在 V2 是 form.created（表单=提问）。
 * - V1 的 session.error 在 V2 是 session.execution.failed（终态，之后不再有 idle）。
 *
 * 线程模型（同 V1）：事件钩子同步进入后立即调度后台工作并返回；管道写入
 * fire-and-forget；所有异常 try-catch 吞掉，通知失败绝不影响 opencode。
 *
 * 仅使用 node: API（不用 Bun.*）：TUI 插件宿主可能是 Bun 或 Node，两者通吃。
 */

// ---------------------------------------------------------------------------
// 配置（与 V1 同源同格式：全局 + 项目级 oc-notify.jsonc）
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
  /** 调试日志开关：开启后 debugLog 写入 %TEMP%\oc-notify-plugin.log（V1/V2 共用，行首 [v2] 区分） */
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
 * 正则会误删引号内的内容导致解析失败。BOM/尾逗号的处理原因见 V1 同名实现。
 */
function stripJsonc(text: string): string {
  // 剥掉 UTF-8 BOM（﻿），否则 JSON.parse 抛 Unexpected token
  if (text.charCodeAt(0) === 0xfeff) text = text.slice(1);
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
// 调试日志（behavior.debug 开启后写入；与 V1 共用同一文件，[v2] 前缀区分）
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
 * 格式：[v2] [ISO时间] [级别] 消息
 */
function debugLog(msg: string, level: "INFO" | "WARN" | "ERROR" = "INFO"): void {
  if (!debugEnabled) return;
  try {
    appendFileSync(DEBUG_LOG, `[v2] [${new Date().toISOString()}] [${level}] ${msg}\n`);
  } catch {
    /* ignore */
  }
}

// ---------------------------------------------------------------------------
// 非前台检测（Windows：祖先进程链 + 前台窗口 PID）
// 不用原生 FFI——原生崩溃无法 try-catch，曾导致 TUI 挂掉（V1 的教训）。
// ---------------------------------------------------------------------------

/** opencode 及其父进程（shell/终端）的 PID 集合；前台 PID 在集合内 = 用户正在看 opencode。 */
let ancestorPids: Set<number> | null = null;

/**
 * 执行一段 PowerShell 并返回 stdout；任何失败返回空串（调用方自行降级）。
 * 统一封装：execFile 带超时，避免 PowerShell 卡死拖住插件。
 */
function runPowerShell(script: string): Promise<string> {
  return new Promise((resolve) => {
    execFile(
      "powershell",
      ["-NoProfile", "-NonInteractive", "-Command", script],
      { windowsHide: true, timeout: 15_000 },
      (error, stdout) => {
        if (error) {
          debugLog(`runPowerShell failed: ${String(error)}`, "ERROR");
          resolve("");
          return;
        }
        resolve(stdout);
      },
    );
  });
}

/**
 * 收集 self + 祖先进程 PID（setup 时执行一次）。
 * 失败返回仅含自身 PID 的集合（检测退化为"仅 opencode 自身前台才算 active"）。
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

    const out = await runPowerShell(script);
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
  const script = [
    `Add-Type -Namespace W -Name N -MemberDefinition '[DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow(); [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);'`,
    `$h = [W.N]::GetForegroundWindow()`,
    `$p = 0`,
    `[void][W.N]::GetWindowThreadProcessId($h, [ref]$p)`,
    `Write-Output $p`,
  ].join("; ");

  const out = await runPowerShell(script);
  const pid = Number.parseInt(out.trim(), 10);
  return Number.isFinite(pid) && pid > 0 ? pid : 0;
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
 * 必须经 `cmd /c start "" /b` 跳板启动：libuv/Bun 在 Windows 上把直接子进程
 * 放进 kill-on-close Job Object，直接 spawn 的 exe 会在 opencode 退出瞬间被
 * 内核连带杀死，永远活不到自己的 60s 空闲回收。跳板产生的孙进程因 Job 带
 * SILENT_BREAKAWAY_OK 而脱离 Job，可独立存活（V1 已踩坑验证，原样保留）。
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
    // await 跳板退出：确保 start 已真正拉起 exe 后插件初始化才继续；
    // exe 是长驻进程，由 start 放后台（/b），cmd 立即返回
    await new Promise<void>((resolve) => {
      const child = spawn("cmd", ["/c", "start", "", "/b", OC_NOTIFY_EXE], {
        stdio: "ignore",
        windowsHide: true,
      });
      const done = (): void => resolve();
      child.on("exit", done);
      child.on("error", done);
      // 兜底：cmd 异常挂起时不让插件初始化卡住
      setTimeout(done, 3000).unref?.();
    });
    debugLog(`ensureOcNotify: spawned via start /b ${OC_NOTIFY_EXE}`);
  } catch (e) {
    debugLog(`ensureOcNotify failed: ${String(e)}`, "ERROR");
  }
}

/**
 * 发送一行 JSON 到 OcNotify 管道。
 * 用 fs 打开命名管道（Windows 上 \\.\pipe\name 可当文件写），
 * 不用 net 模块——对命名管道不可靠（V1 已踩坑：ECONNREFUSED）。
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
// 多开去重（V2 特有：TUI 插件每个 TUI 进程各有一实例，共享服务的事件流会被
// 所有实例同时收到；不去重则两个 TUI 会对同一事件各弹一次）。
//
// 机制：事件的 id 在服务端唯一且各实例收到的完全相同，用 %TEMP% 下的
// 独占创建（wx）认领文件实现跨进程互斥——先创建者发通知，后到者跳过。
// 认领失败（EEXIST）→ 跳过；其他错误 → 放行（宁多勿漏）。
// 认领只在"确将发送"时进行（过滤全通过后），避免被抑制方吃掉认领名额。
// ---------------------------------------------------------------------------

const CLAIM_DIR = join(tmpdir(), "oc-notify-claims");

/** FNV-1a 32 位哈希：把任意 key 压成短文件名，避免 Windows 路径长度/非法字符问题。 */
function claimHash(key: string): string {
  let h = 2166136261;
  for (let i = 0; i < key.length; i++) {
    h ^= key.charCodeAt(i);
    h = Math.imul(h, 16777619);
  }
  return (h >>> 0).toString(16);
}

/**
 * 尝试认领一条通知的发送权。
 * 返回 true=获得发送权；false=其他 TUI 实例已认领（将发/已发同一事件），本次跳过。
 */
function tryClaim(key: string): boolean {
  try {
    mkdirSync(CLAIM_DIR, { recursive: true });
    const fd = openSync(join(CLAIM_DIR, claimHash(key)), "wx");
    writeSync(fd, String(Date.now()));
    closeSync(fd);
    return true;
  } catch (e) {
    const code = (e as { code?: string }).code;
    if (code === "EEXIST") {
      debugLog(`tryClaim: already claimed by another instance key=${key}`);
      return false;
    }
    // 认领机制自身出错时放行：去重是降噪手段，不该反过来吞通知
    debugLog(`tryClaim failed (${code ?? String(e)}), fail-open key=${key}`, "WARN");
    return true;
  }
}

/** 清理 10 分钟前的陈旧认领文件，防止 %TEMP% 无限增长。 */
function gcClaims(): void {
  try {
    const now = Date.now();
    for (const name of readdirSync(CLAIM_DIR)) {
      try {
        const p = join(CLAIM_DIR, name);
        const st = statSync(p);
        if (now - st.mtimeMs > 10 * 60_000) unlinkSync(p);
      } catch {
        /* ignore */
      }
    }
  } catch {
    /* ignore */
  }
}

// ---------------------------------------------------------------------------
// 会话信息（title / parentID）——经 TUI 数据层同步缓存获取
// ---------------------------------------------------------------------------

interface SessionBrief {
  title: string;
  parentID?: string;
}

const sessionCache = new Map<string, SessionBrief>();

function cacheSession(id: string, brief: SessionBrief): void {
  sessionCache.set(id, brief);
}

// ---------------------------------------------------------------------------
// 主插件
// ---------------------------------------------------------------------------

export default Plugin.define({
  id: "oc-notify",
  async setup(ctx: Context): Promise<Cleanup> {
    const directory = ctx.location?.directory ?? ctx.data.location.default().directory;
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
     * 当前处于 busy 态的 sessionID 集合（busy 时加入，终态时移除）。
     * GC 只清理不在此集合内的 busyAt 条目——否则跑过 60s 的长任务会被
     * 按时间误判为陈旧数据删掉，收尾前的二次 busy 又被当成"首次 busy"
     * 重新记时间，导致 idle 时 elapsed 近乎 0、短任务过滤误杀（V1 已踩坑）。
     */
    const inflight = new Set<string>();
    /** sessionID → 最近一次 error 时间（error 后 2s 内的终态抑制） */
    const errorAt = new Map<string, number>();
    /** sessionID → 权限消抖状态（定时器 + 等待回复的 requestID 集合） */
    interface PermPending {
      timer: ReturnType<typeof setTimeout>;
      requestIDs: Set<string>;
    }
    const permTimers = new Map<string, PermPending>();
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
     * 调用点覆盖 inflight 的全部变更处（外加 gcTimer 心跳）：
     * - execution.started：inflight.add 之后 → 请求阻止
     * - 终态（succeeded/failed/interrupted）：inflight.delete 之后、所有提前 return 之前 → 请求释放
     * - gcTimer tick：force=true 重发，用于修复丢失的消息（幂等）
     * - cleanup：显式释放一次
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
     * 组装并发送一条通知（已含事件开关 + 非前台过滤 + 多开去重）。
     * 异步：前台检测走 PowerShell；调用方 void 掉，不阻塞事件钩子。
     * type: sessionIdle | permissionAsk | questionAsk | sessionError | subagentDone
     *
     * @param dedupKey 跨 TUI 实例去重键（事件 id 等），仅在确将发送时认领
     */
    async function emit(
      type: keyof EventsCfg,
      sessionID: string,
      sessionTitle: string,
      dedupKey: string,
    ): Promise<void> {
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
      // 去重认领放在全部过滤之后：被抑制的通知不该占用认领名额，
      // 否则另一实例的合法发送会被误拦
      if (!tryClaim(dedupKey)) return;

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
     * 拉取会话信息（title + parentID）。带内存缓存；缓存未命中时经 TUI 数据层
     * sync 一次再取；失败回退空 brief。异步执行，调用方在 async 路径 await。
     */
    async function getSessionBrief(sessionID: string): Promise<SessionBrief> {
      const cached = sessionCache.get(sessionID);
      if (cached) return cached;
      try {
        let info = ctx.data.session.get(sessionID);
        if (!info) {
          await ctx.data.session.sync(sessionID);
          info = ctx.data.session.get(sessionID);
        }
        if (info) {
          const brief: SessionBrief = { title: info.title || "OpenCode", parentID: info.parentID };
          cacheSession(sessionID, brief);
          return brief;
        }
      } catch {
        /* ignore */
      }
      const fallback: SessionBrief = { title: "OpenCode" };
      cacheSession(sessionID, fallback);
      return fallback;
    }

    /** 释放 busy 态标记并同步防休眠。必须放在所有提前 return 之前，否则短路分支会漏掉释放。 */
    function releaseBusy(sessionID: string): void {
      inflight.delete(sessionID);
      syncPower();
    }

    /**
     * 一轮完成（execution.succeeded / interrupted 非跳过原因）的统一处理（后台异步）：
     * - error 后 2s 内抑制（错误通知已发，避免终态二次弹）
     * - 观测到 busy 且距终态 < 2s → 短任务跳过（降噪）
     * - parentID 存在 → subagentDone，否则 sessionIdle
     */
    async function onIdle(sessionID: string, dedupKey: string): Promise<void> {
      try {
        // 先摘掉 busy 态标记（放在所有 return 之前，保证每条退出路径都清理）
        releaseBusy(sessionID);

        const errTs = errorAt.get(sessionID);
        if (errTs !== undefined && Date.now() - errTs < 2000) {
          debugLog(`onIdle skip: recent execution.failed (suppress duplicate) sid=${sessionID}`);
          errorAt.delete(sessionID);
          return;
        }

        const busyTs = busyAt.get(sessionID);
        busyAt.delete(sessionID);
        if (busyTs !== undefined && Date.now() - busyTs < 2000) {
          debugLog(
            `onIdle skip: short task elapsed=${Date.now() - busyTs}ms (<2000ms) sid=${sessionID}`,
          );
          return; // 刚 busy 就终态，视为无实质工作的短任务
        }

        const brief = await getSessionBrief(sessionID);
        const kind = brief.parentID ? "subagentDone" : "sessionIdle";
        debugLog(
          `onIdle → [${kind}] title="${brief.title}" sid=${sessionID}` +
            (busyTs ? ` busyFor=${Date.now() - busyTs}ms` : " busyTs=none"),
        );
        await emit(kind, sessionID, brief.title, dedupKey);
      } catch (e) {
        debugLog(`onIdle error: ${String(e)}`, "ERROR");
      }
    }

    /**
     * execution.failed：会话错误。与 V1 的关键差异——failed 本身就是终态，
     * 之后不会再有 idle 事件，因此这里必须自己释放 busy 态（V1 是靠后续 idle 释放的），
     * 否则防休眠的 inflight 会永久滞留。
     */
    async function onError(sessionID: string, dedupKey: string): Promise<void> {
      try {
        releaseBusy(sessionID);
        errorAt.set(sessionID, Date.now());
        busyAt.delete(sessionID);
        const brief = await getSessionBrief(sessionID);
        debugLog(`onError → [sessionError] title="${brief.title}" sid=${sessionID}`);
        await emit("sessionError", sessionID, brief.title, dedupKey);
      } catch (e) {
        debugLog(`onError error: ${String(e)}`, "ERROR");
      }
    }

    /**
     * execution.interrupted：打断/终止。
     * - reason=shutdown：进程关停，不通知（无意义且 exe 可能同时在退）
     * - reason=superseded：被新一轮执行顶替，真正完成时新执行会发终态，
     *   这里若弹"对话完成"是误报，静默释放 busy 即可
     * - 其余（user / inactivity / 未来新增值）：按完成路径通知（宁多勿漏）
     */
    async function onInterrupted(
      sessionID: string,
      dedupKey: string,
      reason: string | undefined,
    ): Promise<void> {
      try {
        if (reason === "shutdown" || reason === "superseded") {
          debugLog(`onInterrupted skip: reason=${reason ?? "?"} sid=${sessionID}`);
          releaseBusy(sessionID);
          busyAt.delete(sessionID);
          return;
        }
        await onIdle(sessionID, dedupKey);
      } catch (e) {
        debugLog(`onInterrupted error: ${String(e)}`, "ERROR");
      }
    }

    /**
     * 权限请求：300ms 消抖 + replied 抑制。
     * - 同会话的多个请求合并为一条通知（V1 消抖语义）；
     * - permission.replied 在窗口内到达（自动批准的请求往往数百毫秒内被 replied 掉）
     *   → 移除对应 requestID，全部回复完毕则取消弹窗（落实 README「自动批准不弹」）。
     */
    function schedulePermission(sessionID: string, requestID: string): void {
      if (disposed) return;
      let entry = permTimers.get(sessionID);
      if (entry) {
        clearTimeout(entry.timer);
        entry.requestIDs.add(requestID);
        debugLog(`permission debounce restart sid=${sessionID} req=${requestID}`);
      } else {
        entry = { timer: undefined as never, requestIDs: new Set([requestID]) };
        debugLog(`permission debounce start (300ms) sid=${sessionID} req=${requestID}`);
      }

      entry.timer = setTimeout(() => {
        permTimers.delete(sessionID);
        if (disposed) return;
        void (async () => {
          try {
            const brief = await getSessionBrief(sessionID);
            // 去重键覆盖本轮全部 requestID：两实例的合并集合一致，键才一致
            const key = `perm:${sessionID}:${[...entry.requestIDs].sort().join(",")}`;
            debugLog(`permission debounce fire → title="${brief.title}" sid=${sessionID}`);
            await emit("permissionAsk", sessionID, brief.title, key);
          } catch (e) {
            debugLog(`permission emit error: ${String(e)}`, "ERROR");
          }
        })();
      }, 300);
      permTimers.set(sessionID, entry);
    }

    /** permission.replied：从消抖窗口中移除该请求；窗口内全部回复完毕 → 取消弹窗。 */
    function onPermissionReplied(sessionID: string, requestID: string): void {
      const entry = permTimers.get(sessionID);
      if (!entry) return;
      entry.requestIDs.delete(requestID);
      if (entry.requestIDs.size === 0) {
        clearTimeout(entry.timer);
        permTimers.delete(sessionID);
        debugLog(`permission debounce cancel: replied within window sid=${sessionID} req=${requestID}`);
      }
    }

    /** 表单创建（= V2 的提问）：直接通知，无消抖（与 V1 question.asked 语义一致）。 */
    async function onQuestion(sessionID: string, dedupKey: string): Promise<void> {
      try {
        const brief = await getSessionBrief(sessionID);
        debugLog(`onQuestion → title="${brief.title}" sid=${sessionID}`);
        await emit("questionAsk", sessionID, brief.title, dedupKey);
      } catch (e) {
        debugLog(`onQuestion error: ${String(e)}`, "ERROR");
      }
    }

    /**
     * 事件入口的公共位置过滤：V2 共享服务的事件流对所有 TUI 实例广播，
     * 位置不符的事件不归本实例管（项目级配置/祖先链都只对本目录成立）。
     * 事件缺 location 时放行（宁多勿漏，V1 是严格拒绝——V2 放宽以免漏报）。
     */
    function inOurLocation(ev: { location?: { directory?: string } }): boolean {
      const d = ev.location?.directory;
      return d === undefined || d.toLowerCase() === directory.toLowerCase();
    }

    // --- 事件订阅（V2 事件名 → 语义，映射依据见文件头注释） ---
    /**
     * 事件负载的最小结构类型：只声明本插件实际用到的字段。
     * 真实事件类型（@opencode/client 的 OpenCodeEvent）是它的超集，
     * 回调参数按结构兼容收窄，避免为几个字段引入整个 SDK 类型依赖。
     */
    interface SessionExecEvent {
      id: string;
      location?: { directory?: string };
      data: { sessionID: string; reason?: string; error?: unknown };
    }
    interface PermAskedEvent {
      id: string;
      location?: { directory?: string };
      data: { id: string; sessionID: string };
    }
    interface PermRepliedEvent {
      id: string;
      location?: { directory?: string };
      data: { sessionID: string; requestID: string };
    }
    interface FormEvent {
      id: string;
      location?: { directory?: string };
      data: { form: { sessionID: string } };
    }
    interface RenamedEvent {
      location?: { directory?: string };
      data: { sessionID: string; title: string };
    }

    const unsubscribes: Array<() => void> = [];

    /** execution.started = busy 开始：记首次 busy 时间 + 请求防休眠（多次 join 只记首次，V1 语义） */
    unsubscribes.push(
      ctx.data.on("session.execution.started", (event: SessionExecEvent) => {
        try {
          if (!inOurLocation(event)) return;
          const sid = event.data.sessionID;
          inflight.add(sid);
          if (!busyAt.has(sid)) busyAt.set(sid, Date.now());
          syncPower();
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    /** execution.succeeded = 一轮回复完成 → sessionIdle / subagentDone */
    unsubscribes.push(
      ctx.data.on("session.execution.succeeded", (event: SessionExecEvent) => {
        try {
          if (!inOurLocation(event)) return;
          void onIdle(event.data.sessionID, `exec:${event.id}`);
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    /** execution.interrupted = 打断/终止 → 同完成路径（shutdown/superseded 静默） */
    unsubscribes.push(
      ctx.data.on("session.execution.interrupted", (event: SessionExecEvent) => {
        try {
          if (!inOurLocation(event)) return;
          void onInterrupted(event.data.sessionID, `exec:${event.id}`, event.data.reason);
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    /** execution.failed = 会话错误（终态） */
    unsubscribes.push(
      ctx.data.on("session.execution.failed", (event: SessionExecEvent) => {
        try {
          if (!inOurLocation(event)) return;
          void onError(event.data.sessionID, `exec:${event.id}`);
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    /** permission.asked → 300ms 消抖（replied 抑制见 onPermissionReplied） */
    unsubscribes.push(
      ctx.data.on("permission.asked", (event: PermAskedEvent) => {
        try {
          if (!inOurLocation(event)) return;
          schedulePermission(event.data.sessionID, event.data.id);
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    /** permission.replied → 消抖窗口内回复完毕则取消弹窗 */
    unsubscribes.push(
      ctx.data.on("permission.replied", (event: PermRepliedEvent) => {
        try {
          if (!inOurLocation(event)) return;
          onPermissionReplied(event.data.sessionID, event.data.requestID);
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    /** form.created（= V1 的 question.asked）→ AI 提问 */
    unsubscribes.push(
      ctx.data.on("form.created", (event: FormEvent) => {
        try {
          if (!inOurLocation(event)) return;
          const sid = event.data.form.sessionID;
          if (!sid) return;
          void onQuestion(sid, `form:${event.id}`);
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    /** session.renamed → 刷新标题缓存（标题会随后续生成而变化） */
    unsubscribes.push(
      ctx.data.on("session.renamed", (event: RenamedEvent) => {
        try {
          if (!inOurLocation(event)) return;
          const cached = sessionCache.get(event.data.sessionID);
          if (cached) cached.title = event.data.title || cached.title;
        } catch (e) {
          debugLog(`event handler error: ${String(e)}`, "ERROR");
        }
      }),
    );

    // 定期清理过期状态，防 Map 无限增长（低频即可）
    const gcTimer = setInterval(
      () => {
        try {
          const now = Date.now();
          for (const [k, t] of errorAt) if (now - t > 60_000) errorAt.delete(k);
          // busyAt 只清"已不在 busy 态"的条目：仍在 inflight 的说明本轮还在跑，
          // 哪怕超过 60s 也不能删，否则长任务收尾会被误判成短任务而漏弹（V1 已踩坑）。
          for (const [k, t] of busyAt) {
            if (!inflight.has(k) && now - t > 60_000) busyAt.delete(k);
          }
          if (sessionCache.size > 500) sessionCache.clear();
          // busy 态标记兜底：进程生命周期有限，集合不会涨；异常情况下清空避免泄漏
          if (inflight.size > 500) inflight.clear();
          // 权限消抖兜底：正常情况定时器必触发自清，异常残留超 10 分钟强制清掉
          for (const [k, entry] of permTimers) {
            if (entry.requestIDs.size === 0) permTimers.delete(k);
          }
          gcClaims();
          // 电源心跳（force=true 忽略去重）：修复管道写入失败丢失的消息。
          // 丢失 hold 会让长任务中途被系统休眠，丢失 release 会让机器空耗一晚上，
          // 两个方向都靠这次幂等重发兜底；期望值现场从 inflight 求值，不会误释放长任务。
          syncPower(true);
        } catch (e) {
          debugLog(`gcTimer error: ${String(e)}`, "ERROR");
        }
      },
      60_000,
    );
    // 不阻止进程退出
    gcTimer.unref?.();

    debugLog("plugin setup done");

    return async () => {
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
      for (const entry of permTimers.values()) clearTimeout(entry.timer);
      permTimers.clear();
      errorAt.clear();
      busyAt.clear();
      inflight.clear();
      sessionCache.clear();
      for (const un of unsubscribes) {
        try {
          un();
        } catch {
          /* ignore */
        }
      }
    };
  },
});
