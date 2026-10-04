using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using OcNotify.Controls;
using OcNotify.Models;
using OcNotify.Services;

namespace OcNotify;

/// <summary>
/// 气泡容器主窗口（全透明分层窗口）：
/// - AllowsTransparency=True：窗口本体不可见（仅卡片有背景），alpha=0 空白区点击穿透桌面
/// - 四角锚点（默认右上）对应两种独立的收拢方向：
///   · top-*：面板贴上边、新卡向下叠加（append）；卡片消失向上收拢（下方卡上移补空隙），上方卡不动
///   · bottom-*：面板贴下边、新卡向上叠加（insert 0）；卡片消失向下收拢（上方卡下移补空隙），下方卡不动
/// - 防闪烁核心：面板恒向固定边对齐 + 窗口高度预留 maxVisible 槽位（RootGrid.MinHeight），
///   气泡活动期间窗口矩形完全不变（不移动、不缩放），进入/收拢全部是纯元素布局动画 → 连续无抖动
/// - DPI：工作区像素 ↔ DIP 按每窗口缩放换算（Left/Top/Width/Height 全部 DIP，不做像素中转）
/// </summary>
public partial class MainWindow : Window
{
    /// <summary>
    /// 卡片间距（DIP）。方向随锚点变化：top 锚点加在卡片下方、bottom 锚点加在上方，
    /// 保证固定边到第一张卡片的距离恒定（仅 EdgeMargin），间距不参与锚点计算。
    /// </summary>
    private const double CardGap = 8.0;

    /// <summary>固定边到工作区边缘的距离（DIP）。</summary>
    private const double EdgeMargin = 16.0;

    /// <summary>
    /// 单卡槽位预留（DIP）：卡片实际高约 62 + 间距 8 ≈ 70，放大到 96 留足字体/阴影余量。
    /// 窗口高度下限 = maxVisible × 该值：保证最满堆叠也放得进窗口，从而气泡活动期间
    /// 窗口高度恒定（高度变化即窗口缩放 → 闪烁）。代价只是窗口空白区变大，
    /// 该区域全透明且点击穿透，无副作用。
    /// </summary>
    private const double MaxCardSlotHeight = 96.0;

    private readonly ConfigService _configService;
    private readonly NotificationManager _manager;

    /// <summary>正在播消失动画的卡片数，防止收拢期间被误判为空。</summary>
    private int _exitingCount;

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

        // 溢出兜底：堆叠超高突破 MinHeight 预留时 SizeToContent 会改变窗口高度，bottom 锚点需保持底边固定
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
        ApplyCardSpacing(card);

        // 使用具名方法而非 lambda，便于在卡片移除时解除订阅，避免闭包持有 card 导致内存泄漏
        void OnExitCompleted(NotificationItem it)
        {
            card.ExitCompleted -= OnExitCompleted;
            card.DismissRequested -= OnDismissRequested;
            card.Cleanup();

            CardsPanel.Children.Remove(card);
            _exitingCount = Math.Max(0, _exitingCount - 1);
            _manager.RemoveItem(it);

            if (CardsPanel.Children.Count == 0 && _exitingCount <= 0)
            {
                HideWindow();
            }

            // 无需重新贴位：收拢动画已把空隙在面板内闭合，且窗口矩形活动期间恒定
        }

        void OnDismissRequested(NotificationItem it) => _manager.Dismiss(it);

        card.ExitCompleted += OnExitCompleted;
        card.DismissRequested += OnDismissRequested;

        // 插入方向决定视觉顺序：
        // - top 锚点：新卡在底部（append），最旧卡在顶部
        // - bottom 锚点：新卡在顶部（insert 0），最旧卡沉底
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

