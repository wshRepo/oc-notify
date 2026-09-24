using System.IO;
using System.Text.Json;
using OcNotify.Models;

namespace OcNotify.Services;

/// <summary>
/// 配置加载与热更新服务。
/// 职责：定位全局配置文件 → 读取（失败重试 3 次）→ 可选合并项目级 → 对外发布变更事件。
/// 遵循健壮性要求：文件读写必须 try-catch + 自动重试。
/// </summary>
public sealed class ConfigService : IDisposable
{
    /// <summary>JSON 序列化选项：camelCase、允许尾逗号（便于手改配置）、大小写不敏感。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    /// <summary>全局配置文件绝对路径（~/.config/opencode/oc-notify.json）。</summary>
    public string GlobalConfigPath { get; }

    /// <summary>当前生效配置。</summary>
    public NotifyConfig Current { get; private set; } = new();

    /// <summary>配置热更新事件（文件变更或手动 Reload 后触发）。</summary>
    public event Action<NotifyConfig>? ConfigChanged;

    private FileSystemWatcher? _watcher;
    private System.Timers.Timer? _debounce;
    private readonly object _reloadLock = new();
    private DateTime _lastWriteTimeUtc;
    private bool _disposed;

    /// <summary>
    /// 构造：计算全局配置路径并尝试首次加载；启动文件监视。
    /// </summary>
    /// <param name="globalPath">覆盖全局路径（测试用），null 则用默认用户目录。</param>
    public ConfigService(string? globalPath = null)
    {
        GlobalConfigPath = globalPath ?? GetDefaultGlobalPath();
        Current = LoadMerged(null);
        _lastWriteTimeUtc = GetFileWriteTimeUtc(GlobalConfigPath);
        StartWatcher();
    }

    /// <summary>默认全局配置路径：%USERPROFILE%\.config\opencode\oc-notify.json。</summary>
    /// <returns>绝对路径。</returns>
    public static string GetDefaultGlobalPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "opencode", "oc-notify.json");
    }

    /// <summary>
    /// 若磁盘配置的修改时间晚于上次加载，则重新加载（消息到达时调用，兜底 watcher 丢事件）。
    /// </summary>
    /// <returns>是否发生了重载。</returns>
    public bool EnsureFresh()
    {
        var mtime = GetFileWriteTimeUtc(GlobalConfigPath);
        if (mtime == DateTime.MinValue || mtime <= _lastWriteTimeUtc)
        {
            return false;
        }

        Reload(null);
        return true;
    }

    /// <summary>
    /// 安全读取文件修改时间；失败返回 MinValue。
    /// </summary>
    /// <param name="path">文件路径。</param>
    /// <returns>UTC 修改时间。</returns>
    private static DateTime GetFileWriteTimeUtc(string path)
    {
        try
        {
            return File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// 重新加载并（若与旧值引用不同）发布变更事件。
    /// 供文件监视回调与手动刷新共用。
    /// </summary>
    /// <param name="projectPath">可选项目级配置路径（来自消息 configPath）。</param>
    /// <returns>合并后的最新配置。</returns>
    public NotifyConfig Reload(string? projectPath = null)
    {
        lock (_reloadLock)
        {
            var next = LoadMerged(projectPath);
            Current = next;
            _lastWriteTimeUtc = GetFileWriteTimeUtc(GlobalConfigPath);
        }

        ConfigChanged?.Invoke(Current);
        return Current;
    }

    /// <summary>
    /// 读取全局 + 可选项目级配置并合并；文件不存在或损坏时回退默认值，不抛出。
    /// </summary>
    /// <param name="projectPath">项目级配置路径。</param>
    /// <returns>合并配置。</returns>
    private NotifyConfig LoadMerged(string? projectPath)
    {
        var global = ReadConfigFile(GlobalConfigPath) ?? new NotifyConfig();
        if (string.IsNullOrWhiteSpace(projectPath))
        {
            return global;
        }

        var project = ReadConfigFile(projectPath);
        return NotifyConfig.Merge(global, project);
    }

    /// <summary>
    /// 读取单个 JSON 配置文件；带 3 次重试（应对杀软占用/网络盘抖动）。
    /// 任何失败返回 null，由调用方决定回退策略。
    /// </summary>
    /// <param name="path">文件绝对路径。</param>
    /// <returns>配置对象；不存在/失败返回 null。</returns>
    private static NotifyConfig? ReadConfigFile(string path)
    {
        const int maxRetry = 3;
        for (var attempt = 1; attempt <= maxRetry; attempt++)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<NotifyConfig>(json, JsonOptions) ?? new NotifyConfig();
            }
            catch (JsonException)
            {
                // JSON 语法错误重试无意义，直接放弃该文件
                return null;
            }
            catch (IOException)
            {
                if (attempt == maxRetry)
                {
                    return null;
                }

                Thread.Sleep(50 * attempt);
            }
            catch (UnauthorizedAccessException)
            {
                if (attempt == maxRetry)
                {
                    return null;
                }

                Thread.Sleep(50 * attempt);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// 监视全局配置文件所在目录；文件改名/变更/创建均触发 Reload。
    /// 监视失败（目录不存在等）时静默降级为“仅启动时读一次”。
    /// </summary>
    private void StartWatcher()
    {
        try
        {
            var dir = Path.GetDirectoryName(GlobalConfigPath);
            if (string.IsNullOrEmpty(dir))
            {
                return;
            }

            // 目录可能尚不存在（用户从未创建配置），预先创建以便监视
            Directory.CreateDirectory(dir);

            _watcher = new FileSystemWatcher(dir)
            {
                Filter = Path.GetFileName(GlobalConfigPath),
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            // 文件保存可能触发多次事件，做简单去抖：延迟 100ms 合并处理
            // debounce 存字段，防止被 GC
            _debounce = new System.Timers.Timer(100) { AutoReset = false };
            _debounce.Elapsed += (_, _) =>
            {
                try
                {
                    Reload(null);
                }
                catch
                {
                    // 热更新失败不影响运行
                }
            };

            FileSystemEventHandler onChange = (_, _) => _debounce?.Start();
            _watcher.Changed += onChange;
            _watcher.Created += onChange;
            _watcher.Renamed += (_, _) => _debounce?.Start();
            _watcher.Deleted += (_, _) => _debounce?.Start();
        }
        catch
        {
            // 监视不可用时忽略，配置仍可通过进程重启生效
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
