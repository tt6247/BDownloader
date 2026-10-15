@echo off
setlocal
set "taskApp=%~dp0app"
if not exist "%taskApp%\BDownloader.exe" (
    echo Application not found. Run scripts\build.ps1 first.
    pause
    exit /b 1
)
if not exist "%taskApp%\yt-dlp.exe" (
    echo Download engine missing. Run scripts\prepare-media-tools.ps1 first.
    pause
    exit /b 1
)
start "" /d "%taskApp%" "%taskApp%\BDownloader.exe"