        // 显示前先贴位（常规情况下窗口矩形不变，仅为首显/工作区变化兜底）
        UpdateWindowPlacement();
        ShowWindow();
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
    /// 集合已空且无退出中卡片：隐藏窗口。
    /// </summary>
    private void OnAllDismissed()
    {
        if (_exitingCount <= 0 && CardsPanel.Children.Count == 0)
        {
            HideWindow();
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
    /// 配置热更新：刷新卡片样式并重贴窗口（position/maxVisible 变更即时生效，无需重建窗口）。
    /// </summary>
    /// <param name="cfg">新配置。</param>
    private void OnConfigChanged(NotifyConfig cfg)
    {
        Dispatcher.InvokeAsync(() =>
        {
            RefreshCardStyles(cfg);
            UpdateWindowPlacement();
        });
    }

    /// <summary>
    /// 按锚点方向设置卡片间距：
    /// - top 锚点（左上/右上）：间距加在每张卡片下方
    /// - bottom 锚点（左下/右下）：间距加在每张卡片上方
    /// 固定边一侧保持0 间距，锚点定位只依赖 EdgeMargin，避免间距混入位置计算。
    /// </summary>
    /// <param name="card">目标卡片。</param>
    private void ApplyCardSpacing(NotificationCard card)
    {
        card.Margin = IsBottomAnchor
            ? new Thickness(0, CardGap, 0, 0)
            : new Thickness(0, 0, 0, CardGap);
    }

    /// <summary>
    /// 刷新所有已渲染卡片样式（透明度/圆角/毛玻璃观感/间距方向）。
    /// position 变更时间距方向需同步翻转。
    /// </summary>
    /// <param name="cfg">新配置。</param>
    private void RefreshCardStyles(NotifyConfig cfg)
    {
        foreach (var child in CardsPanel.Children)
        {
            if (child is NotificationCard card && card.Item is not null)
            {
                card.Bind(card.Item, cfg.Style);
                ApplyCardSpacing(card);
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
    /// 溢出兜底：堆叠高度超出 MinHeight 预留（如字体异常变大）时 SizeToContent 会改变窗口高度，
    /// bottom 锚点需同步保持底边固定。常规情况下窗口高度恒定，本回调不会在动画期间触发，
    /// 故同步赋值即可（不延迟、不排队），避免旧实现「动画中途跳位」的割裂感。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnWindowSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!IsBottomAnchor)
        {
            return;
        }

        Top = SystemParameters.WorkArea.Bottom - EdgeMargin - e.NewSize.Height;
    }

    /// <summary>
    /// 窗口定位（核心，仅在首显/新增兜底/配置变更等离散时刻调用）：
    /// - 高度：RootGrid.MinHeight = maxVisible × 槽位预留 → 活动期间窗口高度恒定，杜绝缩放闪烁
    /// - 水平：贴工作区左边（left-*）或右边（right-*）减 EdgeMargin
    /// - 垂直（两种独立逻辑）：
    ///   · top-*：固定上边 = 工作区顶 + EdgeMargin，高度向下扩展；收拢时下方卡上移（向上收拢）
    ///   · bottom-*：固定下边 = 工作区底 - EdgeMargin，Top = 固定边 - 高度；收拢时上方卡下移（向下收拢）
    /// 配合面板向固定边对齐（top 对齐顶、bottom 对齐底），窗口边界变化只影响透明空白区，
    /// 卡片像素级不动 → 本方法常规调用是纯 no-op，不产生任何视觉变化。
    /// </summary>
    private void UpdateWindowPlacement()
    {
        // SystemParameters.WorkArea 在 WPF 中返回 DIP（设备无关单位），
        // Left/Top/Width/Height 也是 DIP，直接运算，不做像素中转（混用单位是定位偏移的根因）
        var workArea = SystemParameters.WorkArea;
        var cfg = _configService.Current;
        var style = cfg.Style;

        var pos = (style.Position ?? "top-right").ToLowerInvariant();
        var isRight = pos.Contains("right", StringComparison.Ordinal);
        var isBottom = pos.Contains("bottom", StringComparison.Ordinal);

        // 面板向固定边对齐：空位只出现在不可见一侧，收拢方向由此决定
        CardsPanel.VerticalAlignment = isBottom ? VerticalAlignment.Bottom : VerticalAlignment.Top;

        // 窗口高度下限：maxVisible 张卡的槽位；上限取工作区高度，防止窗口高到产生负坐标
        var maxVisible = Math.Max(1, cfg.Behavior.MaxVisible);
        RootGrid.MinHeight = Math.Min(maxVisible * MaxCardSlotHeight, workArea.Height);

        // 水平：左/右边距（全 DIP）
        var w = ActualWidth > 0 ? ActualWidth : Width;
        var left = isRight
            ? workArea.Right - w - EdgeMargin
            : workArea.Left + EdgeMargin;

        // 垂直：固定边定位（top 固定上边；bottom 固定下边，按当前高度反推 Top）
        var h = ActualHeight > 0 ? ActualHeight : RootGrid.MinHeight;
        var top = isBottom
            ? workArea.Bottom - EdgeMargin - h
            : workArea.Top + EdgeMargin;

        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        Left = left;
        Top = top;
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
