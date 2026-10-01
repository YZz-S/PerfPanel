@echo off
chcp 65001 >nul
echo ==== PerfPanel: 安装 PawnIO 传感器驱动 + 更新面板 ====
echo 弹出 UAC 窗口时请点"是"
echo.
net session >nul 2>&1
if %errorlevel%==0 goto :run
powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
exit /b

:run
setlocal
cd /d "%~dp0"

echo [1/4] 安装 PawnIO 驱动(开源签名驱动,替代被系统"内存完整性"拦截的旧 WinRing0 驱动)...
if not exist "%~dp0tools\PawnIO_setup.exe" (
  echo     未找到 tools\PawnIO_setup.exe,请先从 https://github.com/namazso/PawnIO.Setup/releases 下载
  goto :end
)
echo     安装向导即将弹出,一路点 Next/Install 即可(选项保持默认)。
"%~dp0tools\PawnIO_setup.exe"
timeout /t 5 /nobreak >nul
reg query "HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO" /v DisplayVersion >nul 2>&1
if %errorlevel%==0 (
  echo     PawnIO 已安装 ✓
) else (
  echo     [警告] 注册表未查到 PawnIO,安装可能未完成,面板 CPU 温度可能仍不可用
)

echo [2/4] 停止正在运行的 PerfPanel...
taskkill /im PerfPanel.exe /f >nul 2>&1
timeout /t 1 /nobreak >nul

echo [3/4] 更新 exe(旧版备份为 PerfPanel-old.exe)...
if exist "%~dp0PerfPanel-new.exe" (
  copy /y "%~dp0PerfPanel.exe" "%~dp0PerfPanel-old.exe" >nul 2>&1
  copy /y "%~dp0PerfPanel-new.exe" "%~dp0PerfPanel.exe" >nul
  echo     已换新 ✓
) else (
  echo     未找到 PerfPanel-new.exe,保持当前版本
)

echo [4/4] 重新启动 PerfPanel...
schtasks /run /tn PerfPanelAutostart >nul 2>&1
if %errorlevel%==0 (
  echo     已通过开机自启计划任务拉起(管理员,全传感器) ✓
) else (
  start "" "%~dp0PerfPanel.exe"
  echo     已直接启动(如需温度,右键"以管理员身份运行")
)

:end
echo.
echo 完成。CPU 卡片应在一两秒后出现 温度/功耗;页脚不再提示驱动问题。
timeout /t 6 >nul
