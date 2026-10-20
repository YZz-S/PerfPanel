@echo off
chcp 65001 >nul
title PerfPanel 更新
rem 用法:把新编译的 exe 放在本脚本旁边命名为 PerfPanel.new.exe,双击本脚本。
rem 流程:自提权(UAC)→ 结束 PerfPanel → 把新 exe 覆盖到 PerfPanelAutostart 计划任务指向的路径
rem       (同时更新本目录 dist\PerfPanel.exe 副本)→ 由计划任务重新拉起面板。
net session >nul 2>&1
if %errorlevel%==0 goto run
echo [..] 面板以管理员权限运行,需要提权才能结束并替换,即将弹出 UAC 确认窗口…
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b

:run
if not exist "%~dp0PerfPanel.new.exe" (
    echo [!] 未找到 "%~dp0PerfPanel.new.exe"
    timeout /t 5 >nul
    exit /b 1
)
echo [1/3] 结束正在运行的 PerfPanel…
taskkill /im PerfPanel.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul
echo [2/3] 替换 exe…
set "TASKEXE="
for /f "tokens=2 delims=><" %%a in ('schtasks /query /tn PerfPanelAutostart /xml 2^>nul ^| find "<Command>"') do set "TASKEXE=%%a"
if defined TASKEXE (
    copy /y "%~dp0PerfPanel.new.exe" "%TASKEXE%" >nul && echo       已更新 %TASKEXE%
) else (
    echo [!] 读取 PerfPanelAutostart 计划任务路径失败,仅更新本目录副本
)
copy /y "%~dp0PerfPanel.new.exe" "%~dp0PerfPanel.exe" >nul
del "%~dp0PerfPanel.new.exe" >nul 2>&1
echo [3/3] 重启面板…
schtasks /run /tn PerfPanelAutostart >nul 2>&1
if %errorlevel%==0 (
    echo       已由 PerfPanelAutostart 计划任务拉起(管理员权限,满传感器)
) else (
    echo [!] 计划任务拉起失败,直接启动普通权限副本…
    start "" "%~dp0PerfPanel.exe"
)
echo.
echo 完成,3 秒后关闭
timeout /t 3 >nul
