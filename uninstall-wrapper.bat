@echo off
setlocal EnableExtensions

net session >nul 2>&1
if errorlevel 1 (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
    exit /b
)

call :ResolveBlueStacksInstallDir
set "DST=%BLUESTACKS_INSTALL_DIR%dinput8.dll"

if not exist "%DST%" (
    echo Wrapper DLL is not installed:
    echo   %DST%
    echo.
    pause
    exit /b 0
)

echo Removing wrapper:
echo   %DST%
echo.

del /F /Q "%DST%"
if errorlevel 1 (
    echo.
    echo Failed to remove wrapper DLL.
    pause
    exit /b 1
)

echo.
echo Wrapper DLL removed.
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
