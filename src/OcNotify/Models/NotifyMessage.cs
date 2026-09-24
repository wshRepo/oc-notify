using System.Text.Json.Serialization;

namespace OcNotify.Models;

/// <summary>
/// 命名管道传输的一行 JSON 消息模型。
/// 协议字段保持 camelCase，与 opencode 插件侧（TS）约定一致。
/// </summary>
public sealed class NotifyMessage
{
    /// <summary>提醒分类：sessionIdle / permissionAsk / questionAsk / sessionError / subagentDone。</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = string.Empty;

    /// <summary>opencode 会话 ID。</summary>
    [JsonPropertyName("sessionID")]
    public string SessionId { get; set; } = string.Empty;

    /// <summary>会话名称（展示用标题）。</summary>
    [JsonPropertyName("sessionTitle")]
    public string SessionTitle { get; set; } = string.Empty;

    /// <summary>事件时间戳（Unix 毫秒），服务端可用于去重/排序。</summary>
    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; }

    /// <summary>
    /// 可选：项目级配置文件绝对路径。P4 插件侧可传入，
    /// 服务端读取后与全局配置深合并（样式/行为以项目级优先）。
    /// </summary>
    [JsonPropertyName("configPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ConfigPath { get; set; }
}
