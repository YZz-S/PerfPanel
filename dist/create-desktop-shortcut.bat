@echo off
rem Create a desktop shortcut for PerfPanel (exe must sit next to this script).
powershell -NoProfile -Command "$ws = New-Object -ComObject WScript.Shell; $lnkPath = Join-Path ([Environment]::GetFolderPath('Desktop')) 'PerfPanel.lnk'; $lnk = $ws.CreateShortcut($lnkPath); $lnk.TargetPath = '%~dp0PerfPanel.exe'; $lnk.WorkingDirectory = '%~dp0'; $lnk.IconLocation = '%~dp0PerfPanel.exe,0'; $lnk.Save(); Write-Host 'Created PerfPanel.lnk on Desktop'"
