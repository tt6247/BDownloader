@echo off
setlocal
set "appDirectory=%~dp0app"
if not exist "%appDirectory%\BDownloader.exe" (
    echo Application missing. Run scripts\build.ps1 first.
    pause
    exit /b 1
)
start "" /d "%appDirectory%" "%appDirectory%\BDownloader.exe"
