@echo off
rem Double-click launcher for dev-install.ps1 (ROADMAP M2): elevates itself and installs
rem the agent from the build and payload next to this file. Edit the three values below.
rem   <this folder>\win-arm64\agent.exe, session.exe   the published build (win-x64 on a real PC)
rem   <this folder>\usb\LabControl\setup.json, ca.crt  the USB payload written by the console
rem   STUDENT=1 also creates the standard `student` account with auto-logon (INSTALLER.md step 8).
set "NUMBER=1"
set "CONSOLE=192.168.64.1"
set "STUDENT=0"
set "EXTRA="
if "%STUDENT%"=="1" set "EXTRA=-Student"
set "HERE=%~dp0"
if "%HERE:~-1%"=="\" set "HERE=%HERE:~0,-1%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -Verb RunAs -ArgumentList '-NoProfile -ExecutionPolicy Bypass -NoExit -File \"%HERE%\dev-install.ps1\" -Build \"%HERE%\win-arm64\" -Payload \"%HERE%\usb\" -Number %NUMBER% -ConsoleHost %CONSOLE% %EXTRA%'"
