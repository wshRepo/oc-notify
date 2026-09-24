using System.Text.Json.Serialization;

namespace OcNotify.Models;

/// <summary>
/// 气泡通知整体配置，对应 oc-notify.json 根对象。
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

    /// <summary>
    /// 深合并：project 覆盖 global 的同名字段（仅覆盖非默认的显式值场景简化为逐段整体替换）。
    /// 这里采用简单策略：哪段 project 提供了就用哪段，否则沿用 global。
    /// </summary>
    /// <param name="global">全局配置（底）。</param>
    /// <param name="project">项目级配置（顶），可为 null。</param>
    /// <returns>合并后的新实例。</returns>
    public static NotifyConfig Merge(NotifyConfig global, NotifyConfig? project)
    {
        if (project is null)
        {
            return global;
        }

        return new NotifyConfig
        {
            Style = project.Style ?? global.Style,
            Behavior = project.Behavior ?? global.Behavior,
            Events = project.Events ?? global.Events,
        };
    }
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
