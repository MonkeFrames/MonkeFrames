@echo off
title MonkeFrames - Build and Install
echo.
echo  MonkeFrames - build and install
echo  ===============================
echo.
echo  1. Make sure Gorilla Tag is CLOSED (it locks the mod files while running).
echo  2. Needs the .NET 10 SDK (https://dotnet.microsoft.com/download).
echo.
pause

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo  ERROR: dotnet was not found. Install the .NET 10 SDK and try again.
    pause
    exit /b 1
)

cd /d "%~dp0"
dotnet build MonkeFrames.slnx -c Release -nologo -v q -clp:ErrorsOnly
if errorlevel 1 (
    echo.
    echo  BUILD FAILED - copy the red errors above and send them over.
    pause
    exit /b 1
)

echo.
echo  Done! MonkeFrames was built and installed into:
echo    C:\Program Files (x86)\Steam\steamapps\common\Gorilla Tag\BepInEx\plugins\MonkeFrames
echo  Start Gorilla Tag to use it.
echo.
pause
