using System.Runtime.InteropServices;
using OcNotify.Helpers;
using OcNotify.Models;

namespace OcNotify.Services;

/// <summary>
/// 系统电源请求守卫：把"期望阻止 Windows 自动休眠"翻译成 Win32 电源请求。
///
/// 原理：
/// SetThreadExecutionState 是**线程级** API，系统按线程计数；
/// 只要还有线程持有 ES_SYSTEM_REQUIRED 就不进入自动睡眠。
/// 因此本类内部常驻一条专用线程，持有人与清除人必须是同一条线程。
///
/// 为什么用 ES_CONTINUOUS | ES_SYSTEM_REQUIRED 而不是别的方式：
/// - ES_CONTINUOUS 让状态持续有效，不需要周期性重复调用（重复调用反而制造节流抖动）；
/// - ES_SYSTEM_REQUIRED 只阻止**自动**休眠，屏幕照常熄灭（关屏由 ES_DISPLAY_REQUIRED 控制），
///   手动休眠/关机也不受影响——用户始终保有最终控制权；
/// - 不碰 powercfg 电源计划，全局设置零污染。
///
/// 双重安全保证（都要求"绝不能出现卡住不休眠"）：
/// 1. 进程死亡时内核自动回收该进程持有的所有电源请求，崩溃/强杀不会残留；
/// 2. 租约：插件需每 LeaseMs 内至少下发一次心跳，否则到点自动释放。
///    这条不依赖任何进程状态，覆盖 opencode 崩溃 / 插件 dispose / 管道写失败等场景。
///
/// 配置否决：即使插件仍在请求，behavior.preventSleep 被改为 false 时也会立即释放
/// （由 ConfigService.ConfigChanged 驱动），构成对插件侧的独立校验。
/// </summary>
public sealed class PowerGuard : IDisposable
{
    /// <summary>管道消息 type 值：电源控制指令（区别于五类提醒事件）。</summary>
    public const string MessageType = "power";

    /// <summary>
    /// 租约时长（毫秒）。插件心跳 60s 一次，此处留 2 倍余量防止机器繁忙时误释放。
    /// 上界意义：opencode 异常退出后最多 LeaseMs 就会恢复自动休眠。
    /// </summary>
    private const long LeaseMs = 120_000;

    /// <summary>
    /// 循环轮询间隔（毫秒）。仅用于让"租约到期"能被及时发现；
    /// 后台线程 5s 醒一次做两次字段比较，开销可忽略，换取逻辑简单无重试路径。
    /// </summary>
    private const int PollMs = 5_000;

    /// <summary>Win32 电源请求标志位（EXECUTION_STATE）。</summary>
    [Flags]
    private enum ExecutionState : uint
    {
        /// <summary>重置系统空闲计时器，阻止自动进入睡眠/休眠。返回值为 0 表示失败。</summary>
        SystemRequired = 0x00000001,

        /// <summary>让上一个标志持续有效，直到下一次带 ES_CONTINUOUS 的调用清除它。</summary>
        Continuous = 0x80000000,
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private readonly ConfigService _configService;
    private readonly CancellationTokenSource _cts = new();

    /// <summary>
    /// 唤醒信号量（计数型，maxCount 放开）。
    /// 刻意不用 ManualResetEventSlim：它是**置位锁存**的，Set() 之后不会自动复位，
    /// Wait() 会立刻持续返回，专用线程直接退化成 100% CPU 空转。
    /// 计数型每次 Wait 消费一个计数、每次 Release 补一个，语义正好是"唤醒一轮求值"。
    /// maxCount 放开是为了 Release 永不抛 SemaphoreFullException；
    /// 重复唤醒最坏只是多做几次"期望未变"的空转求值，无副作用。
    /// </summary>
    private readonly SemaphoreSlim _wake = new(0);

    private readonly Thread _thread;

    /// <summary>插件意图：是否希望阻止休眠（来自 hold 字段）。</summary>
    private bool _desired;

    /// <summary>最近一次收到 power 消息的时刻（Environment.TickCount64，跨线程用 Interlocked 读写）。</summary>
    private long _lastSignalMs;

    /// <summary>专用线程上已实际应用的请求状态；与"期望"不一致才调 Win32 API。</summary>
    private bool _applied;

    /// <summary>已置位则不再求值，防止 P/Invoke 失败时疯狂重试刷日志。</summary>
    private bool _nativeBroken;

    /// <summary>是否已释放。</summary>
    private bool _disposed;

    /// <summary>
    /// 构造：订阅配置热更并启动专用线程。
    /// </summary>
    /// <param name="configService">配置服务（提供 behavior.preventSleep 与热更事件）。</param>
    public PowerGuard(ConfigService configService)
    {
        _configService = configService;

        // 开关被改成 false 时 100ms 内释放，不等插件心跳
        _configService.ConfigChanged += OnConfigChanged;

        // 必须用 lambda 捕获令牌：Thread 构造函数有 ThreadStart/ParameterizedThreadStart 两个重载，
        // 直接传带 CancellationToken 参数的方法组会歧义到 ParameterizedThreadStart 而编译失败
        _thread = new Thread(() => Run(_cts.Token))
        {
            IsBackground = true,
            Name = "OcNotify.PowerGuard",
        };
        _thread.Start();
    }

