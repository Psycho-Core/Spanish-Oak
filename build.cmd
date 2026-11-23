@echo off
rem ---------------------------------------------------------------------------
rem PsychoFlash build helper (Windows)
rem ---------------------------------------------------------------------------
setlocal

where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo  The .NET SDK was not found. Install the .NET 8 SDK:
    echo    https://dotnet.microsoft.com/download
    echo.
    exit /b 1
)

echo === Building PsychoFlash (Release) ===
dotnet build "%~dp0PsychoFlash.sln" -c Release
if errorlevel 1 exit /b 1

echo.
echo === Running self tests ===
dotnet run --project "%~dp0tests\PsychoFlash.SelfTest" -c Release --no-build
if errorlevel 1 exit /b 1

echo.
echo Build complete:
echo   UI  : src\PsychoFlash.App\bin\Release\net8.0-windows\PsychoFlash.exe
echo   UI  : src\PsychoFlash.App\bin\Release\net48\PsychoFlash.exe
echo   CLI : src\PsychoFlash.Cli\bin\Release\net8.0\psychoflash.exe
endlocal
