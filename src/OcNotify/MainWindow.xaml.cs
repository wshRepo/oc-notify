using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using OcNotify.Controls;
using OcNotify.Models;
using OcNotify.Services;

namespace OcNotify;

/// <summary>
/// 气泡容器主窗口（全透明分层窗口）：
/// - AllowsTransparency=True：窗口本身不可见，仅卡片有背景
/// - 四角锚点（默认右上）：
///   · top-*：最高点固定，新卡向下叠加，最旧卡在顶部消失后其余上移
///   · bottom-*：最低点固定，新卡向上叠加（新卡在顶部），最旧卡在底部消失后其余下移
/// - DPI：工作区像素 ↔ DIP 按每窗口缩放换算
/// </summary>
public partial class MainWindow : Window
{
    private readonly ConfigService _configService;
    private readonly NotificationManager _manager;

    /// <summary>是否已首次定位（避免句柄就绪前布局抖动）。</summary>
    private bool _positioned;

    /// <summary>正在播消失动画的卡片数，防止收拢期间被误判为空。</summary>
    private int _exitingCount;

    /// <summary>bottom 锚点时屏幕底边固定像素位置（DIP），随内容高度反推 Top。</summary>
    private double _fixedEdgeDip;

    /// <summary>
    /// 构造：注入依赖、订阅事件。始终分层透明窗口，无需按毛玻璃模式重建。
    /// </summary>
    /// <param name="configService">配置服务。</param>
    /// <param name="manager">通知管理器。</param>
    public MainWindow(ConfigService configService, NotificationManager manager)
    {
        _configService = configService;
        _manager = manager;

        // AllowsTransparency 必须在 InitializeComponent/句柄创建前保持 True
        AllowsTransparency = true;
        InitializeComponent();

        _manager.CardAdded += OnCardAdded;
        _manager.CardDismissed += OnCardDismissed;
        _manager.AllDismissed += OnAllDismissed;
        _configService.ConfigChanged += OnConfigChanged;

        // bottom 锚点：高度变化时保持底边固定（收拢动画期间 Top 需跟随）
        SizeChanged += OnWindowSizeChanged;

        Visibility = Visibility.Hidden;
    }

    /// <summary>是否当前配置为底部锚点（bottom-left / bottom-right）。</summary>
    private bool IsBottomAnchor =>
        (_configService.Current.Style.Position ?? "top-right")
        .Contains("bottom", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 新消息入队回调（UI 线程）：创建卡片、按锚点插入、播进入动画、贴角。
    /// </summary>
    /// <param name="item">新通知项。</param>
    public void OnCardAdded(NotificationItem item)
    {
        var cfg = _configService.Current;
        var card = new NotificationCard();
        card.Bind(item, cfg.Style);

        card.ExitCompleted += it =>
        {
            CardsPanel.Children.Remove(card);
            _exitingCount = Math.Max(0, _exitingCount - 1);
            _manager.RemoveItem(it);

            if (CardsPanel.Children.Count == 0 && _exitingCount <= 0)
            {
                HideWindow();
                _positioned = false;
            }
            else
            {
                // 收拢后重新贴锚点（top 固定不动；bottom 需按新高度回贴底边）
                UpdateAnchorPosition(animate: true);
            }
        };
        card.DismissRequested += it => _manager.Dismiss(it);

        // 插入方向决定视觉顺序：
        // - top 锚点：新卡在底部（append）
        // - bottom 锚点：新卡在顶部（insert 0），旧卡沉底
        if (IsBottomAnchor)
        {
            CardsPanel.Children.Insert(0, card);
        }
        else
        {
            CardsPanel.Children.Add(card);
        }

        // 进入动画方向：top 锚点新卡在下方→从下滑入；bottom 锚点新卡在上方→从上滑入
        card.PlayEnter(fromTop: IsBottomAnchor);

        ShowWindow();
        UpdateAnchorPosition(animate: true);
    }

    /// <summary>
    /// 管理器要求退出某项：找到对应卡片播消失动画。
    /// </summary>
    /// <param name="item">目标通知项。</param>
    private void OnCardDismissed(NotificationItem item)
    {
        var card = FindCard(item);
        if (card is null)
        {
            _manager.RemoveItem(item);
            return;
        }

        _exitingCount++;
        card.PlayExit();
    }

    /// <summary>
    /// 按通知项查找已渲染卡片。
    /// </summary>
    /// <param name="item">通知项。</param>
    /// <returns>卡片；找不到返回 null。</returns>
    private NotificationCard? FindCard(NotificationItem item)
    {
        foreach (var child in CardsPanel.Children)
        {
            if (child is NotificationCard card && ReferenceEquals(card.Item, item))
            {
                return card;
            }
        }

        return null;
    }

    /// <summary>
    /// 集合已空且无退出中卡片：隐藏窗口并重置锚点。
    /// </summary>
    private void OnAllDismissed()
    {
        if (_exitingCount <= 0 && CardsPanel.Children.Count == 0)
        {
            HideWindow();
            _positioned = false;
        }
    }

    /// <summary>
    /// 句柄就绪：仅去系统边框；不再启用窗口级 backdrop（会刷出灰色矩形）。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        // 确保关闭任何可能的系统 backdrop，边框抑制
        DwmHelper.SetAcrylic(hwnd, false);
        DwmHelper.SuppressBorder(hwnd);
    }

