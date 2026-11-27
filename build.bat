@echo off
setlocal
rem Builds self-contained single-file RoPorter for Windows and Linux into .\build\
rem Usage: build.bat [Configuration]   (default: Release)

cd /d "%~dp0"
set "CONFIG=%~1"
if "%CONFIG%"=="" set "CONFIG=Release"

rem Use the first dotnet that has a .NET 10 SDK (a dotnet on PATH may only have runtimes).
set "DOTNET="
for %%D in (dotnet "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" "%ProgramFiles%\dotnet\dotnet.exe") do (
    if not defined DOTNET (
        %%D --list-sdks 2>nul | findstr /b "10." >nul && set "DOTNET=%%~D"
    )
)
if not defined DOTNET (
    echo The .NET 10 SDK was not found. Install it from https://dotnet.microsoft.com/download/dotnet/10.0
    exit /b 1
)
echo Using "%DOTNET%"

set "OUT=%~dp0build"
set "STAGE=%~dp0obj\publish"
rem Only the two programs are replaced: RoPorter keeps its settings, library and rbx_cache (content cache) beside them.
for %%F in ("%OUT%\RoPorter-Windows-x64.exe" "%OUT%\RoPorter-Linux-x64") do (
    if exist %%F del /f /q %%F 2>nul
    if exist %%F (
        echo Could not replace %%F. Close RoPorter if it is running from the build folder, then try again.
        exit /b 1
    )
)
if exist "%STAGE%" rmdir /s /q "%STAGE%"
if not exist "%OUT%" mkdir "%OUT%"

call :publish win-x64 Windows-x64 || goto :failed
call :publish linux-x64 Linux-x64 || goto :failed

copy /y "%STAGE%\win-x64\RoPorter.exe" "%OUT%\RoPorter-Windows-x64.exe" >nul || goto :failed
copy /y "%STAGE%\linux-x64\RoPorter" "%OUT%\RoPorter-Linux-x64" >nul || goto :failed
rmdir /s /q "%STAGE%"

echo.
echo Build finished. Output:
dir /b "%OUT%"
exit /b 0

:publish
echo.
echo === Publishing %2 (%CONFIG%) ===
"%DOTNET%" publish Rbx2Source.csproj --nologo --configuration %CONFIG% --runtime %1 --output "%STAGE%\%1"
exit /b %errorlevel%

:failed
echo.
echo Build FAILED.
exit /b 1
