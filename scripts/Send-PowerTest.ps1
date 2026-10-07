# Send-PowerTest.ps1
# 向 oc-notify 命名管道发送电源控制消息（type=power），用于手工验证 behavior.preventSleep。
#
# 用法：
#   .\Send-PowerTest.ps1 -Hold          # 请求阻止休眠（模拟"会话忙碌"）
#   .\Send-PowerTest.ps1                # 请求恢复休眠（模拟"会话空闲"）
#   .\Send-PowerTest.ps1 -Hold -ConfigPath "D:\proj\oc-notify.jsonc"   # 带项目级配置
#
# 验证步骤（需管理员 PowerShell 查看 powercfg /requests）：
#   1. 运行 -Hold，等 1~2 秒
#   2. powercfg /requests  → 应出现 [PROCESS] OcNotify.exe 的 power request
#   3. 不带参数运行一次，powercfg /requests  → 该条目应消失
#   4. 什么都不做静置 120s（PowerGuard 租约），也必须自动消失

param(
    # true=请求阻止休眠；省略/false=请求恢复休眠
    [switch]$Hold,

    # 可选：项目级 oc-notify.jsonc 绝对路径，用于验证项目级 preventSleep 覆盖
    [string]$ConfigPath
)

$pipeName = "oc-notify"
$payload = @{
    type      = "power"
    hold      = [bool]$Hold
    timestamp = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
}

if ($ConfigPath) {
    $payload["configPath"] = $ConfigPath
}

$msg = $payload | ConvertTo-Json -Compress

$state = if ($Hold) { "阻止休眠" } else { "恢复休眠" }
Write-Host "发送 power → $state" -ForegroundColor Cyan
Write-Host "  JSON: $msg" -ForegroundColor DarkGray

try {
    $client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::Out)
    # 等待服务端就绪，最多 3 秒
    $client.Connect(3000)

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
    $client.Write($bytes, 0, $bytes.Length)
    $client.Flush()
    $client.Dispose()

    Write-Host "  已发送 ✓" -ForegroundColor Green
    Write-Host "  现在跑 'powercfg /requests' 看 OcNotify.exe 是否出现在列表中。" -ForegroundColor DarkGray
    exit 0
}
catch {
    Write-Host "  发送失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  请确认 oc-notify.exe 正在运行。" -ForegroundColor Yellow
    exit 1
}
