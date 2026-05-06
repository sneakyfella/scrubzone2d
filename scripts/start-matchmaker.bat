@echo off
echo Starting ScrubZone 2D Matchmaking Service...
echo.

set MATCHMAKER_DIR=%~dp0..\matchmaker\MatchmakingService

if not exist "%MATCHMAKER_DIR%" (
    echo ERROR: matchmaker submodule not found at %MATCHMAKER_DIR%
    echo Run:  git submodule update --init --recursive
    pause
    exit /b 1
)

cd /d "%MATCHMAKER_DIR%"

echo Using in-memory store (no Redis required for local play)
echo Service will start on http://localhost:5000
echo Press Ctrl+C to stop.
echo.

set Jwt__Key=scrubzone2d-dev-secret-key-minimum-32chars
set Jwt__Issuer=scrubzone2d
set Jwt__Audience=scrubzone2d
set ASPNETCORE_ENVIRONMENT=Development
set ASPNETCORE_URLS=http://localhost:5000
set Matchmaking__Coop__MaxPlayersPerSession=2
set Matchmaking__Coop__FillTimeoutSeconds=120

dotnet run --no-launch-profile
