# Send-StackTest.ps1
# 连发多条不同类型消息，验证堆叠/过期/上移动画。
# 用法：
#   .\Send-StackTest.ps1           # 连发 5 条
#   .\Send-StackTest.ps1 -Count 6  # 连发 6 条（超过默认 maxVisible=5，验证挤出）

param(
    [int]$Count = 5,

    [int]$IntervalMs = 400
)

$pipeName = "oc-notify"
$types = @("sessionIdle", "permissionAsk", "questionAsk", "sessionError", "subagentDone")
$titles = @("前端重构", "部署脚本", "API 网关", "数据库迁移", "单元测试", "文档补全", "缓存优化")

Write-Host "连发 $Count 条通知（间隔 ${IntervalMs}ms）..." -ForegroundColor Cyan

for ($i = 0; $i -lt $Count; $i++) {
    $type = $types[$i % $types.Count]
    $title = $titles[$i % $titles.Count] + " # $($i + 1)"

    $msg = @{
        type         = $type
        sessionID    = "ses_stack_$i"
        sessionTitle = $title
        timestamp    = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    } | ConvertTo-Json -Compress

    try {
        $client = [System.IO.Pipes.NamedPipeClientStream]::new(".", $pipeName, [System.IO.Pipes.PipeDirection]::Out)
        $client.Connect(2000)
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
        $client.Write($bytes, 0, $bytes.Length)
        $client.Flush()
        $client.Dispose()
        Write-Host ("  [{0}/{1}] {2,-16} {3}" -f ($i + 1), $Count, $type, $title) -ForegroundColor Green
    }
    catch {
        Write-Host ("  [{0}/{1}] 失败: {2}" -f ($i + 1), $Count, $_.Exception.Message) -ForegroundColor Red
    }

    if ($i -lt $Count - 1) {
        Start-Sleep -Milliseconds $IntervalMs
    }
}

Write-Host "完成。观察气泡：新的在下、旧的超时渐隐后其余上移。" -ForegroundColor Cyan
