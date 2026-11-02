#!/usr/bin/env bash
# Builds a framework-dependent release. NuGet restores the version pinned in the project file.
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

dotnet publish CsharpAspNetCoreChallenge.csproj \
  --configuration Release \
  --runtime linux-x64 \
  --self-contained false \
  --output bin
