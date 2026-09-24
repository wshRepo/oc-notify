@echo off
setlocal EnableDelayedExpansion
chcp 65001 >nul 2>nul
title oc-notify 一键部署

rem ============================================================
rem  oc-notify 一键部署脚本
rem  双击运行：检测 .NET 8 Desktop 运行时 → 复制文件到
rem  opencode 配置目录 → 打印部署报告（窗口保持打开）
rem ============================================================

set "SRC=%~dp0files"
set "CFG=%USERPROFILE%\.config\opencode"
set "PLUGINS=%CFG%\plugins"
set "ASSETS=%CFG%\assets\OcNotify"
set "DEFAULT_JSON=%SRC%\oc-notify.default.json"
set "USER_JSON=%CFG%\oc-notify.json"

set /a OK=0
set /a FAIL=0
set /a SKIP=0

set "VERSION=v1.0.0"

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
taskkill /F /IM OcNotify.exe >nul 2>&1
if not errorlevel 1 (
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

rem ---------- 6. 配置文件（已存在则保留用户配置） ----------
if exist "%USER_JSON%" (
    echo   [跳过] oc-notify.json 已存在，保留你的现有配置
    set /a SKIP+=1
) else (
    call :COPY_ONE "%DEFAULT_JSON%" "%USER_JSON%"
)

rem ---------- 7. 部署报告 ----------
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
echo   1. 重启 opencode CLI（插件仅在启动时加载）
echo   2. 发一条消息，切到其他窗口，等回复完成即应弹气泡
echo   3. 修改配置请编辑上面的 oc-notify.json（保存即热生效）
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
