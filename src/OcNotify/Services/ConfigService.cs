using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using OcNotify.Helpers;
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

    /// <summary>JSON 解析选项：与 <see cref="JsonOptions"/> 保持同一套宽松语义。</summary>
    private static readonly JsonDocumentOptions ParseOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>全局配置文件绝对路径（~/.config/opencode/oc-notify.jsonc）。</summary>
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

    /// <summary>默认全局配置路径：%USERPROFILE%\.config\opencode\oc-notify.jsonc。</summary>
    /// <returns>绝对路径。</returns>
    public static string GetDefaultGlobalPath()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".config", "opencode", "oc-notify.jsonc");
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
        var global = ReadConfigObject(GlobalConfigPath) ?? new JsonObject();
        if (!string.IsNullOrWhiteSpace(projectPath))
        {
            var project = ReadConfigObject(projectPath);
            if (project is not null)
            {
                global = MergeObjects(global, project);
            }
        }

        try
        {
            return global.Deserialize<NotifyConfig>(JsonOptions) ?? new NotifyConfig();
        }
        catch (JsonException ex)
        {
            // 合并后的 JSON 结构合法但类型不匹配（如 opacity 写成字符串）时兜底默认值
            DiagLog.Write($"[ERROR] ConfigService: deserialize failed, fallback to defaults: {ex.Message}");
            return new NotifyConfig();
        }
    }

    /// <summary>
    /// 逐字段深合并：project 的每个字段覆盖 global 的同名字段，未提供的字段保留 global 值。
    ///
    /// 为什么必须在 JsonNode 层做而不是反序列化成 POCO 再拼：
    /// System.Text.Json 会给缺失字段填 C# 默认值，POCO 层面无法区分
    /// "字段缺省"与"字段恰好等于默认值"，于是任何"哪段 project 提供了就用哪段"的写法
    /// 都会把整段打回默认值——项目级只写 {theme:"dark"} 就会连 opacity 一起丢掉。
    /// 现在改为按原始 JSON 逐字段合并，与插件侧 pick() 的语义完全一致。
    /// </summary>
    /// <param name="global">全局配置（底，不被修改）。</param>
    /// <param name="project">项目级配置（顶，可为 null）。</param>
    /// <returns>合并后的新对象。</returns>
    private static JsonObject MergeObjects(JsonObject global, JsonObject? project)
    {
        if (project is null)
        {
            return global;
        }

        var result = (JsonObject)global.DeepClone();
        foreach (var field in project)
        {
            // 显式 null 视为"未提供"：若照单覆盖会把 style/behavior 整段打成 null，
            // 下游访问 Style.Language 之类立刻 NRE。保留 global 值更安全。
            if (field.Value is null)
            {
                continue;
            }

            // 两侧都是对象 → 递归逐字段（style/behavior/events 三段就是这么合的）
            if (result.TryGetPropertyValue(field.Key, out var current) &&
                current is JsonObject currentObj &&
                field.Value is JsonObject overrideObj)
            {
                result[field.Key] = MergeObjects(currentObj, overrideObj);
                continue;
            }

            // 标量/数组，或 global 侧不存在该键 → 直接覆盖
            result[field.Key] = field.Value.DeepClone();
        }

        return result;
    }

    /// <summary>
    /// 读取单个 JSONC 配置文件为原始 JSON 对象；带 3 次重试（应对杀软占用/网络盘抖动）。
    /// 任何失败返回 null，由调用方决定回退策略。
    /// </summary>
    /// <param name="path">文件绝对路径。</param>
    /// <returns>JSON 对象；不存在/失败返回 null。</returns>
    private static JsonObject? ReadConfigObject(string path)
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
                return JsonNode.Parse(json, documentOptions: ParseOptions) as JsonObject;
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
