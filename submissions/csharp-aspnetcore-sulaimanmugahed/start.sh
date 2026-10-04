#!/usr/bin/env bash
# Runs in the foreground. Configuration comes only from SQLITE_PATH, JWT_SECRET, HOST, and PORT.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1
exec dotnet bin/CsharpAspNetCoreChallenge.dll
