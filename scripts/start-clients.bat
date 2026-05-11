@echo off
setlocal enabledelayedexpansion

set EXE=%~dp0..\src\ScrubZone2D\bin\Debug\net9.0\ScrubZone2D.exe

if not exist "%EXE%" (
    echo ERROR: Game executable not found at %EXE%
    echo Run:  dotnet build src\ScrubZone2D -c Debug
    pause
    exit /b 1
)

set COUNT=2
if not "%~1"=="" set COUNT=%~1

echo Starting %COUNT% client(s)...

set IDX=1
:launch
if %IDX% GTR %COUNT% goto done
echo   Launching Player%IDX%
start "" "%EXE%" --name Player%IDX%
set /a IDX+=1
if %IDX% LEQ %COUNT% timeout /t 2 /nobreak >nul
goto launch

:done
echo All clients launched.
