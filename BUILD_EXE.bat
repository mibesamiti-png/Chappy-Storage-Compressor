@echo off
setlocal
cd /d "%~dp0"
echo ==============================================
echo  Chappy Storage Compressor - EXE Builder
 echo  Windows 10/11 x64 + .NET 10 SDK required
 echo ==============================================
where dotnet >nul 2>nul
if errorlevel 1 (
    echo.
    echo [ERROR] .NET SDK not found.
    echo Please install .NET 10 SDK from https://dotnet.microsoft.com/download/dotnet/10.0
    pause
    exit /b 1
)
echo.
echo Running safety checks before building ...
dotnet run -c Release --project "tests\ChappyStorageCompressor.SmokeTests.csproj"
if errorlevel 1 (
    echo.
    echo [ERROR] Safety tests failed. EXE build cancelled.
    pause
    exit /b 1
)
echo.
echo Building a self-contained Windows EXE ...
dotnet publish "ChappyStorageCompressor.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o "dist\win-x64"
if errorlevel 1 (
    echo.
    echo [ERROR] Build failed. Read the errors above.
    pause
    exit /b 1
)
echo.
echo [OK] EXE: %~dp0dist\win-x64\ChappyStorageCompressor.exe
start "" "%~dp0dist\win-x64"
pause
