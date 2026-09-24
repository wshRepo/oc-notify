using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using OcNotify.Helpers;
using OcNotify.Models;

namespace OcNotify.Controls;

/// <summary>
/// 单个气泡卡片：负责绑定 <see cref="NotificationItem"/>、播放进入/消失动画、
/// 处理点击关闭。动画完成后通过事件通知上层从集合移除。
/// </summary>
public partial class NotificationCard : UserControl
{
    /// <summary>消失动画（渐隐 + 收拢）完成事件，参数为对应的通知项。</summary>
    public event Action<NotificationItem>? ExitCompleted;

    /// <summary>点击关闭请求（上层决定是否允许并执行 Dismiss）。</summary>
    public event Action<NotificationItem>? DismissRequested;

    /// <summary>当前绑定的通知项。</summary>
    public NotificationItem? Item { get; private set; }

    private bool _enterPlayed;
    private bool _exitStarted;

    /// <summary>缓存退出动画的 Storyboard，完成后显式清理避免泄漏。</summary>
    private Storyboard? _exitStoryboard;

    /// <summary>
    /// 构造：初始化 XAML。
    /// </summary>
    public NotificationCard()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 绑定数据并应用样式：背景/文字/描边/分类色均按主题（light/dark）与配置切换。
    /// 窗口本身始终全透明，样式只作用于卡片（避免窗口级 backdrop 的可见矩形）。
    /// </summary>
    /// <param name="item">通知项。</param>
    /// <param name="style">当前样式配置（含 theme/opacity/glassEffect）。</param>
    public void Bind(NotificationItem item, StyleConfig style)
    {
        Item = item;

        TitleText.Text = item.SessionTitle;

        var dark = style.IsDarkTheme;

        // 分类元数据按当前主题+语言重取（主题/语言热切换后 item 中的快照可能过期）
        var meta = CategoryInfo.Get(item.Type, style.Language, style.AccentColor, dark);
        item.CategoryColor = meta.ColorHex;
        item.Label = meta.Label;
        LabelText.Text = item.Label;
        var color = CategoryInfo.ParseColor(meta.ColorHex);
        AccentBar.Background = new SolidColorBrush(color);
        LabelText.Foreground = new SolidColorBrush(color);

        // 背景：opacity 直接作为 alpha（1.0=不透明）；glassEffect 只切底色风格
        string baseHex;
        string borderHex;
        if (dark)
        {
            baseHex = style.GlassEffect ? "#1C1C22" : "#141418";
            borderHex = style.GlassEffect ? "#38FFFFFF" : "#22FFFFFF";
            TitleText.Foreground = new SolidColorBrush(Color.FromRgb(0xF5, 0xF5, 0xF5));
        }
        else
        {
            baseHex = style.GlassEffect ? "#FFFFFF" : "#F4F4F7";
            borderHex = style.GlassEffect ? "#22000000" : "#14000000";
            TitleText.Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1B));
        }

        var alpha = Math.Clamp(style.Opacity, 0.0, 1.0);
        RootBorder.Background = CategoryInfo.BrushWithAlpha(baseHex, alpha);
        RootBorder.BorderBrush = CategoryInfo.BrushWithAlpha(borderHex, 1.0);

        RootBorder.CornerRadius = new CornerRadius(style.CornerRadius);
        AccentBar.CornerRadius = new CornerRadius(2);
    }

    /// <summary>
    /// 播放进入动画 + 淡入。只播一次。
    /// top 锚点：新卡在堆叠底部 → 从下方滑入（Y+24→0）。
    /// bottom 锚点：新卡在堆叠顶部 → 从上方滑入（Y-24→0）。
    /// </summary>
    /// <param name="fromTop">true=从上方滑入（bottom 锚点用）；false=从下方滑入。</param>
    public void PlayEnter(bool fromTop = false)
    {
        if (_enterPlayed)
        {
            return;
        }

        _enterPlayed = true;

        var startOffset = fromTop ? -24.0 : 24.0;

        Opacity = 0;
        SlideTransform.Y = startOffset;

        var opacityAnim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var slideAnim = new DoubleAnimation(startOffset, 0, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };

        // 使用 BeginAnimation 而非 Storyboard：Storyboard 的 FillBehavior 默认 Stop
        // 会在动画完成后恢复原值，与 SizeToContent 窗口高度计算冲突导致截断
        BeginAnimation(OpacityProperty, opacityAnim);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slideAnim);
    }

    /// <summary>
    /// 播放消失动画：先渐隐（保持占位）→ 再收拢高度（驱动下方卡片上移）。
    /// 完成后触发 <see cref="ExitCompleted"/>。
    /// </summary>
    public void PlayExit()
    {
        if (_exitStarted)
        {
            return;
        }

        _exitStarted = true;
        IsHitTestVisible = false;

        // 阶段 1：渐隐 300ms（保持占位，此时下方不动）
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn },
            FillBehavior = FillBehavior.Stop,
        };

        fade.Completed += (_, _) =>
        {
            Opacity = 0;

            // 阶段 2：收拢高度 300ms（Margin+MaxHeight 同步动画，StackPanel 重排）
            var targetHeight = ActualHeight > 0 ? ActualHeight : 72;
            MaxHeight = targetHeight;

            var collapseH = new DoubleAnimation(targetHeight, 0, TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop,
            };

            // Margin 收拢到 0，消除残留间隙
            var collapseM = new ThicknessAnimation(
                Margin,
                new Thickness(0),
                TimeSpan.FromMilliseconds(300))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
                FillBehavior = FillBehavior.Stop,
            };

            Storyboard.SetTarget(collapseH, this);
            Storyboard.SetTargetProperty(collapseH, new PropertyPath(MaxHeightProperty));

            _exitStoryboard = new Storyboard();
            _exitStoryboard.Children.Add(collapseH);

            // Margin 动画
            Storyboard.SetTarget(collapseM, this);
            Storyboard.SetTargetProperty(collapseM, new PropertyPath(MarginProperty));
            _exitStoryboard.Children.Add(collapseM);

            _exitStoryboard.Completed += (_, _) =>
            {
                MaxHeight = 0;
                Margin = new Thickness(0);

                // 清理 Storyboard 引用，避免持有卡片
                _exitStoryboard = null;

                ExitCompleted?.Invoke(Item!);
            };

            _exitStoryboard.Begin();
        };

        BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// 清理动画资源，卡片从视觉树移除时调用。
    /// </summary>
    public void Cleanup()
    {
        // 停止并清理进行中的动画
        _exitStoryboard?.Stop();
        _exitStoryboard = null;

        BeginAnimation(OpacityProperty, null);
        SlideTransform.BeginAnimation(TranslateTransform.YProperty, null);
    }

    /// <summary>
    /// 点击卡片：请求关闭（是否允许由上层配置决定）。
    /// </summary>
    /// <param name="sender">事件源。</param>
    /// <param name="e">事件参数。</param>
    private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Item is null || Item.IsExiting || !Item.ClickToDismiss)
        {
            return;
        }

        DismissRequested?.Invoke(Item);
        e.Handled = true;
    }
}
