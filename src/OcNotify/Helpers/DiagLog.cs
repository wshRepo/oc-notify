using System.IO;

namespace OcNotify.Helpers;

/// <summary>
/// exe 侧诊断日志：统一追加到 %TEMP%\oc-notify-error.log。
/// 单独抽成工具类是因为 App 启动失败与 PowerGuard 都需要写同一个文件，
/// 重复两遍路径拼接与异常吞掉逻辑属于无意义的 DRY 债务。
/// 约定：日志是最后一道诊断手段，绝不允许因写日志失败影响主流程。
/// </summary>
public static class DiagLog
{
    /// <summary>日志文件绝对路径（%TEMP%\oc-notify-error.log）。</summary>
    public static string FilePath => Path.Combine(Path.GetTempPath(), "oc-notify-error.log");

    /// <summary>
    /// 追加一行带时间戳的日志；任何失败静默忽略。
    /// </summary>
    /// <param name="message">日志内容（调用方自行带级别前缀）。</param>
    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(FilePath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败忽略
        }
    }

    /// <summary>
    /// 仅当 behavior.debug 开启时写日志，避免默认状态下产生无谓磁盘 IO。
    /// </summary>
    /// <param name="debugEnabled">当前配置是否开启调试日志。</param>
    /// <param name="message">日志内容。</param>
    public static void WriteIfDebug(bool debugEnabled, string message)
    {
        if (debugEnabled)
        {
            Write(message);
        }
    }
}
