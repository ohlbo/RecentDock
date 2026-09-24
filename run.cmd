@echo off
REM Launches the RecentDock panel.
REM
REM Pass --selftest to run a headless scan and print the result instead of
REM showing the window:  run.cmd --selftest

setlocal
set EXE=%~dp0dist\RecentDock.exe

if not exist "%EXE%" (
    echo RecentDock.exe not found at "%EXE%".
    echo Build it first:
    echo     dotnet publish src\RecentDock.App\RecentDock.App.csproj -c Release -r win-x64 --self-contained false -o dist
    exit /b 1
)

"%EXE%" %*
