@echo off
REM Vox Commander voice service launcher.
REM Started automatically by the OpenHV VoxBridge trait, or runnable manually.

setlocal
set ROOT=%~dp0
set PYTHON=%ROOT%voice-service\.venv\Scripts\python.exe
set PYTHONUNBUFFERED=1

if not exist "%PYTHON%" (
    echo [vox-commander] python venv not found at %PYTHON%
    echo                 run: cd voice-service ^&^& python -m venv .venv ^&^& .venv\Scripts\python.exe -m pip install -e .
    exit /b 1
)

cd /d "%ROOT%voice-service"
start "" "%PYTHON%" -u -m vox.panel
