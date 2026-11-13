@echo off
setlocal
cd /d "%~dp0"
where dotnet >nul 2>nul
if errorlevel 1 (
  echo [ERROR] Please install .NET 10 SDK first.
  pause
  exit /b 1
)
echo Running isolated temporary-folder smoke tests (Windows NTFS only)...
dotnet run -c Release --project "tests\ChappyStorageCompressor.SmokeTests.csproj"
set RESULT=%ERRORLEVEL%
echo.
if %RESULT% EQU 0 (echo [PASS] All tests passed.) else (echo [FAIL] Review the errors above.)
pause
exit /b %RESULT%
