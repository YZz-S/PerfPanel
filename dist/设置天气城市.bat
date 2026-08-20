@echo off
chcp 65001 >nul
set /p CITY=请输入城市名(支持中文/拼音/英文,如:上海 / Chengdu / Tokyo):
if "%CITY%"=="" (echo 未输入,已取消 & pause & exit /b)
"%~dp0PerfPanel.exe" --set-city "%CITY%"
