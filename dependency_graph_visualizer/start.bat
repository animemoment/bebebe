@echo off
cd /d "%~dp0"
if exist "DependencyGraphVisualizer.exe" (
  start "" "DependencyGraphVisualizer.exe"
  exit /b
)
if exist "app.exe" (
  start "" "app.exe"
  exit /b
)
where py >nul 2>nul
if %errorlevel%==0 (
  py -3 app.py
) else (
  python app.py
)
pause
