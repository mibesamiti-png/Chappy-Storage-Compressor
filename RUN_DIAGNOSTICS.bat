@echo off
setlocal EnableExtensions
cd /d "%~dp0"
if not exist "diagnostics" mkdir "diagnostics"
set "LOGDIR=%CD%\diagnostics"
echo ================================================
echo Chappy Storage Compressor R2.6 - Windows diagnostics
 echo Never select your original media as a test input.
echo ================================================
where dotnet >nul 2>&1
if errorlevel 1 (
  echo ERROR: .NET SDK not found. Install .NET 10 SDK for Windows.
  echo ERROR: .NET SDK not found. > "%LOGDIR%\RESULT.txt"
  exit /b 2
)
dotnet --info > "%LOGDIR%\dotnet-info.txt" 2>&1
if errorlevel 1 goto failSdk
rem G4-02: the exact official CI source-built SIMD jpegtran fingerprint is
rem compiled INTO the normal WPF assembly. A normal unpinned local build
rem refuses to run unknown jpegtran.exe rather than trusting its basename.
if defined CHAPPY_R217_SIMD_JPEGTRAN if not defined CHAPPY_R217_SIMD_SHA256 goto failToolPin
if defined CHAPPY_R217_SIMD_SHA256 set "ChappyTrustedJpegtranSha256=%CHAPPY_R217_SIMD_SHA256%"

echo [0/4] Checking Python references if Python is available...
where python >nul 2>&1
if not errorlevel 1 (
  python tools\static_verify.py > "%LOGDIR%\static-checks.log" 2>&1
  if errorlevel 1 goto failStatic
  python tools\r24_contract_check.py >> "%LOGDIR%\static-checks.log" 2>&1
  if errorlevel 1 goto failStatic
  python tools\r24_reference_check.py >> "%LOGDIR%\static-checks.log" 2>&1
  if errorlevel 1 goto failStatic
  python tools\r25_contract_check.py >> "%LOGDIR%\static-checks.log" 2>&1
  if errorlevel 1 goto failStatic
  python tools\r25_reference_check.py >> "%LOGDIR%\static-checks.log" 2>&1
  if errorlevel 1 goto failStatic
  python tools\r26_contract_check.py >> "%LOGDIR%\static-checks.log" 2>&1
  if errorlevel 1 goto failStatic
  python tools\r26_reference_check.py >> "%LOGDIR%\static-checks.log" 2>&1
  if errorlevel 1 goto failStatic
) else (
  echo SKIPPED: Python not installed; independent reference checks were NOT run. > "%LOGDIR%\static-checks.log"
)
echo [1/4] Building Windows WPF application...
dotnet build "ChappyStorageCompressor.csproj" -c Release --nologo > "%LOGDIR%\build.log" 2>&1
if errorlevel 1 goto failBuild

echo [2/4] Running isolated synthetic safety tests...
dotnet run -c Release --project "tests\ChappyStorageCompressor.SmokeTests.csproj" > "%LOGDIR%\smoke-tests.log" 2>&1
if errorlevel 1 goto failTests

echo [3/4] Exercising WPF routed Scan/Stop button events on synthetic media...
dotnet run -c Release --project "tests\ChappyStorageCompressor.UiSmokeTests.csproj" > "%LOGDIR%\wpf-routed-click.log" 2>&1
if errorlevel 1 goto failUiTests

echo [4/4] Publishing self-contained Windows executable...
dotnet publish "ChappyStorageCompressor.csproj" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:Version=1.0.0-rc.1 -o "dist\win-x64" > "%LOGDIR%\publish.log" 2>&1
if errorlevel 1 goto failPublish
rem Copy EXACTLY the source-verified tool whose SHA was embedded into the app.
rem The CI workflow copies the pinned upstream license and audits this binary.
if defined CHAPPY_R217_SIMD_SHA256 (
  if not defined CHAPPY_R217_SIMD_JPEGTRAN goto failToolPin
  copy /Y "%CHAPPY_R217_SIMD_JPEGTRAN%" "dist\win-x64\jpegtran.exe" >nul
  if errorlevel 1 goto failPublish
  powershell -NoProfile -NonInteractive -Command "$s=(Get-FileHash -LiteralPath 'dist\win-x64\jpegtran.exe' -Algorithm SHA256).Hash; if ($s -ne $env:CHAPPY_R217_SIMD_SHA256) { exit 9 }"
  if errorlevel 1 goto failToolPin
  pwsh -NoLogo -NoProfile -NonInteractive -File "bootstrap\WriteV1ReleaseCandidate.ps1"
  if errorlevel 1 goto failPublish
)

echo PASS: Build, synthetic safety tests, routed WPF UI events and publish. > "%LOGDIR%\RESULT.txt"
echo PASS: Build, synthetic safety tests, routed WPF UI events and publish.
echo See diagnostics\ logs, and dist\win-x64 for binaries.
echo IMPORTANT: This does not authorize use on original media.
exit /b 0

:failToolPin
echo FAIL: pinned jpegtran binary SHA-256 missing/mismatched. > "%LOGDIR%\RESULT.txt"
exit /b 9
:failStatic
echo FAIL: Python static/reference checks. Read diagnostics\static-checks.log. > "%LOGDIR%\RESULT.txt"
exit /b 7
:failSdk
echo FAIL: dotnet --info. > "%LOGDIR%\RESULT.txt"
exit /b 3
:failBuild
echo FAIL: Windows build. Read diagnostics\build.log. > "%LOGDIR%\RESULT.txt"
echo Build failed. Read diagnostics\build.log.
exit /b 4
:failTests
echo FAIL: isolated tests. Read diagnostics\smoke-tests.log. > "%LOGDIR%\RESULT.txt"
echo Tests failed. Read diagnostics\smoke-tests.log.
exit /b 5
:failUiTests
echo FAIL: WPF routed Click smoke tests. Read diagnostics\wpf-routed-click.log. > "%LOGDIR%\RESULT.txt"
echo WPF routed UI event test failed. Read diagnostics\wpf-routed-click.log.
exit /b 8

:failPublish
echo FAIL: publish. Read diagnostics\publish.log. > "%LOGDIR%\RESULT.txt"
echo Publish failed. Read diagnostics\publish.log.
exit /b 6
