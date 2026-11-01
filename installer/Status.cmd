@echo off
title Family Time Guard - status
"%~dp0FamilyTimeGuard.exe" status
echo.
"%~dp0FamilyTimeGuard.exe" check-config
echo.
pause
