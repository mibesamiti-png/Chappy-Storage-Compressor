@echo off
setlocal
cd /d "%~dp0"
echo Chappy Storage Compressor - G5-04 human WPF acceptance (synthetic files only)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0G5-04-WPF-Acceptance.ps1" -StartApp
if errorlevel 1 (
  echo [FAIL] Safe acceptance fixture preparation failed. No originals were modified.
) else (
  echo [INFO] G5-04 is NOT automatically approved. Follow the generated checklist.
)
pause
