@echo off
rem Build a single self-contained exe (no .NET needed on the target machine).
rem Output: dist\Override Mirage Launcher.exe
rem Game data lives next to it: dist\.minecraft

dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o dist

if errorlevel 1 goto :end
if exist "dist\OverrideMirageLauncher.exe" move /Y "dist\OverrideMirageLauncher.exe" "dist\Override Mirage Launcher.exe" >nul
echo Done: dist\Override Mirage Launcher.exe

:end
