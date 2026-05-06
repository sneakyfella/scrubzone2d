#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MATCHMAKER_DIR="$SCRIPT_DIR/../matchmaker/MatchmakingService"

if [ ! -d "$MATCHMAKER_DIR" ]; then
    echo "ERROR: matchmaker submodule not found at $MATCHMAKER_DIR"
    echo "Run:  git submodule update --init --recursive"
    exit 1
fi

echo "Starting ScrubZone 2D Matchmaking Service on http://localhost:5000"
cd "$MATCHMAKER_DIR"

export Jwt__Key="scrubzone2d-dev-secret-key-minimum-32chars"
export Jwt__Issuer="scrubzone2d"
export Jwt__Audience="scrubzone2d"
export ASPNETCORE_ENVIRONMENT="Development"
export ASPNETCORE_URLS="http://localhost:5000"

dotnet run --no-launch-profile
