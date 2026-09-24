using System.IO;
using System.Windows;
using OcNotify.Models;
using OcNotify.Services;

namespace OcNotify;

/// <summary>
/// 应用入口：
/// - 单实例 Mutex（Local 会话作用域）
/// - 启动配置服务、命名管道服务
/// - 创建并持有主窗口（ShutdownMode=OnExplicitShutdown，无通知时窗口隐藏但进程常驻）
/// - 窗口始终为全透明分层窗口，毛玻璃/位置均为热更新，无需重建 HWND
/// </summary>
public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private ConfigService? _configService;
    private PipeServer? _pipeServer;
    private NotificationManager? _manager;
    private MainWindow? _mainWindow;

    /// <summary>
    /// 启动：抢单实例锁 → 初始化服务 → 创建窗口 → 开管道。
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
