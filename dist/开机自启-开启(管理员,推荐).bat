@echo off
chcp 65001 >nul
echo 正在以管理员身份注册开机自启(计划任务 - 完整传感器)...
echo 弹出 UAC 窗口时请点"是"
powershell -NoProfile -Command "Start-Process -FilePath '%~dp0PerfPanel.exe' -ArgumentList '--autostart-on' -Verb RunAs"
