#!/usr/bin/env bash
# Run once as root on Ubuntu 24.04. Installs the pinned .NET 10 SDK from Microsoft.
set -euo pipefail
export DEBIAN_FRONTEND=noninteractive

SDK_VERSION=10.0.100
INSTALL_DIR=/usr/local/dotnet

apt-get update
apt-get install -y --no-install-recommends ca-certificates curl

if [ ! -x "$INSTALL_DIR/dotnet" ] || ! "$INSTALL_DIR/dotnet" --list-sdks | grep -q "^${SDK_VERSION} "; then
  tmp="$(mktemp)"
  trap 'rm -f "$tmp"' EXIT
  curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$tmp"
  bash "$tmp" --version "$SDK_VERSION" --install-dir "$INSTALL_DIR" --no-path
fi

ln -sfn "$INSTALL_DIR/dotnet" /usr/local/bin/dotnet
export DOTNET_ROOT="$INSTALL_DIR"
dotnet --info
