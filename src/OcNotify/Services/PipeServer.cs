using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using OcNotify.Helpers;
using OcNotify.Models;

namespace OcNotify.Services;

/// <summary>
/// 命名管道服务端：循环接受连接，按行读取 UTF-8 JSON 并解析为 <see cref="NotifyMessage"/>。
/// 设计要点：
/// - 单实例 Mutex 已保证进程唯一，管道名固定 "oc-notify"；
/// - 每个连接独立异步读取到 EOF，支持并发 session 同时提醒；
/// - 坏消息/异常一律静默丢弃，绝不让通知失败影响 opencode 主流程。
/// </summary>
public sealed class PipeServer : IDisposable
{
    /// <summary>管道名（客户端路径 \\.\pipe\oc-notify）。</summary>
    public const string PipeName = "oc-notify";

    /// <summary>单条消息最大长度保护，防止异常客户端写爆内存。</summary>
    private const int MaxMessageBytes = 4096;

    /// <summary>最大并发服务端实例（多 session 并发触发时排队）。</summary>
    private const int MaxServerInstances = 16;

    /// <summary>解析成功的消息回调（可能来自任意线程，订阅方自行调度到 UI 线程）。</summary>
    public event Action<NotifyMessage>? MessageReceived;

    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    /// <summary>
    /// 启动后台接受循环。方法立即返回，不阻塞调用方。
    /// </summary>
    public void Start()
    {
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    /// <summary>
    /// 持续接受连接：每个连接交给独立任务处理，服务端立即回到等待下一连接。
    /// </summary>
    /// <param name="ct">取消令牌。</param>
    /// <returns>任务。</returns>
    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.In,
                    MaxServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);

                var connected = server;
                server = null; // 所有权移交
                _ = Task.Run(() => HandleClientAsync(connected, ct), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(200, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            finally
            {
                server?.Dispose();
            }
        }
    }

    /// <summary>
    /// 读取单个客户端发送的行式 JSON，解析成功后触发 MessageReceived。
    /// </summary>
    /// <param name="server">已连接的管道流。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>任务。</returns>
    private async Task HandleClientAsync(NamedPipeServerStream server, CancellationToken ct)
    {
        try
        {
            using (server)
            {
                var buffer = new byte[512];
                using var ms = new MemoryStream();
                while (!ct.IsCancellationRequested)
                {
                    var read = await server.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        break;
                    }

                    if (ms.Length + read > MaxMessageBytes)
                    {
                        return;
                    }

                    ms.Write(buffer, 0, read);
                }

                if (ms.Length == 0)
                {
                    return;
                }

                var json = Encoding.UTF8.GetString(ms.ToArray());
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                foreach (var line in json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    NotifyMessage? msg;
                    try
                    {
                        msg = JsonSerializer.Deserialize<NotifyMessage>(line, options);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (msg is not null && !string.IsNullOrWhiteSpace(msg.Type))
                    {
                        MessageReceived?.Invoke(msg);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // 客户端异常断开等，静默
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
        _cts.Cancel();
        _cts.Dispose();
    }
}
