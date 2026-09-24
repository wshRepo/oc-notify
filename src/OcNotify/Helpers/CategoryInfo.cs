using System.Windows.Media;
using OcNotify.Models;

namespace OcNotify.Helpers;

/// <summary>单个提醒分类的展示元数据（标签 + 颜色）。</summary>
/// <param name="Label">中文展示标签。</param>
/// <param name="ColorHex">分类主色（十六进制，已按主题取色）。</param>
public sealed record CategoryMeta(string Label, string ColorHex);

/// <summary>
/// 提醒分类映射：type → 标签 + 按主题取色。
/// 深色主题用亮色保证深底可读，浅色主题用深色保证浅底可读（对比度）。
/// 集中一处定义，避免 UI 与服务层重复 switch（DRY）。
/// </summary>
public static class CategoryInfo
{
    /// <summary>五类事件：标签 + (浅色主题色, 深色主题色)。</summary>
    private static readonly IReadOnlyDictionary<string, (string Label, string Light, string Dark)> MetaMap =
        new Dictionary<string, (string, string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionIdle"] = ("对话完成", "#16A34A", "#4ADE80"),
            ["permissionAsk"] = ("权限请求", "#D97706", "#FBBF24"),
            ["questionAsk"] = ("问题询问", "#7C3AED", "#A78BFA"),
            ["sessionError"] = ("会话错误", "#DC2626", "#F87171"),
            ["subagentDone"] = ("子代理完成", "#0284C7", "#38BDF8"),
        };

    /// <summary>
    /// 获取分类元数据；未知 type 回退为通用“提醒”与配置强调色。
    /// </summary>
    /// <param name="type">消息 type 字段。</param>
    /// <param name="fallbackAccent">未知分类时的强调色（来自配置）。</param>
    /// <param name="darkTheme">true=深色主题取亮色，false=浅色主题取深色。</param>
    /// <returns>分类元数据。</returns>
    public static CategoryMeta Get(string type, string fallbackAccent = "#7C9CFF", bool darkTheme = false)
    {
        if (!string.IsNullOrWhiteSpace(type) && MetaMap.TryGetValue(type, out var meta))
        {
            return new CategoryMeta(meta.Label, darkTheme ? meta.Dark : meta.Light);
        }

        return new CategoryMeta("提醒", fallbackAccent);
    }

    /// <summary>
    /// 十六进制颜色字符串转 <see cref="Color"/>；解析失败回退中性灰，保证 UI 不因脏配置崩溃。
    /// </summary>
    /// <param name="hex">#RRGGBB 或 #AARRGGBB。</param>
    /// <returns>WPF Color。</returns>
    public static Color ParseColor(string hex)
    {
        try
        {
            var c = ColorConverter.ConvertFromString(hex);
            return c is Color color ? color : Colors.Gray;
        }
        catch
        {
            return Colors.Gray;
        }
    }

    /// <summary>
    /// 在给定颜色上叠加透明度，返回新的 SolidColorBrush。
    /// 卡片背景透明度由 style.opacity 控制（1.0=不透明）。
    /// </summary>
    /// <param name="hex">基础色。</param>
    /// <param name="alpha">0.0~1.0 透明度。</param>
    /// <returns>带 alpha 的画刷。</returns>
    public static SolidColorBrush BrushWithAlpha(string hex, double alpha)
    {
        var c = ParseColor(hex);
        var aByte = (byte)Math.Round(Math.Clamp(alpha, 0.0, 1.0) * 255.0);
        return new SolidColorBrush(Color.FromArgb(aByte, c.R, c.G, c.B));
    }
}
