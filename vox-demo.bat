@echo off
REM Vox Commander one-click demo launcher.
REM Skips OpenHV's main menu entirely and lands the player directly in a
REM skirmish on the Vox Demo map, so the submission video starts in-match
REM with no keyboard or mouse interaction.

setlocal
set ROOT=%~dp0
set OPENHV=%ROOT%openhv

if not exist "%OPENHV%\launch-game.cmd" (
    echo [vox-demo] OpenHV not found at %OPENHV%
    echo            Clone it first ^(see README^) then re-run this script.
    exit /b 1
)

cd /d "%OPENHV%"
call .\launch-game.cmd Launch.Map=vox-demo
