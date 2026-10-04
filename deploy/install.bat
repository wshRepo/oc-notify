@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul 2>nul
title oc-notify 一键部署

rem ============================================================
rem  oc-notify 一键部署脚本
rem  双击运行：检测 .NET 8 Desktop 运行时 → 复制文件到
rem  opencode 配置目录 → 智能合并配置（只增不删、用户值优先）
rem  → 若部署时关闭了正在运行的 OcNotify.exe 则自动重启新版
rem  → 打印部署报告（窗口保持打开）
rem ============================================================

set "SRC=%~dp0files"
set "CFG=%USERPROFILE%\.config\opencode"
set "PLUGINS=%CFG%\plugins"
set "ASSETS=%CFG%\assets\OcNotify"
set "DEFAULT_JSON=%SRC%\oc-notify.default.jsonc"
set "USER_JSON=%CFG%\oc-notify.jsonc"
set "MERGE_PS1=%~dp0merge-config.ps1"

set /a OK=0
set /a FAIL=0
set /a SKIP=0

set "VERSION=v1.0.1"

echo ============================================================
echo    oc-notify 一键部署  %VERSION%
echo ============================================================
echo.

rem ---------- 0. 源文件检查 ----------
if not exist "%SRC%\notify-bubble.ts" (
    echo [X] 未找到部署源: %SRC%\notify-bubble.ts
    echo     请确认 deploy\files 目录完整后重试。
    echo.
    pause
    exit /b 1
)
if not exist "%SRC%\OcNotify\OcNotify.exe" (
    echo [X] 未找到部署源: %SRC%\OcNotify\OcNotify.exe
    echo     请确认 deploy\files\OcNotify 目录完整后重试。
    echo.
    pause
    exit /b 1
)
if not exist "%MERGE_PS1%" (
    echo [X] 未找到合并脚本: %MERGE_PS1%
    echo     请确认 deploy 目录完整后重试。
    echo.
    pause
    exit /b 1
)

rem ---------- 1. 检测 .NET 8 Desktop 运行时 ----------
set "HAS_RUNTIME=0"

if exist "%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App" (
    for /d %%V in ("%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*") do (
        if exist "%%~fV" set "HAS_RUNTIME=1"
    )
)
if exist "%ProgramFiles(x86)%\dotnet\shared\Microsoft.WindowsDesktop.App" (
    for /d %%V in ("%ProgramFiles(x86)%\dotnet\shared\Microsoft.WindowsDesktop.App\8.*") do (
        if exist "%%~fV" set "HAS_RUNTIME=1"
    )
)
if exist "%USERPROFILE%\.dotnet\shared\Microsoft.WindowsDesktop.App" (
    for /d %%V in ("%USERPROFILE%\.dotnet\shared\Microsoft.WindowsDesktop.App\8.*") do (
        if exist "%%~fV" set "HAS_RUNTIME=1"
    )
)
if defined DOTNET_ROOT (
    if exist "%DOTNET_ROOT%\shared\Microsoft.WindowsDesktop.App" (
        for /d %%V in ("%DOTNET_ROOT%\shared\Microsoft.WindowsDesktop.App\8.*") do (
            if exist "%%~fV" set "HAS_RUNTIME=1"
        )
    )
)

if "!HAS_RUNTIME!"=="0" (
    echo [X] 未检测到 .NET 8 Desktop 运行时 ^(x64^)
    echo.
    echo     OcNotify.exe 依赖 .NET 8 桌面运行时，请先安装:
    echo.
    echo     1. 打开下载页:
    echo        https://dotnet.microsoft.com/download/dotnet/8.0
    echo     2. 下载并安装 "^.NET Desktop Runtime 8.0.x^(x64^)"
    echo        ^(注意是 Desktop Runtime，不是 ASP.NET / Runtime^)
    echo     3. 安装完成后重新双击本脚本
    echo.
    pause
    exit /b 1
)
echo [OK] 已检测到 .NET 8 Desktop 运行时
echo.

rem ---------- 2. 停止正在运行的 OcNotify.exe ----------
rem  记录 KILLED：部署成功后据此自动拉起新版，避免用户漏掉这一步
taskkill /F /IM OcNotify.exe >nul 2>&1
if not errorlevel 1 (
    set "KILLED=1"
    echo [i] 已停止正在运行的 OcNotify.exe
    echo.
    timeout /t 1 /nobreak >nul
)

rem ---------- 3. 创建目标目录 ----------
if not exist "%PLUGINS%" mkdir "%PLUGINS%"
if not exist "%ASSETS%" mkdir "%ASSETS%"

rem ---------- 4-5. 复制插件和 exe ----------
echo 复制文件:
call :COPY_ONE "%SRC%\notify-bubble.ts" "%PLUGINS%\notify-bubble.ts"
for %%F in ("%SRC%\OcNotify\*.*") do (
    call :COPY_ONE "%%~fF" "%ASSETS%\%%~nxF"
)

rem ---------- 6. 配置文件（智能合并：只增不删、用户值优先） ----------
rem  由 merge-config.ps1 执行：补新字段、保留用户值/注释/已删字段、
rem  合并前自动备份 .bak。退出码 0=已合并 1=失败 2=无变更
if exist "%USER_JSON%" (
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%MERGE_PS1%" -TemplatePath "%DEFAULT_JSON%" -TargetPath "%USER_JSON%"
    if errorlevel 2 (
        set /a SKIP+=1
    ) else if errorlevel 1 (
        echo   [失败] oc-notify.jsonc 合并失败，你的原配置未被修改
        set /a FAIL+=1
    ) else (
        set /a OK+=1
    )
) else (
    call :COPY_ONE "%DEFAULT_JSON%" "%USER_JSON%"
)

rem ---------- 7. 自动重启（部署前正在运行的 OcNotify.exe） ----------
if defined KILLED (
    if exist "%ASSETS%\OcNotify.exe" (
        start "" "%ASSETS%\OcNotify.exe"
        echo   [OK] 已自动启动新版 OcNotify.exe
        echo.
    )
)

rem ---------- 8. 部署报告 ----------
echo ------------------------------------------------------------
echo   成功: !OK!    跳过: !SKIP!    失败: !FAIL!
echo ============================================================
echo.
echo 目标位置:
echo   插件   %PLUGINS%\notify-bubble.ts
echo   程序   %ASSETS%\OcNotify.exe
echo   配置   %USER_JSON%
echo.
echo 下一步:
if defined KILLED (
    echo   1. OcNotify.exe 已自动重启，无需手动启动
) else (
    echo   1. 未在部署时检测到运行中的 OcNotify.exe；发通知时插件会自动拉起
)
echo   2. 重启 opencode CLI（插件仅在启动时加载）
echo   3. 发一条消息，切到其他窗口，等回复完成即应弹气泡
echo   4. 修改配置请编辑上面的 oc-notify.jsonc（保存即热生效）
echo.
if !FAIL! gtr 0 (
    echo 注意: 存在失败项，请检查上方 [失败] 信息后重试。
    echo.
)
echo 按任意键关闭窗口...
pause >nul
exit /b 0

rem ============================================================
rem  子过程: 复制单个文件并记入报告
rem  用法: call :COPY_ONE "源" "目标"
rem ============================================================
:COPY_ONE
set "SRCF=%~1"
set "DSTF=%~2"
copy /Y "%SRCF%" "%DSTF%" >nul 2>&1
if errorlevel 1 (
    echo   [失败] %~nx1 -^> %DSTF%
    set /a FAIL+=1
) else (
    echo   [成功] %~nx1 -^> %DSTF%
    set /a OK+=1
)
goto :eof
