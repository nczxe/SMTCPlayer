@echo off
title Release SMTCPlayer

set "PROJECT_DIR=%~dp0"
cd /d "%PROJECT_DIR%"

echo ============================================================
echo   SMTC Player - Release
echo ============================================================

echo [1/5] Syntax check...
python -m compileall server tests
if errorlevel 1 exit /b 1

echo [2/5] Running tests...
python -m pytest tests
if errorlevel 1 (
    echo [Info] pytest unavailable or tests failed, trying unittest...
    python -m unittest discover -s tests
    if errorlevel 1 exit /b 1
)

echo [3/5] Building all...
call build.bat
if errorlevel 1 exit /b 1

echo [4/5] Copying .NET UI output to dist...
echo.

REM --- WPF ---
set "WPF_SRC=smtc-ui\SMTCPlayer.Wpf\bin\Release\net10.0-windows\win-x64\publish"
if exist "%WPF_SRC%\SMTCPlayer.Wpf.exe" (
    xcopy /E /I /Y "%WPF_SRC%\*" "dist\SMTCPlayer-WPF\" >nul
    echo [OK] WPF UI copied to dist\SMTCPlayer-WPF\
) else (
    echo [Info] WPF UI not found, skipping
)

REM --- WinUI ---
set "WINUI_SRC=smtc-ui\SMTCPlayer.WinUI\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64"
if exist "%WINUI_SRC%\SMTCPlayer.WinUI.exe" (
    xcopy /E /I /Y "%WINUI_SRC%\*" "dist\SMTCPlayer-WinUI\" >nul
    echo [OK] WinUI UI copied to dist\SMTCPlayer-WinUI\
) else (
    echo [Info] WinUI UI not found, skipping
)

echo.
echo [5/5] Release artifacts:
echo   Python server: dist\SMTCPlayer\
echo   WPF UI:        dist\SMTCPlayer-WPF\
echo   WinUI UI:      dist\SMTCPlayer-WinUI\
echo.
if exist "dist\SMTCPlayer_WPF_Setup_*.exe" (
    echo   WPF Installer:  dist\SMTCPlayer_WPF_Setup_v*.exe
) else (
    echo   WPF Installer:  not built (install Inno Setup 6+ to enable)
)
if exist "dist\SMTCPlayer_WinUI_Setup_*.exe" (
    echo   WinUI Installer: dist\SMTCPlayer_WinUI_Setup_v*.exe
) else (
    echo   WinUI Installer: not built (install Inno Setup 6+ to enable)
)
