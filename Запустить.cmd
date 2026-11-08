@echo off
setlocal
if exist "%~dp0publish\VoiceSiteLauncher.exe" (
    start "" "%~dp0publish\VoiceSiteLauncher.exe"
    exit /b 0
)
if exist "%~dp0VoiceSiteLauncher.exe" (
    start "" "%~dp0VoiceSiteLauncher.exe"
    exit /b 0
)
echo VoiceSiteLauncher.exe was not found. Download the built executable or build the C# project.
echo See README.md for build instructions.
pause
exit /b 1
