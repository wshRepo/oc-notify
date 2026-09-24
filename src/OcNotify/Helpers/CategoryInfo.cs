using System.Windows.Media;
using OcNotify.Models;

namespace OcNotify.Helpers;

/// <summary>单个提醒分类的展示元数据（标签 + 颜色）。</summary>
/// <param name="Label">中文展示标签。</param>
/// <param name="ColorHex">分类主色（十六进制）。</param>
public sealed record CategoryMeta(string Label, string ColorHex);

/// <summary>
/// 提醒分类映射：type 字符串 → 展示标签与颜色。
/// 集中一处定义，避免 UI 与服务层重复 switch（DRY）。
/// </summary>
public static class CategoryInfo
{
    /// <summary>五类事件的默认元数据。</summary>
    private static readonly IReadOnlyDictionary<string, CategoryMeta> MetaMap =
        new Dictionary<string, CategoryMeta>(StringComparer.OrdinalIgnoreCase)
        {
            ["sessionIdle"] = new("对话完成", "#4ADE80"),
            ["permissionAsk"] = new("权限请求", "#FBBF24"),
            ["questionAsk"] = new("问题询问", "#A78BFA"),
            ["sessionError"] = new("会话错误", "#F87171"),
            ["subagentDone"] = new("子代理完成", "#38BDF8"),
        };

    /// <summary>
    /// 获取分类元数据；未知 type 回退为通用“提醒”与默认强调色。
    /// </summary>
    /// <param name="type">消息 type 字段。</param>
    /// <param name="fallbackAccent">配置中的强调色（未知分类时用）。</param>
    /// <returns>分类元数据。</returns>
    public static CategoryMeta Get(string type, string fallbackAccent = "#7C9CFF")
    {
        if (!string.IsNullOrWhiteSpace(type) && MetaMap.TryGetValue(type, out var meta))
        {
            return meta;
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
    /// 毛玻璃模式下卡片背景需要半透明才能透出 backdrop。
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
