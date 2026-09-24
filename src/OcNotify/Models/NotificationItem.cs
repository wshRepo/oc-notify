using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OcNotify.Models;

/// <summary>
/// 单条通知的运行时状态，绑定到 NotificationCard。
/// 在消息基础上追加生命周期字段（创建时间、取消源句柄等），
/// 与传输协议解耦，便于 UI 层独立演进。
/// </summary>
public sealed class NotificationItem : INotifyPropertyChanged
{
    private string _type = string.Empty;
    private string _sessionTitle = string.Empty;
    private string _sessionId = string.Empty;
    private string _label = string.Empty;
    private string _categoryColor = "#7C9CFF";
    private bool _isExiting;

    /// <summary>提醒分类原始 type 字符串。</summary>
    public string Type
    {
        get => _type;
        set => SetField(ref _type, value);
    }

    /// <summary>会话名称（标题）。</summary>
    public string SessionTitle
    {
        get => _sessionTitle;
        set => SetField(ref _sessionTitle, value);
    }

    /// <summary>会话 ID。</summary>
    public string SessionId
    {
        get => _sessionId;
        set => SetField(ref _sessionId, value);
    }

    /// <summary>分类展示标签（如“对话完成”）。</summary>
    public string Label
    {
        get => _label;
        set => SetField(ref _label, value);
    }

    /// <summary>分类颜色（十六进制，用于左侧色条/标签着色）。</summary>
    public string CategoryColor
    {
        get => _categoryColor;
        set => SetField(ref _categoryColor, value);
    }

    /// <summary>是否正在执行消失动画（防止重复触发）。</summary>
    public bool IsExiting
    {
        get => _isExiting;
        set => SetField(ref _isExiting, value);
    }

    /// <summary>创建时刻（本地 UTC 毫秒），用于排序与调试。</summary>
    public long CreatedAtMs { get; set; }

    /// <summary>是否允许点击关闭（从 BehaviorConfig 拷贝快照，避免运行中配置变更竞态）。</summary>
    public bool ClickToDismiss { get; set; } = true;

    /// <summary>属性变更事件（INotifyPropertyChanged）。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 字段赋值 + 通知，仅在值真正变化时触发，避免冗余 UI 刷新。
    /// </summary>
    /// <param name="field"> backing field 引用。</param>
    /// <param name="value">新值。</param>
    /// <param name="propertyName">调用方成员名（编译器自动注入）。</param>
    /// <returns>是否发生了变化。</returns>
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