    /// <summary>
    /// 处理一条 type=power 的管道消息：记录心跳时间与意图，然后唤醒专用线程重新求值。
    /// 与 NotificationManager.AddInternal 保持一致：带项目级配置路径时先重载，保证项目级 preventSleep 生效。
    /// </summary>
    /// <param name="message">管道消息。</param>
    public void Handle(NotifyMessage message)
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Exchange(ref _lastSignalMs, Environment.TickCount64);
        _desired = message.Hold;

        if (!string.IsNullOrWhiteSpace(message.ConfigPath))
        {
            _configService.Reload(message.ConfigPath);
        }

        _wake.Release();
    }

    /// <summary>配置热更回调：仅唤醒线程重新求值（求值逻辑统一在 Run 里，不在此处重复）。</summary>
    /// <param name="config">新配置（未使用，保留签名以便将来扩展）。</param>
    private void OnConfigChanged(NotifyConfig config)
    {
        _wake.Release();
    }

    /// <summary>
    /// 专用线程主体：持续把"期望状态"收敛到 Win32 电源请求。
    /// 期望 = 插件意图 && 租约未过期 && 配置开关开启（三者与，缺一即释放）。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    private void Run(CancellationToken ct)
    {
        var debug = false;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var cfg = _configService.Current;
                debug = cfg.Behavior.Debug;

                var leaseValid =
                    _desired &&
                    Environment.TickCount64 - Interlocked.Read(ref _lastSignalMs) < LeaseMs;
                var want = leaseValid && cfg.Behavior.PreventSleep;

                // 只有期望与实际不一致才调 Win32：心跳重发 hold 时这里是 no-op，零 API 调用零抖动
                if (want != _applied)
                {
                    _applied = Apply(want, debug);
                }

                _wake.Wait(PollMs, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 正常退出
        }
        catch (Exception ex)
        {
            DiagLog.Write($"[ERROR] PowerGuard loop crashed: {ex}");
        }
        finally
        {
            // 线程退出前无条件复位。进程死亡时内核也会兜底释放，
            // 这里显式复位是为了让正常退出路径（Dispose）不留残余请求。
            if (!_nativeBroken)
            {
                SetRequest(ExecutionState.Continuous);
            }
        }
    }

    /// <summary>
    /// 应用一次电源请求并更新本地状态。
    /// </summary>
    /// <param name="want">期望阻止休眠与否。</param>
    /// <param name="debug">是否写调试日志。</param>
    /// <returns>实际成功应用的状态（P/Invoke 失败时返回 false，避免刷日志重试）。</returns>
    private bool Apply(bool want, bool debug)
    {
        var flags = want
            ? ExecutionState.Continuous | ExecutionState.SystemRequired
            : ExecutionState.Continuous;

        if (SetRequest(flags))
        {
            DiagLog.WriteIfDebug(debug, want
                ? "[INFO] PowerGuard: sleep blocked (ES_SYSTEM_REQUIRED)"
                : "[INFO] PowerGuard: sleep released");
            return want;
        }

        DiagLog.Write($"[ERROR] PowerGuard: SetThreadExecutionState failed, " +
                      $"lastError={Marshal.GetLastWin32Error()} want={want}");
        return false;
    }

    /// <summary>
    /// 调用 Win32 设置电源请求；失败返回 false。
    /// 返回 0 即失败（MSDN：返回值 NULL 表示失败），不能只靠 Marshal 错误码判断。
    /// </summary>
    /// <param name="flags">电源请求标志位。</param>
    /// <returns>true=调用成功。</returns>
    private bool SetRequest(ExecutionState flags)
    {
        if (_nativeBroken)
        {
            return false;
        }

        try
        {
            if (SetThreadExecutionState(flags) != 0)
            {
                return true;
            }
        }
        catch (DllNotFoundException)
        {
            // 理论上 kernel32 必然存在；真发生了就永久放弃，绝不在每次心跳上重试
            _nativeBroken = true;
            DiagLog.Write("[ERROR] PowerGuard: SetThreadExecutionState unavailable (kernel32?)");
        }
        catch (EntryPointNotFoundException)
        {
            _nativeBroken = true;
            DiagLog.Write("[ERROR] PowerGuard: SetThreadExecutionState entrypoint missing");
        }

        return false;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _configService.ConfigChanged -= OnConfigChanged;

        _cts.Cancel();
        _wake.Release();
        // 等专用线程走完 finally 里的复位再收工；超时则放弃（进程死亡同样会释放）
        _thread.Join(2000);

        _cts.Dispose();
        _wake.Dispose();
    }
}
