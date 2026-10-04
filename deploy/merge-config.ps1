#requires -Version 5.1
<#
  oc-notify 配置智能合并脚本（install.bat 调用，也可手动执行）

  合并原则：只增不删，用户数据优先
    - 用户已有的设置值与行注释一律保留（含模板中已删除的孤儿字段/段）
    - 模板新增的字段/段自动补入，并携带模板中的新注释
    - 已存在字段的注释：用户注释优先，缺失时回退模板注释

  为什么是重新生成而不是文本插行：
    JSON 解析器序列化时会丢弃注释，无法原地保真改写；本脚本按
    模板结构 + 用户值 + 双方注释表重新输出 JSONC，并在写回前校验产物，
    任何一步失败都不修改用户原文件（且写回前自动备份 .bak）。

  退出码：0 = 新建或已合并；1 = 失败（原文件未修改）；2 = 无变更（幂等跳过）
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$TemplatePath,

    [Parameter(Mandatory = $true)]
    [string]$TargetPath
)

$ErrorActionPreference = 'Stop'
# 让 PowerShell 5.1 的中文输出以 UTF-8 写入控制台（install.bat 已 chcp 65001）
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8 } catch { }

# ---------------------------------------------------------------------------
# JSONC 剥离：状态机去掉 // 与 /* */ 注释、移除尾逗号
# 必须用状态机而非正则——字符串值里的 //（如 http://x）会被正则误删
# ---------------------------------------------------------------------------
function StripJsonc {
    param([Parameter(Mandatory = $true)][string]$Text)

    $sb = New-Object System.Text.StringBuilder
    $inStr = $false   # 是否在字符串字面量内
    $esc = $false     # 上一字符是否为转义反斜杠
    $lastComma = -1   # 输出缓冲中最近一个非字符串逗号位置（用于识别尾逗号）
    $i = 0
    $len = $Text.Length

    while ($i -lt $len) {
        $c = $Text[$i]
        if ($inStr) {
            [void]$sb.Append($c)
            if ($esc) { $esc = $false }
            elseif ($c -eq '\') { $esc = $true }
            elseif ($c -eq '"') { $inStr = $false }
            $i++
            continue
        }
        if ($c -eq '"') { $inStr = $true; [void]$sb.Append($c); $i++; continue }
        if ($c -eq '/' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '/') {
            # 行注释：保留换行符，维持报错时的行号准确
            while ($i -lt $len -and $Text[$i] -ne "`n") { $i++ }
            continue
        }
        if ($c -eq '/' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '*') {
            $i += 2
            while ($i -lt $len -and -not ($Text[$i] -eq '*' -and ($i + 1) -lt $len -and $Text[$i + 1] -eq '/')) {
                if ($Text[$i] -eq "`n") { [void]$sb.Append("`n") }
                $i++
            }
            $i = [Math]::Min($i + 2, $len)
            continue
        }
        if ($c -eq ',') { $lastComma = $sb.Length; [void]$sb.Append($c); $i++; continue }
        if ($c -eq '}' -or $c -eq ']') {
            # 逗号到闭括号之间只有空白 -> 尾逗号，移除（保证产物是合法 JSON）
            if ($lastComma -ge 0) {
                $between = $sb.ToString().Substring($lastComma + 1)
                if ($between -match '^\s*$') { [void]$sb.Remove($lastComma, 1) }
            }
            $lastComma = -1
            [void]$sb.Append($c)
            $i++
            continue
        }
        [void]$sb.Append($c)
        $i++
    }
    return $sb.ToString()
}

# ---------------------------------------------------------------------------
# 行尾注释定位：字符串感知地找出行内第一个位于字符串外的 //
# 返回注释起始下标，无注释返回 -1
# ---------------------------------------------------------------------------
function Get-CommentIndex {
    # AllowEmptyString：文件尾部/段间空行会传入空串，属正常情况
    param([AllowEmptyString()][Parameter(Mandatory = $true)][string]$Line)

    $inStr = $false
    $esc = $false
    for ($i = 0; $i -lt $Line.Length; $i++) {
        $c = $Line[$i]
        if ($inStr) {
            if ($esc) { $esc = $false }
            elseif ($c -eq '\') { $esc = $true }
            elseif ($c -eq '"') { $inStr = $false }
            continue
        }
        if ($c -eq '"') { $inStr = $true; continue }
        if ($c -eq '/' -and ($i + 1) -lt $Line.Length -and $Line[$i + 1] -eq '/') { return $i }
    }
    return -1
}

# ---------------------------------------------------------------------------
# 注释表：按行扫描 + 路径栈维护嵌套，得到 路径 -> // 注释 的映射
# 无法识别的行只影响注释归属；值一律来自解析器，不受扫描质量影响
# ---------------------------------------------------------------------------
function Get-CommentMap {
    param([Parameter(Mandatory = $true)][string]$Text)

    $map = @{}
    $stack = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($Text -split "`r?`n")) {
        $idx = Get-CommentIndex $line
        $code = if ($idx -ge 0) { $line.Substring(0, $idx) } else { $line }
        $comment = if ($idx -ge 0) { $line.Substring($idx).Trim() } else { $null }
        $trimmed = $code.TrimEnd()

        if ($trimmed -match '^\s*"([^"]+)"\s*:\s*\{\s*$') {
            # 段开括号行 "xxx": {
            $key = $Matches[1]
            $path = if ($stack.Count -gt 0) { ($stack -join '.') + '.' + $key } else { $key }
            if ($comment) { $map[$path] = $comment }
            $stack.Add($key)
            continue
        }
        if ($trimmed -match '^\s*\}') {
            if ($stack.Count -gt 0) { $stack.RemoveAt($stack.Count - 1) }
            continue
        }
        if ($trimmed -match '^\s*"([^"]+)"\s*:') {
            # 字段行（含数组开始行）：注释归属该字段
            $key = $Matches[1]
            $path = if ($stack.Count -gt 0) { ($stack -join '.') + '.' + $key } else { $key }
            if ($comment) { $map[$path] = $comment }
            continue
        }
    }
    return $map
}

# ---------------------------------------------------------------------------
# 文件头注释：首个 { 之前的连续 // 行（输出时保留用户头，其次模板头）
# ---------------------------------------------------------------------------
function Get-FileHeader {
    param([Parameter(Mandatory = $true)][string]$Text)

    $header = New-Object System.Collections.Generic.List[string]
    foreach ($line in ($Text -split "`r?`n")) {
        $t = $line.Trim()
        if ($t -eq '') { if ($header.Count -gt 0) { break } else { continue } }
        if ($t.StartsWith('//')) { $header.Add($line); continue }
        break
    }
    return $header
}

# ---------------------------------------------------------------------------
# 值判断与序列化
# ---------------------------------------------------------------------------
function Test-IsObject {
    param([object]$Value)
    return $null -ne $Value -and $Value -is [System.Management.Automation.PSCustomObject]
}

function Format-Scalar {
    param([object]$Value)
    # 单值压缩输出；PS 5.1 把 0.90 解析为 Decimal，round-trip 可保留 0.90 写法
    return ConvertTo-Json -InputObject $Value -Compress -Depth 10
}

# 注释取值：用户注释优先（不丢失用户批注），缺失回退模板注释
function Get-FieldComment {
    param([Parameter(Mandatory = $true)][string]$ChildPath)
    if ($script:UserComments.ContainsKey($ChildPath)) { return $script:UserComments[$ChildPath] }
    if ($script:TmplComments.ContainsKey($ChildPath)) { return $script:TmplComments[$ChildPath] }
    return $null
}

# ---------------------------------------------------------------------------
# 递归合并并生成输出行：
#   遍历顺序 = 模板字段（随模板顺序）-> 用户孤儿字段（随用户顺序）；
#   同名字段用户值优先；模板新增记入统计；各层独立对齐行尾注释
# ---------------------------------------------------------------------------
function Write-MergedObject {
    param(
        [AllowNull()][object]$TemplateObj,
        [AllowNull()][object]$UserObj,
        [string]$Path,
        [int]$Indent,
        [System.Collections.Generic.List[string]]$Out
    )

    $indentStr = ' ' * ($Indent * 2)
    $entries = New-Object System.Collections.Generic.List[object]

    $userMap = @{}
    if ($null -ne $UserObj) {
        foreach ($up in $UserObj.PSObject.Properties) { $userMap[$up.Name] = $up.Value }
    }

    $seen = @{}
    if ($null -ne $TemplateObj) {
        foreach ($tp in $TemplateObj.PSObject.Properties) {
            $k = $tp.Name
            $seen[$k] = $true
            $childPath = if ($Path) { $Path + '.' + $k } else { $k }
            $tVal = $tp.Value
            $hasUser = $userMap.ContainsKey($k)
            $uVal = if ($hasUser) { $userMap[$k] } else { $null }
            $comment = Get-FieldComment $childPath
            $first = $indentStr + '"' + $k + '": '

            if ($hasUser -and (Test-IsObject $tVal) -and (Test-IsObject $uVal)) {
                # 双方都是对象 -> 逐字段递归合并
                $body = New-Object System.Collections.Generic.List[string]
                Write-MergedObject $tVal $uVal $childPath ($Indent + 1) $body
                $entries.Add(@{ First = $first + '{'; Body = $body; IsObject = $true; Comment = $comment })
            }
            elseif ($hasUser) {
                # 用户值优先（标量，或用户把对象改成了标量/数组）
                $entries.Add(@{ First = $first + (Format-Scalar $uVal); Body = $null; IsObject = $false; Comment = $comment })
            }
            elseif (Test-IsObject $tVal) {
                # 模板新增的段/子对象（叶子字段在递归中记入统计）
                $body = New-Object System.Collections.Generic.List[string]
                Write-MergedObject $tVal $null $childPath ($Indent + 1) $body
                $entries.Add(@{ First = $first + '{'; Body = $body; IsObject = $true; Comment = $comment })
            }
            else {
                # 模板新增的标量字段
                $script:AddedPaths.Add($childPath)
                $entries.Add(@{ First = $first + (Format-Scalar $tVal); Body = $null; IsObject = $false; Comment = $comment })
            }
        }
    }

    # 用户孤儿字段/段：模板已删除 -> 保留用户值与注释
    if ($null -ne $UserObj) {
        foreach ($up in $UserObj.PSObject.Properties) {
            if ($seen.ContainsKey($up.Name)) { continue }
            $k = $up.Name
            $childPath = if ($Path) { $Path + '.' + $k } else { $k }
            $script:OrphanCount++
            $comment = if ($script:UserComments.ContainsKey($childPath)) { $script:UserComments[$childPath] } else { $null }
            $first = $indentStr + '"' + $k + '": '
            if (Test-IsObject $up.Value) {
                $body = New-Object System.Collections.Generic.List[string]
                Write-MergedObject $null $up.Value $childPath ($Indent + 1) $body
                $entries.Add(@{ First = $first + '{'; Body = $body; IsObject = $true; Comment = $comment })
            }
            else {
                $entries.Add(@{ First = $first + (Format-Scalar $up.Value); Body = $null; IsObject = $false; Comment = $comment })
            }
        }
    }

    if ($entries.Count -eq 0) { return }

    # 注释对齐基准：首行加逗号后的最大长度（对象的逗号在闭括号行，不计入）
    $widths = New-Object System.Collections.Generic.List[int]
    for ($i = 0; $i -lt $entries.Count; $i++) {
        $w = $entries[$i].First.Length
        if (-not $entries[$i].IsObject -and $i -lt $entries.Count - 1) { $w++ }
        $widths.Add($w)
    }
    $maxW = ($widths | Measure-Object -Maximum).Maximum

    for ($i = 0; $i -lt $entries.Count; $i++) {
        $e = $entries[$i]
        $isLast = ($i -eq $entries.Count - 1)
        $line = $e.First
        if (-not $e.IsObject -and -not $isLast) { $line += ',' }
        if ($e.Comment) {
            $pad = [Math]::Max(2, $maxW + 2 - $line.Length)
            $line += (' ' * $pad) + $e.Comment
        }
        $Out.Add($line)
        if ($null -ne $e.Body) { foreach ($b in $e.Body) { $Out.Add($b) } }
        if ($e.IsObject) {
            $close = $indentStr + '}'
            if (-not $isLast) { $close += ',' }
            $Out.Add($close)
        }
    }
}

# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------
$script:AddedPaths = New-Object System.Collections.Generic.List[string]
$script:OrphanCount = 0
$script:UserComments = @{}
$script:TmplComments = @{}

try {
    if (-not (Test-Path -LiteralPath $TemplatePath)) { throw "模板不存在: $TemplatePath" }

    # 目标不存在 -> 直接从模板创建（配合 install.bat 首次安装或手动部署）
    if (-not (Test-Path -LiteralPath $TargetPath)) {
        Copy-Item -LiteralPath $TemplatePath -Destination $TargetPath -Force
        Write-Output '  [新建] 未发现已有配置，已从模板创建'
        exit 0
    }

    $tmplText = [System.IO.File]::ReadAllText($TemplatePath, [System.Text.Encoding]::UTF8)
    $targetText = [System.IO.File]::ReadAllText($TargetPath, [System.Text.Encoding]::UTF8)

    # 解析模板（失败 = 发布包损坏，硬失败不碰用户文件）
    try {
        $tmplObj = StripJsonc $tmplText | ConvertFrom-Json
    }
    catch { throw "模板解析失败: $($_.Exception.Message)" }

    # 解析用户配置（失败 = 备份后用模板重建，绝不静默覆盖不备份）
    try {
        $userObj = StripJsonc $targetText | ConvertFrom-Json
    }
    catch {
        Copy-Item -LiteralPath $TargetPath -Destination "$TargetPath.bak" -Force
        Copy-Item -LiteralPath $TemplatePath -Destination $TargetPath -Force
        Write-Output '  [警告] 原配置无法解析，已备份为 .bak 并用模板重建'
        exit 0
    }

    # 双方注释表（重新生成时决定每个字段带谁的注释）
    $script:TmplComments = Get-CommentMap $tmplText
    $script:UserComments = Get-CommentMap $targetText

    # 重新生成合并结果
    $out = New-Object System.Collections.Generic.List[string]
    $header = Get-FileHeader $targetText
    if ($header.Count -eq 0) { $header = Get-FileHeader $tmplText }
    if ($header.Count -eq 0) {
        $header.Add('// oc-notify 配置文件')
        $header.Add('// 支持 // 与 /* */ 注释、尾逗号；保存即热生效，无需重启任何进程')
    }
    foreach ($h in $header) { $out.Add($h) }
    $out.Add('{')
    $body = New-Object System.Collections.Generic.List[string]
    Write-MergedObject $tmplObj $userObj '' 0 $body
    foreach ($b in $body) { $out.Add($b) }
    $out.Add('}')
    $newText = ($out -join "`r`n") + "`r`n"

    # 写回前校验产物可解析，失败则原文件保持原样
    try {
        [void](StripJsonc $newText | ConvertFrom-Json)
    }
    catch { throw "合并结果校验失败，原文件未修改: $($_.Exception.Message)" }

    # 幂等：无任何新增/差异则不写回（避免每次部署都动用户文件）
    if ($newText -ceq $targetText) {
        Write-Output '  [跳过] 无新增设置项，配置已是最新'
        exit 2
    }

    # 备份后写回（UTF-8 无 BOM：带 BOM 会导致插件端 JSON.parse 失败）
    Copy-Item -LiteralPath $TargetPath -Destination "$TargetPath.bak" -Force
    [System.IO.File]::WriteAllText($TargetPath, $newText, (New-Object System.Text.UTF8Encoding($false)))

    if ($script:AddedPaths.Count -gt 0) {
        $shown = @($script:AddedPaths | Select-Object -First 5)
        $suffix = ''
        if ($script:AddedPaths.Count -gt 5) { $suffix = " 等 $($script:AddedPaths.Count) 项" }
        Write-Output "  [合并] 新增设置 $($script:AddedPaths.Count) 项：$($shown -join '、')$suffix"
    }
    if ($script:OrphanCount -gt 0) {
        Write-Output "  [合并] 保留你的自定义设置 $($script:OrphanCount) 项（模板中已删除的字段/段）"
    }
    Write-Output "  [备份] 原配置已备份 → $(Split-Path -Leaf $TargetPath).bak"
    if ($script:AddedPaths.Count -eq 0 -and $script:OrphanCount -eq 0) {
        Write-Output '  [合并] 配置格式已规范化（字段与注释无增减）'
    }
    exit 0
}
catch {
    Write-Output "  [失败] $($_.Exception.Message)"
    exit 1
}
