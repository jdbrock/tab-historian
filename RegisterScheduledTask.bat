@echo off
:: Registers a scheduled task to run TabHistorian every 5 minutes with elevated privileges.
:: Must be run as Administrator.

net session >nul 2>&1
if %errorlevel% neq 0 (
    powershell -Command "Start-Process cmd -ArgumentList '/c \"\"%~f0\"\"' -Verb RunAs"
    exit /b 0
)

schtasks /query /tn "TabHistorian" >nul 2>&1
if %errorlevel% equ 0 (
    echo Removing existing TabHistorian scheduled task...
    schtasks /delete /tn "TabHistorian" /f >nul 2>&1
)

schtasks /create ^
    /tn "TabHistorian" ^
    /tr "powershell.exe -WindowStyle Hidden -Command & '%~dp0src\TabHistorian\bin\TabHistorian.exe'" ^
    /sc minute /mo 5 ^
    /rl highest ^
    /f

if %errorlevel% equ 0 (
    echo.
    echo TabHistorian scheduled task created successfully.
    echo It will run every 5 minutes with highest privileges.
) else (
    echo.
    echo ERROR: Failed to create scheduled task.
)

pause