    /// <summary>
    /// 配置热更新：刷新卡片样式并重贴锚点（position 变更即时生效，无需重建窗口）。
    /// </summary>
    /// <param name="cfg">新配置。</param>
    private void OnConfigChanged(NotifyConfig cfg)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RefreshCardStyles(cfg);
            _positioned = false; // 位置变更后强制重新计算固定边
            UpdateAnchorPosition(animate: false);
        });
    }

    /// <summary>
    /// 刷新所有已渲染卡片样式（透明度/圆角/毛玻璃观感）。
    /// </summary>
    /// <param name="cfg">新配置。</param>
    private void RefreshCardStyles(NotifyConfig cfg)
    {
        foreach (var child in CardsPanel.Children)
        {
            if (child is NotificationCard card && card.Item is not null)
            {
                card.Bind(card.Item, cfg.Style);
            }
        }
    }

    /// <summary>
    /// 显示窗口。
    /// </summary>
    private void ShowWindow()
    {
        if (Visibility != Visibility.Visible)
        {
            Visibility = Visibility.Visible;
        }

        Opacity = 1;
    }

    /// <summary>
    /// 隐藏窗口。
    /// </summary>
    private void HideWindow()
    {
        Visibility = Visibility.Hidden;
    }

    /// <summary>
    /// SizeChanged：窗口尺寸变化（含 SizeToContent 与卡片收拢）时重新贴锚点。
    /// top 锚点：Top 不随高度变，但 Left 需保持；bottom 锚点：底边固定，Top 需跟随高度。
    /// 直接在此同步校正，避免依赖单独调用时机。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_positioned || CardsPanel.Children.Count == 0)
        {
            return;
        }

        if (Visibility != Visibility.Visible)
        {
            return;
        }

        // 尺寸变化后立即按当前尺寸重算（不做动画，避免抖动）
        UpdateAnchorPosition(animate: false);
    }

    /// <summary>
    /// 锚点定位（核心）：
    /// - top-*：Top 固定 = 工作区顶 + margin；高度向下增长；消失时 Top 不动 → 下方上移
    /// - bottom-*：底边固定 = 工作区底 - margin；高度向上增长；消失时底边不动 → 上方下移
    /// 仅在新增/退出完成/配置变更时调用。
    /// </summary>
    /// <param name="animate">是否平滑动画窗口水平位移（垂直由锚点规则驱动）。</param>
    private void UpdateAnchorPosition(bool animate)
    {
        if (CardsPanel.Children.Count == 0)
        {
            return;
        }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (CardsPanel.Children.Count == 0)
            {
                return;
            }

            // SystemParameters.WorkArea 在 WPF 中返回 DIP（设备无关单位），
            // Left/Top/Width/Height 也是 DIP，直接运算，不做像素中转（混用单位是定位偏移的根因）
            var workArea = SystemParameters.WorkArea;
            var style = _configService.Current.Style;
            const double margin = 16.0;

            var w = ActualWidth > 0 ? ActualWidth : Width;
            var h = ActualHeight > 0 ? ActualHeight : MinHeight;

            var pos = (style.Position ?? "top-right").ToLowerInvariant();
            var isRight = pos.Contains("right", StringComparison.Ordinal);
            var isBottom = pos.Contains("bottom", StringComparison.Ordinal);

            // 水平：左/右边距（全 DIP）
            var left = isRight
                ? workArea.Right - w - margin
                : workArea.Left + margin;

            double top;
            if (isBottom)
            {
                // 底边固定：记录固定底边（DIP），Top = 底边 - 高度
                _fixedEdgeDip = workArea.Bottom - margin;
                top = _fixedEdgeDip - h;
            }
            else
            {
                // 顶边固定：Top 恒定，高度向下扩展
                top = workArea.Top + margin;
                _fixedEdgeDip = 0;
            }

            // 水平与（top 锚点时）垂直可动画；bottom 锚点垂直交给 SizeChanged 连续校正
            if (animate && _positioned)
            {
                var leftAnim = new DoubleAnimation(Left, left, TimeSpan.FromMilliseconds(280))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                };
                BeginAnimation(LeftProperty, leftAnim);

                if (!isBottom)
                {
                    var topAnim = new DoubleAnimation(Top, top, TimeSpan.FromMilliseconds(280))
                    {
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                    };
                    BeginAnimation(TopProperty, topAnim);
                }
                else
                {
                    BeginAnimation(TopProperty, null);
                    Top = top;
                }
            }
            else
            {
                BeginAnimation(LeftProperty, null);
                BeginAnimation(TopProperty, null);
                Left = left;
                Top = top;
            }

            _positioned = true;
        }), DispatcherPriority.Loaded);
    }

    /// <inheritdoc />
    protected override void OnClosed(EventArgs e)
    {
        _manager.CardAdded -= OnCardAdded;
        _manager.CardDismissed -= OnCardDismissed;
        _manager.AllDismissed -= OnAllDismissed;
        _configService.ConfigChanged -= OnConfigChanged;
        SizeChanged -= OnWindowSizeChanged;
        base.OnClosed(e);
    }
}
