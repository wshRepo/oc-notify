using System.Runtime.InteropServices;

namespace OcNotify.Services;

/// <summary>
/// DWM（Desktop Window Manager）P/Invoke 封装：毛玻璃 backdrop、系统圆角、去边框。
/// 所有调用均 try-catch 降级，API 不可用（旧系统/远程会话）时返回 false，由调用方回退纯色方案。
/// </summary>
public static class DwmHelper
{
    /// <summary>DWMWA_SYSTEMBACKDROP_TYPE 属性编号（Win11 22621+）。</summary>
    private const int DwmwaSystemBackdropType = 38;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE 属性编号（Win11 22000+）。</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWA_BORDER_COLOR 属性编号（Win11 22000+）。</summary>
    private const int DwmwaBorderColor = 34;

    /// <summary>DWMWA_USE_IMMERSIVE_DARK_MODE：深色模式边框。</summary>
    private const int DwmwaUseImmersiveDarkMode = 20;

    // DWM_SYSTEMBACKDROP_TYPE 枚举值
    private const int DwmsbtAuto = 0;
    private const int DwmsbtNone = 1;
    private const int DwmsbtMainWindow = 2;
    private const int DwmsbtTransientWindow = 3; // Acrylic 毛玻璃
    private const int DwmsbtTabbedWindow = 4;

    // DWM_WINDOW_CORNER_PREFERENCE 枚举值
    private const int DwmwcpDoNotRound = 1;
    private const int DwmwcpRound = 2;
    private const int DwmwcpRoundSmall = 3;

    /// <summary>无边框颜色（DWMWA_COLOR_NONE）。</summary>
    private const int DwmwaColorNone = unchecked((int)0xFFFFFFFE);

    /// <summary>
    /// 设置/关闭窗口系统 backdrop（毛玻璃）。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="enableAcrylic">true=Acrylic 毛玻璃，false=NONE。</param>
    /// <returns>是否调用成功。</returns>
    public static bool SetAcrylic(IntPtr hwnd, bool enableAcrylic)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var value = enableAcrylic ? DwmsbtTransientWindow : DwmsbtNone;
            var hr = DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref value, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 关闭/开启系统绘制的窗口边框（用 DWMWA_COLOR_NONE 抑制，得到干净无边框外观）。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <returns>是否调用成功。</returns>
    public static bool SuppressBorder(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var color = DwmwaColorNone;
            var hr = DwmSetWindowAttribute(hwnd, DwmwaBorderColor, ref color, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 设置系统圆角偏好（对启用 backdrop 的整窗有效；卡片自身圆角由 WPF Border 负责）。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="round">true=圆角。</param>
    /// <returns>是否调用成功。</returns>
    public static bool SetRoundedCorners(IntPtr hwnd, bool round)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var value = round ? DwmwcpRound : DwmwcpDoNotRound;
            var hr = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref value, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 深色模式下使用深色边框/文字，提升毛玻璃上的可读性。
    /// </summary>
    /// <param name="hwnd">窗口句柄。</param>
    /// <param name="dark">true=深色模式。</param>
    /// <returns>是否调用成功。</returns>
    public static bool SetDarkMode(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            var value = dark ? 1 : 0;
            var hr = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
            return hr == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>DwmSetWindowAttribute P/Invoke。</summary>
    /// <param name="hwnd">句柄。</param>
    /// <param name="attribute">属性编号。</param>
    /// <param name="value">值引用。</param>
    /// <param name="size">值大小（字节）。</param>
    /// <returns>HRESULT。</returns>
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    // 保留枚举常量，便于调试时对照文档；未直接引用的按约定不删除以保持可读性
    #pragma warning disable CS0414
    private static readonly int DwmsbtAutoUnused = DwmsbtAuto;
    private static readonly int DwmsbtMainWindowUnused = DwmsbtMainWindow;
    private static readonly int DwmsbtTabbedWindowUnused = DwmsbtTabbedWindow;
    private static readonly int DwmwcpRoundSmallUnused = DwmwcpRoundSmall;
    #pragma warning restore CS0414
}
