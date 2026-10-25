#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2

# Resolve the exact baseline SDK declared by global.json for pipeline evidence.
# Developer workstations may use global.json's stable .NET 10 feature-band
# fallback, but agents must not silently change SDK when their host is upgraded.
# Install the baseline in a version-isolated, non-root cache when the host does
# not resolve that exact SDK, and print only the executable path on stdout.

set -eu

DOTNET_INSTALLER_URL="https://raw.githubusercontent.com/dotnet/install-scripts/da3ce11ba63f3dbb0fb835d41bda2665d5c48e84/src/dotnet-install.sh"
DOTNET_INSTALLER_SHA256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"

GLOBAL_JSON="${1:-global.json}"
if [ ! -s "$GLOBAL_JSON" ]; then
    echo "Pinned SDK manifest not found: $GLOBAL_JSON" >&2
    exit 1
fi
GLOBAL_JSON_DIR="$(CDPATH= cd -- "$(dirname -- "$GLOBAL_JSON")" && pwd)"

SDK_VERSION="$(sed -n 's/^[[:space:]]*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$GLOBAL_JSON" | head -n 1)"
if ! printf '%s\n' "$SDK_VERSION" | grep -Eq '^[0-9]+\.[0-9]+\.[0-9]+$'; then
    echo "Pinned SDK version is missing or invalid in $GLOBAL_JSON" >&2
    exit 1
fi

resolves_exact_sdk() {
    [ "$(cd "$GLOBAL_JSON_DIR" && "$1" --version 2>/dev/null)" = "$SDK_VERSION" ]
}

if command -v dotnet >/dev/null 2>&1; then
    SYSTEM_DOTNET="$(command -v dotnet)"
    if resolves_exact_sdk "$SYSTEM_DOTNET"; then
        printf '%s\n' "$SYSTEM_DOTNET"
        exit 0
    fi
fi

: "${HOME:?HOME is required to install the pinned .NET SDK without root}"
INSTALL_ROOT="${AETHEUS_DOTNET_ROOT:-$HOME/.aetheus/dotnet}"
INSTALL_DIR="$INSTALL_ROOT/$SDK_VERSION"
LOCAL_DOTNET="$INSTALL_DIR/dotnet"
if [ -x "$LOCAL_DOTNET" ] && resolves_exact_sdk "$LOCAL_DOTNET"; then
    printf '%s\n' "$LOCAL_DOTNET"
    exit 0
fi

if ! command -v curl >/dev/null 2>&1; then
    echo "curl is required to install .NET SDK $SDK_VERSION" >&2
    exit 1
fi
if ! command -v bash >/dev/null 2>&1; then
    echo "bash is required to run the official .NET SDK installer" >&2
    exit 1
fi
if ! command -v sha256sum >/dev/null 2>&1; then
    echo "sha256sum is required to verify the official .NET SDK installer" >&2
    exit 1
fi

mkdir -p "$INSTALL_DIR"
INSTALLER="$(mktemp)"
cleanup() { rm -f "$INSTALLER"; }
trap cleanup EXIT HUP INT TERM

echo "Installing pinned .NET SDK $SDK_VERSION in $INSTALL_DIR" >&2
curl -fsSL --retry 3 --retry-delay 2 --connect-timeout 15 \
    "$DOTNET_INSTALLER_URL" -o "$INSTALLER"
printf '%s  %s\n' "$DOTNET_INSTALLER_SHA256" "$INSTALLER" | sha256sum -c - >/dev/null
bash "$INSTALLER" --version "$SDK_VERSION" --install-dir "$INSTALL_DIR" --no-path >/dev/null

if [ ! -x "$LOCAL_DOTNET" ] || ! resolves_exact_sdk "$LOCAL_DOTNET"; then
    echo "Pinned .NET SDK $SDK_VERSION installation could not be verified" >&2
    exit 1
fi

printf '%s\n' "$LOCAL_DOTNET"
