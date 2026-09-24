using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using OcNotify.Models;
using OcNotify.Services;

namespace OcNotify;

/// <summary>
/// 应用入口：
/// - 单实例 Mutex（Local 会话作用域）
/// - 启动配置服务、命名管道服务
/// - 创建并持有主窗口（ShutdownMode=OnExplicitShutdown，无通知时窗口隐藏但进程常驻）
/// - 空闲回收：定时检查是否还有 opencode 进程，全部退出后自动关闭本进程
/// - 窗口始终为全透明分层窗口，毛玻璃/位置均为热更新，无需重建 HWND
/// </summary>
public partial class App : Application
{
    /// <summary>空闲检查默认间隔（毫秒），可被 behavior.idleCheckIntervalMs 覆盖。</summary>
    private const int DefaultIdleCheckIntervalMs = 30_000;

    /// <summary>空闲退出默认重试次数，可被 behavior.idleRetry 覆盖。</summary>
    private const int DefaultIdleRetry = 2;

    private Mutex? _singleInstanceMutex;
    private ConfigService? _configService;
    private PipeServer? _pipeServer;
    private NotificationManager? _manager;
    private MainWindow? _mainWindow;
    private DispatcherTimer? _idleTimer;
    private int _idleMissCount;

    /// <summary>
    /// 启动：抢单实例锁 → 初始化服务 → 创建窗口 → 开管道 → 启动空闲回收。
    /// </summary>
    /// <param name="e">启动事件参数。</param>
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例：同名 Mutex 已存在则直接退出（第二个实例无事可做）
        _singleInstanceMutex = new Mutex(true, @"Local\OcNotify.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Shutdown(0);
            return;
        }

        try
        {
            _configService = new ConfigService();
            _manager = new NotificationManager(Dispatcher, _configService);
            _pipeServer = new PipeServer();

            // 管道消息 → 通知管理器（Manager 内部会切 UI 线程）
            _pipeServer.MessageReceived += msg => _manager?.Add(msg);

            // CardAdded 等 UI 事件由 MainWindow 构造函数自行订阅，避免双重触发
            CreateMainWindow();

            _pipeServer.Start();
            StartIdleReaper();
        }
        catch (Exception ex)
        {
            // 启动失败：记录日志后退出，不留下僵尸进程
            try
            {
                File.AppendAllText(
                    Path.Combine(Path.GetTempPath(), "oc-notify-error.log"),
                    $"[{DateTime.Now:O}] {ex}{Environment.NewLine}");
            }
            catch
            {
                // 日志失败忽略
            }

            Shutdown(1);
        }
    }

    /// <summary>
    /// 空闲回收：周期检查是否还有 opencode 进程；
    /// 连续 idleRetry 次未发现则退出（所有 CLI 已关闭）。
    /// 间隔/次数从配置读取，每次 Tick 取最新值（支持热更新）。
    /// </summary>
    private void StartIdleReaper()
    {
        _idleTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(GetIdleIntervalMs()),
        };
        _idleTimer.Tick += OnIdleCheck;
        _idleTimer.Start();
    }

    /// <summary>当前配置的空闲检查间隔（毫秒），非法值回退默认 30s。</summary>
    /// <returns>间隔毫秒。</returns>
    private int GetIdleIntervalMs()
    {
        var ms = _configService?.Current.Behavior.IdleCheckIntervalMs ?? DefaultIdleCheckIntervalMs;
        return ms >= 1000 ? ms : DefaultIdleCheckIntervalMs;
    }

    /// <summary>当前配置的空闲重试次数，非法值回退默认 2。</summary>
    /// <returns>重试次数。</returns>
    private int GetIdleRetry()
    {
        var n = _configService?.Current.Behavior.IdleRetry ?? DefaultIdleRetry;
        return n >= 1 ? n : DefaultIdleRetry;
    }

    /// <summary>单次空闲检查。</summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnIdleCheck(object? sender, EventArgs e)
    {
        // 支持热更新：每次 Tick 同步间隔
        if (_idleTimer is not null)
        {
            _idleTimer.Interval = TimeSpan.FromMilliseconds(GetIdleIntervalMs());
        }

        if (IsOpencodeRunning())
        {
            _idleMissCount = 0;
            return;
        }

        _idleMissCount++;
        if (_idleMissCount >= GetIdleRetry())
        {
            _idleTimer?.Stop();
            Shutdown(0);
        }
    }

    /// <summary>
    /// 是否仍有 opencode 进程在运行。
    /// 进程名匹配 opencode（含可能的 opencode.exe 宿主）。
    /// </summary>
    /// <returns>true=仍在运行。</returns>
    private static bool IsOpencodeRunning()
    {
        try
        {
            return Process.GetProcessesByName("opencode").Length > 0;
        }
        catch
        {
            // 查询失败时保守认为仍在运行，避免误退出
            return true;
        }
    }

    /// <summary>
    /// 创建主窗口并设为应用主窗口。
    /// </summary>
    private void CreateMainWindow()
    {
        if (_configService is null || _manager is null)
        {
            return;
        }

        _mainWindow = new MainWindow(_configService, _manager);
        MainWindow = _mainWindow;
    }

    /// <inheritdoc />
    protected override void OnExit(ExitEventArgs e)
    {
        if (_idleTimer is not null)
        {
            _idleTimer.Stop();
            _idleTimer = null;
        }

        _pipeServer?.Dispose();
        _manager?.Shutdown();
        _configService?.Dispose();

        if (_singleInstanceMutex is not null)
        {
            try
            {
                _singleInstanceMutex.ReleaseMutex();
            }
            catch
            {
                // 忽略
            }

            _singleInstanceMutex.Dispose();
        }

        base.OnExit(e);
    }
}
