@echo off
REM ====================================================================
REM  Doppelklick-Starter fuer build.ps1
REM  Ruft das PowerShell-Build-Skript mit umgangener ExecutionPolicy auf.
REM  Alle uebergebenen Argumente werden durchgereicht, z.B.:
REM     build.cmd -Mode framework
REM     build.cmd -Clean -Sign -PfxPath cert.pfx
REM ====================================================================
setlocal
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
echo.
pause
