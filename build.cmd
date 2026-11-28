@echo off
REM ====================================================================
REM  Double-click launcher for build.ps1
REM  Calls the PowerShell build script with the execution policy bypassed.
REM  All arguments are passed through, e.g.:
REM     build.cmd -Mode framework
REM     build.cmd -Clean -Sign -PfxPath cert.pfx
REM ====================================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
