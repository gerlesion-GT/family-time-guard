@echo off
:: Family Time Guard - connect the phone remote (Telegram)
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Asking Windows for administrator permission...
    powershell -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Setup-Phone.ps1"
echo.
pause
