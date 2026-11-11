@echo off
title Installing Persocord
rem Double-click this for a first install and for every update after.
rem Settings and clips are stored elsewhere and are never touched.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\install.ps1"
echo.
pause
