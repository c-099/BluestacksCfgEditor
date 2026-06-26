@echo off
setlocal EnableExtensions

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

set "SCRIPT_DIR=%~dp0"
set "SRC=%SCRIPT_DIR%dinput8.dll"
if not exist "%SRC%" set "SRC=%SCRIPT_DIR%app\dinput8.dll"

if not exist "%SRC%" (
    echo Source wrapper DLL was not found.
    echo Expected one of:
    echo   %SCRIPT_DIR%dinput8.dll
    echo   %SCRIPT_DIR%app\dinput8.dll
    echo.
    pause
    exit /b 2
)

call :ResolveBlueStacksInstallDir
set "DST=%BLUESTACKS_INSTALL_DIR%dinput8.dll"

echo Installing wrapper:
echo   Source:      %SRC%
echo   Destination: %DST%
echo.

copy /Y "%SRC%" "%DST%"
if errorlevel 1 (
    echo.
    echo Failed to copy wrapper DLL.
    pause
    exit /b 1
)

echo.
echo Wrapper DLL installed.
pause
exit /b 0

:ResolveBlueStacksInstallDir
set "BLUESTACKS_INSTALL_DIR="
for %%V in (InstallDir InstallDir64) do (
    for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\BlueStacks_nxt" /v %%V /reg:64 2^>nul ^| findstr /i "REG_SZ"') do if not defined BLUESTACKS_INSTALL_DIR set "BLUESTACKS_INSTALL_DIR=%%B"
    for /f "tokens=2,*" %%A in ('reg query "HKLM\SOFTWARE\BlueStacks_nxt" /v %%V /reg:32 2^>nul ^| findstr /i "REG_SZ"') do if not defined BLUESTACKS_INSTALL_DIR set "BLUESTACKS_INSTALL_DIR=%%B"
)
if not defined BLUESTACKS_INSTALL_DIR set "BLUESTACKS_INSTALL_DIR=C:\Program Files\BlueStacks_nxt"
if not "%BLUESTACKS_INSTALL_DIR:~-1%"=="\" set "BLUESTACKS_INSTALL_DIR=%BLUESTACKS_INSTALL_DIR%\"
exit /b 0
