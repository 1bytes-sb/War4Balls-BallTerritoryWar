@echo off
setlocal
cd /d "%~dp0"

rem Use a full path in GODOT, or find a Godot .NET executable on PATH.
if not defined GODOT (
  for %%G in (godot-mono.exe godot.exe Godot_v4.6.2-stable_mono_win64_console.exe Godot_v4.6.2-stable_mono_win64.exe) do (
    for /f "delims=" %%P in ('where %%G 2^>nul') do if not defined GODOT set "GODOT=%%P"
  )
)
rem Also support the existing local tools directory beside the project.
if not defined GODOT if exist "%~dp0..\tools\godot462mono\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe" (
  set "GODOT=%~dp0..\tools\godot462mono\Godot_v4.6.2-stable_mono_win64\Godot_v4.6.2-stable_mono_win64_console.exe"
)
if not defined GODOT (
  echo Godot .NET was not found. Set GODOT to the full executable path.
  pause
  exit /b 1
)
if not exist "%GODOT%" (
  echo Godot executable not found: "%GODOT%"
  pause
  exit /b 1
)
where dotnet >nul 2>&1
if errorlevel 1 (
  echo Install the .NET 10 SDK and the .NET 9 runtime, then try again.
  pause
  exit /b 1
)

echo Building C# ...
dotnet build BallTerritoryWar.csproj --nologo -v minimal
if errorlevel 1 (
  echo Build failed.
  pause
  exit /b 1
)
rem Pass extra arguments to the game, e.g. run_game.bat --timescale=2
"%GODOT%" --path . -- %*
exit /b %errorlevel%
