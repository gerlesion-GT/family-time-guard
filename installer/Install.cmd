@echo off
:: Family Time Guard installer - double-click this file.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Asking Windows for administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install.ps1"
echo.
pause
