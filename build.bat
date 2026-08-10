@echo off
title Build SMTCPlayer

echo ============================================================
echo   SMTC Player - Build Tool
echo ============================================================
echo.

set "PROJECT_DIR=%~dp0"
cd /d "%PROJECT_DIR%"

echo [1/6] Checking Python...
python --version >nul 2>&1
if errorlevel 1 (
    echo [Error] Python not found.
    pause
    exit /b 1
)
echo [OK] Python ready
echo.

echo [2/6] Installing PyInstaller...
python -c "import PyInstaller" >nul 2>&1
if errorlevel 1 (
    echo [Info] Installing PyInstaller...
    pip install pyinstaller
)
echo [OK] PyInstaller ready
echo.

echo [3/6] Installing project dependencies...
pip install -r server\requirements.txt
echo [OK] Dependencies ready
echo.

echo [4/6] Building Python server (onedir)...
echo.
pyinstaller --clean --noconfirm SMTCPlayer.spec
if errorlevel 1 (
    echo.
    echo [Error] Python build failed!
    pause
    exit /b 1
)
echo [OK] Python package built to dist\SMTCPlayer\
echo.

echo [5/6] Building .NET UI (WPF + WinUI)...
where dotnet >nul 2>&1
if errorlevel 1 (
    echo [Warn] dotnet CLI not found, skipping .NET UI build
    goto :skip_dotnet
)
echo [Info] Building WinUI (SelfContained)...
dotnet build smtc-ui\SMTCPlayer.WinUI\SMTCPlayer.WinUI.csproj -c Release --nologo
if errorlevel 1 (
    echo [Warn] WinUI build failed
    goto :skip_dotnet
)
echo [OK] WinUI UI built to smtc-ui\SMTCPlayer.WinUI\bin\x64\Release\
echo [Info] Publishing WPF (SelfContained)...
dotnet publish smtc-ui\SMTCPlayer.Wpf\SMTCPlayer.Wpf.csproj -c Release -r win-x64 --self-contained --nologo
if errorlevel 1 (
    echo [Warn] WPF publish failed
    goto :skip_dotnet
)
echo [OK] WPF UI published to smtc-ui\SMTCPlayer.Wpf\bin\Release\net10.0-windows\win-x64\publish\
:skip_dotnet
echo.

echo [6/6] Looking for Inno Setup compiler...
set "ISCC="
if exist "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" (
    set "ISCC=C:\Program Files (x86)\Inno Setup 6\ISCC.exe"
)
if exist "C:\Program Files\Inno Setup 6\ISCC.exe" (
    set "ISCC=C:\Program Files\Inno Setup 6\ISCC.exe"
)

if defined ISCC (
    echo [Info] Building WPF installer...
    "%ISCC%" setup.iss
    if not errorlevel 1 (
        echo [OK] WPF installer built to dist\SMTCPlayer_WPF_Setup_v*.exe
    ) else (
        echo [Warn] WPF installer build failed
    )
    echo [Info] Building WinUI installer...
    "%ISCC%" setup-winui.iss
    if not errorlevel 1 (
        echo [OK] WinUI installer built to dist\SMTCPlayer_WinUI_Setup_v*.exe
    ) else (
        echo [Warn] WinUI installer build failed
    )
) else (
    echo [Info] Inno Setup not found, skipping installer build
    echo [Info] Download: https://jrsoftware.org/isinfo.php
)

echo.
echo ============================================================
echo   Build complete!
echo.
echo   Python server: dist\SMTCPlayer\
echo   WPF UI:        smtc-ui\SMTCPlayer.Wpf\bin\Release\net10.0-windows\win-x64\publish\
echo   WinUI UI:      smtc-ui\SMTCPlayer.WinUI\bin\x64\Release\
echo   WPF installer: dist\SMTCPlayer_WPF_Setup_v*.exe  (if ISCC found)
echo   WinUI installer: dist\SMTCPlayer_WinUI_Setup_v*.exe  (if ISCC found)
echo ============================================================
echo.
pause
