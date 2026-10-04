using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using OcNotify.Helpers;
using OcNotify.Models;

namespace OcNotify.Services;

/// <summary>
/// 通知队列与生命周期管理（UI 线程亲和）。
/// 职责：新消息入队、过期计时、maxVisible 挤出、点击/超时 dismiss 触发。
/// 卡片的实际创建/销毁动画由 MainWindow 订阅事件驱动（职责分离）。
/// </summary>
public sealed class NotificationManager
{
    /// <summary>当前可见卡片集合（旧在上、新在下）。</summary>
    public ObservableCollection<NotificationItem> Items { get; } = new();

    /// <summary>新卡片已入队（MainWindow 据此创建卡片 UI）。</summary>
    public event Action<NotificationItem>? CardAdded;

    /// <summary>请求对某卡片播消失动画（超时/点击/超限）。</summary>
    public event Action<NotificationItem>? CardDismissed;

    /// <summary>集合清空（全部消失），主窗口隐藏并重置锚点。</summary>
    public event Action? AllDismissed;

    private readonly Dispatcher _dispatcher;
    private readonly ConfigService _configService;

    /// <summary>每条通知的过期取消源。</summary>
    private readonly Dictionary<NotificationItem, CancellationTokenSource> _expiryCts = new();

    /// <summary>
    /// 构造。
    /// </summary>
    /// <param name="dispatcher">UI Dispatcher。</param>
    /// <param name="configService">配置服务。</param>
    public NotificationManager(Dispatcher dispatcher, ConfigService configService)
    {
        _dispatcher = dispatcher;
        _configService = configService;
    }

    /// <summary>
    /// 添加一条通知（自动切 UI 线程）。
    /// </summary>
    /// <param name="message">管道消息。</param>
    public void Add(NotifyMessage message)
    {
        _dispatcher.InvokeAsync(() => AddInternal(message));
    }

    /// <summary>UI 线程内部入队。</summary>
    /// <param name="message">管道消息。</param>
    private void AddInternal(NotifyMessage message)
    {
        // 兜底热更新：文件修改时间变化时重载（watcher 可能丢事件）
        _configService.EnsureFresh();

        var cfg = _configService.Current;

        // 消息带项目级配置路径时合并重载（样式/行为项目级优先）
        if (!string.IsNullOrWhiteSpace(message.ConfigPath))
        {
            cfg = _configService.Reload(message.ConfigPath);
        }

        var meta = CategoryInfo.Get(message.Type, cfg.Style.Language, cfg.Style.AccentColor, cfg.Style.IsDarkTheme);

        var item = new NotificationItem
        {
            Type = message.Type,
            SessionId = message.SessionId,
            SessionTitle = string.IsNullOrWhiteSpace(message.SessionTitle) ? "OpenCode" : message.SessionTitle,
            Label = meta.Label,
            CategoryColor = meta.ColorHex,
            CreatedAtMs = message.Timestamp > 0 ? message.Timestamp : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            // 粘性模式强制点击关闭：sticky 开启时 clickToDismiss 配置被忽略
            ClickToDismiss = cfg.Behavior.ClickToDismiss || cfg.Behavior.Sticky,
        };

        // 超限挤出：立即从 Items 移除最旧以腾出槽位（动画是异步的，
        // 若等动画完成再移除会导致 while 死循环），再触发其卡片消失动画。
        // 注意：挤出的 item 已不在 Items，ExitCompleted → RemoveItem 为 no-op，
        // 窗口隐藏检查改为 MainWindow 侧基于 CardsPanel.Children.Count。
        var maxVisible = Math.Max(1, cfg.Behavior.MaxVisible);
        while (Items.Count >= maxVisible)
        {
            var oldest = Items[0];
            Items.RemoveAt(0);
            StartDismissCore(oldest);
        }

        Items.Add(item);

        // 粘性模式不启动过期计时：气泡常驻，只能被点击或 maxVisible 挤出关闭，
        // 此时 durationMs 配置被忽略（StartDismissCore 对无计时器项用 TryGetValue，安全）
        if (!cfg.Behavior.Sticky)
        {
            StartExpiryTimer(item, cfg.Behavior.DurationMs);
        }

        CardAdded?.Invoke(item);
    }

    /// <summary>
    /// 启动过期计时器；到期触发消失。
    /// 使用 WeakReference 避免 Task 闭包强引用 item 导致内存泄漏。
    /// </summary>
    /// <param name="item">通知项。</param>
    /// <param name="durationMs">时长。</param>
    private void StartExpiryTimer(NotificationItem item, int durationMs)
    {
        if (_expiryCts.TryGetValue(item, out var old))
        {
            old.Cancel();
            old.Dispose();
        }

        var cts = new CancellationTokenSource();
        _expiryCts[item] = cts;
        var token = cts.Token;

        // 弱引用：Task 完成后不阻止 item 被 GC 回收
        var weakItem = new WeakReference<NotificationItem>(item);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(Math.Max(0, durationMs), token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            // 若 item 已被 GC 回收，则跳过
            if (!weakItem.TryGetTarget(out var target))
            {
                return;
            }

            await _dispatcher.InvokeAsync(() => TriggerDismiss(target));
        }, token);
    }

    /// <summary>
    /// 对外 dismiss 入口（点击）：item 保留在 Items 中，动画完成后再 RemoveItem（保证 AllDismissed 时序）。
    /// </summary>
    /// <param name="item">目标项。</param>
    public void Dismiss(NotificationItem item)
    {
        _dispatcher.InvokeAsync(() => TriggerDismiss(item));
    }

    /// <summary>
    /// 标准消失（超时/点击）：item 仍在 Items，防重入后发 CardDismissed；
    /// 动画完成由 MainWindow 调 RemoveItem 清占位并检查 AllDismissed。
    /// </summary>
    /// <param name="item">目标项。</param>
    private void TriggerDismiss(NotificationItem item)
    {
        if (item.IsExiting)
        {
            return;
        }

        StartDismissCore(item);
    }

    /// <summary>
    /// 核心 dismiss：取消过期计时、标记 IsExiting、发 CardDismissed 让 MainWindow 播动画。
    /// 不负责从 Items 移除（超时/点击由 RemoveItem 收尾；挤出由调用方预先移除）。
    /// </summary>
    /// <param name="item">目标项。</param>
    private void StartDismissCore(NotificationItem item)
    {
        if (item.IsExiting)
        {
            return;
        }

        if (_expiryCts.TryGetValue(item, out var cts))
        {
            _expiryCts.Remove(item);
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // 忽略
            }
        }

        item.IsExiting = true;
        CardDismissed?.Invoke(item);
    }

    /// <summary>
    /// 从集合移除已完成动画的项（超时/点击路径）；
    /// 若集合已空则发 AllDismissed。
    /// 挤出路径的 item 已提前移除，此处 Remove 返回 false，不影响逻辑——
    /// 窗口隐藏的最终判断在 MainWindow：CardsPanel 无子且无退出中卡片。
    /// </summary>
    /// <param name="item">完成项。</param>
    public void RemoveItem(NotificationItem item)
    {
        _dispatcher.InvokeAsync(() =>
        {
            Items.Remove(item);
            if (Items.Count == 0)
            {
                AllDismissed?.Invoke();
            }
        });
    }

    /// <summary>清理所有计时器。</summary>
    public void Shutdown()
    {
        foreach (var cts in _expiryCts.Values)
        {
            try
            {
                cts.Cancel();
                cts.Dispose();
            }
            catch
            {
                // 忽略
            }
        }

        _expiryCts.Clear();
    }
}
