@echo off
title Family Time Guard - activity log
powershell -NoProfile -Command "$f = Get-ChildItem \"$env:ProgramData\FamilyTimeGuard\logs\activity-*.csv\" -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1; if ($f) { Start-Process notepad.exe -ArgumentList $f.FullName } else { Write-Host \"No activity recorded yet.\"; pause }"
