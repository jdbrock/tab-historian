@echo off
:: Registers a scheduled task to run TabHistorian every 30 minutes with elevated privileges.
:: Must be run as Administrator.

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b 0
)

schtasks /query /tn "TabHistorian" >nul 2>&1
if %errorlevel% equ 0 (
    echo TabHistorian scheduled task already exists. No changes made.
    echo To re-create it, delete the existing task first:
    echo   schtasks /delete /tn "TabHistorian" /f
    pause
    exit /b 0
)

schtasks /create ^
    /tn "TabHistorian" ^
    /tr "\"%~dp0publish\TabHistorian\TabHistorian.exe\"" ^
    /sc minute /mo 30 ^
    /rl highest ^
    /f

if %errorlevel% equ 0 (
    echo.
    echo TabHistorian scheduled task created successfully.
    echo It will run every 30 minutes with highest privileges.
) else (
    echo.
    echo ERROR: Failed to create scheduled task.
)

pause
