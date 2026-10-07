using System.Text.Json.Serialization;

namespace OcNotify.Models;

/// <summary>
/// 气泡通知整体配置，对应 oc-notify.jsonc 根对象。
/// 分为样式、行为、事件开关三段，便于按需深合并与热更新。
/// </summary>
public sealed class NotifyConfig
{
    /// <summary>视觉样式配置。</summary>
    [JsonPropertyName("style")]
    public StyleConfig Style { get; set; } = new();

    /// <summary>行为配置（时长、上限、点击）。</summary>
    [JsonPropertyName("behavior")]
    public BehaviorConfig Behavior { get; set; } = new();

    /// <summary>各提醒事件启用开关（P4 插件侧过滤用，exe 仅解析存档）。</summary>
    [JsonPropertyName("events")]
    public EventsConfig Events { get; set; } = new();
}

/// <summary>样式配置。</summary>
public sealed class StyleConfig
{
    /// <summary>整体透明度 0.0~1.0，直接作为卡片背景 alpha；1.0=完全不透明（默认）。</summary>
    [JsonPropertyName("opacity")]
    public double Opacity { get; set; } = 1.0;

    /// <summary>毛玻璃开关（默认开）：亮边框+霜面风格；关=纯色深底。</summary>
    [JsonPropertyName("glassEffect")]
    public bool GlassEffect { get; set; } = true;

    /// <summary>左侧色条基色（分类未指定颜色时的回退）。</summary>
    [JsonPropertyName("accentColor")]
    public string AccentColor { get; set; } = "#7C9CFF";

    /// <summary>卡片圆角半径（DIP）。</summary>
    [JsonPropertyName("cornerRadius")]
    public double CornerRadius { get; set; } = 14;

    /// <summary>气泡停靠位置：top-left / top-right / bottom-left / bottom-right，默认右上。</summary>
    [JsonPropertyName("position")]
    public string Position { get; set; } = "top-right";

    /// <summary>主题：light / dark，默认浅色。</summary>
    [JsonPropertyName("theme")]
    public string Theme { get; set; } = "light";

    /// <summary>
    /// 界面语言：zh（默认）/ en；en-xx 前缀亦识别为英文。
    /// 影响分类标签等展示文案，保存即热生效。
    /// </summary>
    [JsonPropertyName("language")]
    public string Language { get; set; } = "zh";

    /// <summary>是否深色主题（解析 Theme 的便捷判断，非配置字段）。</summary>
    [JsonIgnore]
    public bool IsDarkTheme =>
        string.Equals(Theme, "dark", StringComparison.OrdinalIgnoreCase);
}

/// <summary>行为配置。</summary>
public sealed class BehaviorConfig
{
    /// <summary>自动消失时间（毫秒）。</summary>
    [JsonPropertyName("durationMs")]
    public int DurationMs { get; set; } = 5000;

    /// <summary>最大同时可见气泡数。</summary>
    [JsonPropertyName("maxVisible")]
    public int MaxVisible { get; set; } = 5;

    /// <summary>是否允许点击气泡立即消失。</summary>
    [JsonPropertyName("clickToDismiss")]
    public bool ClickToDismiss { get; set; } = true;

    /// <summary>
    /// 粘性气泡（默认关）：开启后不启动自动消失计时，durationMs 不再生效；
    /// 同时强制允许点击关闭，clickToDismiss 配置被视为开启。
    /// 气泡仅在点击（或被 maxVisible 挤出）时消失。
    /// </summary>
    [JsonPropertyName("sticky")]
    public bool Sticky { get; set; }

    /// <summary>
    /// 仅在 opencode 非前台时弹窗（默认开）。
    /// 检测在插件侧完成（祖先进程链 + GetForegroundWindow），
    /// exe 收到消息即视为需要展示；此字段供配置读取与文档一致性。
    /// </summary>
    [JsonPropertyName("onlyWhenInactive")]
    public bool OnlyWhenInactive { get; set; } = true;

    /// <summary>
    /// 调试日志开关（默认关）。
    /// 开启后插件写 %TEMP%\oc-notify-plugin.log，
    /// exe 侧可在关键路径调用同一开关输出诊断信息。
    /// </summary>
    [JsonPropertyName("debug")]
    public bool Debug { get; set; }

    /// <summary>
    /// 空闲检查间隔（毫秒，默认 30s）。
    /// exe 周期检查是否还有 opencode 进程，全部退出后自动关闭本进程。
    /// </summary>
    [JsonPropertyName("idleCheckIntervalMs")]
    public int IdleCheckIntervalMs { get; set; } = 30_000;

    /// <summary>
    /// 空闲退出重试次数（默认 2 次）。
    /// 连续 N 次检查未发现 opencode 进程才退出；总宽限 ≈ interval × retry。
    /// </summary>
    [JsonPropertyName("idleRetry")]
    public int IdleRetry { get; set; } = 2;

    /// <summary>
    /// 阻止 Windows 自动休眠（默认关）。
    /// 开启后：opencode 存在忙碌会话时向系统申请电源请求，屏蔽"无操作 N 分钟后睡眠"；
    /// 会话空闲后自动恢复休眠。屏幕熄灭不受影响（未申请 ES_DISPLAY_REQUIRED），
    /// 手动休眠/关机也不受影响。
    /// 决策在插件侧（它才知道会话忙不忙），本字段同时作为 exe 侧的独立否决：
    /// 此处改为 false 会立即释放请求，不必等插件心跳。
    /// </summary>
    [JsonPropertyName("preventSleep")]
    public bool PreventSleep { get; set; }
}

/// <summary>各提醒事件启用开关。</summary>
public sealed class EventsConfig
{
    /// <summary>对话完成（session.idle）。</summary>
    [JsonPropertyName("sessionIdle")]
    public bool SessionIdle { get; set; } = true;

    /// <summary>权限请求（permission.asked）。</summary>
    [JsonPropertyName("permissionAsk")]
    public bool PermissionAsk { get; set; } = true;

    /// <summary>问题询问（question.asked）。</summary>
    [JsonPropertyName("questionAsk")]
    public bool QuestionAsk { get; set; } = true;

    /// <summary>会话错误（session.error）。</summary>
    [JsonPropertyName("sessionError")]
    public bool SessionError { get; set; } = true;

    /// <summary>子代理完成。</summary>
    [JsonPropertyName("subagentDone")]
    public bool SubagentDone { get; set; } = true;
}
