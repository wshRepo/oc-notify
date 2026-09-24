# Send-TestNotification.ps1
# 向 oc-notify 命名管道发送单条测试消息。
# 用法：
#   .\Send-TestNotification.ps1
#   .\Send-TestNotification.ps1 -Type permissionAsk -Title "我的项目" -SessionId "ses_test"

param(
    [ValidateSet("sessionIdle", "permissionAsk", "questionAsk", "sessionError", "subagentDone")]
    [string]$Type = "sessionIdle",

    [string]$Title = "测试会话",

    [string]$SessionId = "ses_local_test"
)

$pipeName = "oc-notify"
$msg = @{
    type         = $Type
    sessionID    = $SessionId
    sessionTitle = $Title
    timestamp    = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
} | ConvertTo-Json -Compress

Write-Host "发送 [$Type] -> $Title" -ForegroundColor Cyan
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
    exit 0
}
catch {
    Write-Host "  发送失败: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "  请确认 oc-notify.exe 正在运行。" -ForegroundColor Yellow
    exit 1
}
