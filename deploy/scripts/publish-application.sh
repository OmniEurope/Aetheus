#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Publish application payloads on the host to reuse the solution build; retain agent packaging and
# the EF bundle inside Docker for their platform-specific tooling and apphosts. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
set -eu

fail() {
  echo "FATAL: $*" >&2
  exit 1
}

OUTPUT="${1:?output directory is required}"
# Optional second argument: which half to publish. `all` (the default, and what the CI uses) does
# both; release-fast calls `backend` then `frontend` so it can start the EF-bundle image stage in
# between (see build-fast-release-images.sh). The commands and their outputs are the same either way.
PARTS="${2:-all}"
DOTNET="${DOTNET:?DOTNET is required}"
APP_VERSION="${APP_VERSION:?APP_VERSION is required}"
WORKSPACE="${WORKSPACE:-$(pwd)}"

case "$APP_VERSION" in
  ''|*[!0-9.a-zA-Z_-]*) fail "APP_VERSION contains unexpected characters: $APP_VERSION" ;;
esac
case "$OUTPUT" in
  /*|[A-Za-z]:*) ;;
  *) OUTPUT="$WORKSPACE/$OUTPUT" ;;
esac

case "$PARTS" in
  all) rm -rf "$OUTPUT" ;;
  backend) rm -rf "$OUTPUT/backend" ;;
  frontend) rm -rf "$OUTPUT/frontend" "$OUTPUT/static-server" ;;
  *) fail "Unknown part '$PARTS': expected all, backend or frontend." ;;
esac
mkdir -p "$OUTPUT"

# Every file below is an <EmbeddedResource> of Aetheus.Shared, Aetheus.Back or Aetheus.Agent.Core
# that lives OUTSIDE the project directory, and several are declared with a glob. A glob that matches
# nothing does not fail a build: it embeds nothing, and the application ships without the pipeline
# templates, the Toto fixtures or the scanner manifest it believes it carries. The Dockerfiles used
# to make that impossible by COPYing each one into a partial build context, where a missing file
# failed the COPY. Publishing from a full checkout removes the partial context and, with it, that
# accidental guard - so the requirement is stated here instead of being lost.
require_path() {
  # shellcheck disable=SC2086 # deliberate: the argument may be a glob and must be expanded here.
  set -- $1
  [ -e "$1" ] || fail "Embedded resource input is missing: $1. The publish would silently ship without it."
}
require_path "$WORKSPACE/scanner-manifest.json"
require_path "$WORKSPACE/.aetheus/security-rules/opengrep/aetheus-security.yml"
require_path "$WORKSPACE/deploy/pipelines/toto-conformance-fixture/common"
require_path "$WORKSPACE/deploy/pipelines/toto-conformance-fixture/v1"
require_path "$WORKSPACE/deploy/pipelines/toto-conformance-fixture/v2"
require_path "$WORKSPACE/deploy/pipelines/toto-vulnerable-fixture"
require_path "$WORKSPACE/deploy/pipelines/toto-*.yaml"
require_path "$WORKSPACE/deploy/pipeline-templates/*.yaml"
require_path "$WORKSPACE/deploy/pipeline-templates/generic/*.yaml"
require_path "$WORKSPACE/deploy/scripts/generate-delivery-contract.mjs"
require_path "$WORKSPACE/deploy/scripts/verify-delivery-promotion.mjs"
require_path "$WORKSPACE/deploy/scripts/resolve-injected-nuget-packages.mjs"
require_path "$WORKSPACE/deploy/scripts/generate-artifact-provenance.mjs"
require_path "$WORKSPACE/deploy/scripts/generate-candidate-assurance-contract.mjs"
require_path "$WORKSPACE/deploy/scripts/verify-candidate-assurance-contract.mjs"
require_path "$WORKSPACE/deploy/scripts/zap-active-automation.sh"
require_path "$WORKSPACE/deploy/scripts/publish-observability-package.sh"
require_path "$WORKSPACE/deploy/scripts/promote-observability-packages.sh"
require_path "$WORKSPACE/packages/aetheus-web-analytics/src/aetheus-web-analytics.js"

# Every compile below runs with -p:RunAnalyzers=false. The analyzers' verdict belongs to the CI
# Compile stage, which runs them on this same source; here they only burned CPU on a host that is
# CPU-bound. Measured locally on 2026-09-13: Aetheus.Back Release compile 28 s -> 14 s, and the
# Aetheus.Back.dll and Aetheus.Shared.dll produced are byte-identical (same sha256) - analyzers add
# diagnostics, never IL, and source generators still run.
if [ "$PARTS" != frontend ]; then
echo ">>> Publishing the backend (portable, framework-dependent)"
# No `-r linux-x64`, unlike the image did. A runtime identifier makes restore write a RID section
# into the tracked packages.lock.json files, so running this on a Windows host recorded `win-x64`
# and on Linux `linux-x64`: the publish would leave the repository dirty, differently depending on
# who ran it. The container ran `dotnet Aetheus.Back.dll` and never the apphost, so a portable
# publish is the same application, carries every runtime's native assets under runtimes/, and
# depends on nothing about the machine that produced it.
# Build first, then publish --no-build. A cold `dotnet publish` compiles on its own, and three of them
# in a row compile the shared projects three times over; the header above assumed the pipeline had
# already compiled everything, which is true in the CI and false in release-fast, where this script is
# the first thing that touches obj/. Measured locally from a cold obj/: 134 s as three publishes,
# 111 s as three builds plus three --no-build publishes, same three outputs - except that the
# frontend cannot take --no-build (see below), so only the backend and the static server do.
"$DOTNET" build "$WORKSPACE/src/Aetheus.Back/Aetheus.Back.csproj" \
  -c Release -p:Version="$APP_VERSION" -p:RunAnalyzers=false --nologo
"$DOTNET" publish "$WORKSPACE/src/Aetheus.Back/Aetheus.Back.csproj" \
  -c Release --no-self-contained --no-build \
  -o "$OUTPUT/backend" \
  -p:Version="$APP_VERSION"
mkdir -p "$OUTPUT/backend/wwwroot/downloads"
[ -s "$OUTPUT/backend/Aetheus.Back.dll" ] || fail "The backend publish produced no assembly."
fi

# The EF migrations bundle is NOT built here, and must not be. `dotnet ef migrations bundle
# --runtime linux-x64 --self-contained` produces an apphost for the BUILD host's operating system:
# run on Windows it writes a PE executable that the Linux image happily copies, chmods and then fails
# to execute, with no output and exit 1 (observed 2026-09-07). The image builds it in a Linux
# container, where the answer does not depend on who ran the pipeline.

if [ "$PARTS" != backend ]; then
echo ">>> Publishing the Blazor WebAssembly frontend"
# The frontend is the one publish that must compile on its own. Blazor WebAssembly resolves the
# "#[.{fingerprint}]" asset placeholders of index.html during publish, and only when publish builds:
# a --no-build publish copies the index with the placeholders still in it, whichever property the
# build was given. Run 2247 shipped exactly that and the browser answered "Blazor is not defined".
# Measured locally: build + publish --no-build keeps the placeholders in every variant tried.
# STD-WASMOPT: the WASM optimizer only runs when the wasm-tools workload is installed on the SDK that
# publishes. Without it the publish prints "Publishing without optimizations" and still succeeds, so
# the site ships an unoptimized runtime and nothing fails. Install the workload when it is missing,
# then read the publish log back and refuse a publish that was not optimized.
# The workload also decides the WebAssembly runtime served to browsers, so it is installed from the
# workload set the repository pins (AetheusWasmWorkloadVersion), and reinstalled when the host holds
# another set: "installed once" kept 10.0.12 while the images ran 10.0.13. The Front's
# VerifyBrowserRuntimePack target refuses a publish whose runtime still differs.
WASM_WORKLOAD_VERSION="$(sed -n 's:.*<AetheusWasmWorkloadVersion>\([^<]*\)</AetheusWasmWorkloadVersion>.*:\1:p' "$WORKSPACE/Directory.Build.props")"
[ -n "$WASM_WORKLOAD_VERSION" ] || fail "Directory.Build.props declares no AetheusWasmWorkloadVersion."
if ! "$DOTNET" workload list | grep -qi "^wasm-tools" \
  || [ "$("$DOTNET" workload --version | head -n 1)" != "$WASM_WORKLOAD_VERSION" ]; then
  echo ">>> Installing the wasm-tools workload from workload set $WASM_WORKLOAD_VERSION (STD-WASMOPT)"
  "$DOTNET" workload install wasm-tools --version "$WASM_WORKLOAD_VERSION" \
    || fail "Could not install the wasm-tools workload set $WASM_WORKLOAD_VERSION; the frontend publish would ship unoptimized or another runtime."
fi
FRONTEND_PUBLISH_LOG="$(mktemp)"
# The log is read back below, so the publish goes through tee. `set -e` only sees the LAST command of
# a pipeline, and this script is POSIX sh without pipefail: a failed publish would be hidden by a
# successful tee. The exit code is therefore carried explicitly.
FRONTEND_PUBLISH_STATUS=0
{ "$DOTNET" publish "$WORKSPACE/src/Aetheus.Front/Aetheus.Front.csproj" \
  -c Release \
  -o "$OUTPUT/frontend" \
  -p:StaticWebAssetsPublishFingerprint=false \
  -p:RunAnalyzers=false 2>&1 || echo "PUBLISH_FAILED_$?"; } | tee "$FRONTEND_PUBLISH_LOG"
if grep -q "PUBLISH_FAILED_" "$FRONTEND_PUBLISH_LOG"; then FRONTEND_PUBLISH_STATUS=1; fi
if [ "$FRONTEND_PUBLISH_STATUS" -ne 0 ]; then
  rm -f "$FRONTEND_PUBLISH_LOG"
  fail "The frontend publish failed."
fi
if grep -qi "Publishing without optimizations" "$FRONTEND_PUBLISH_LOG"; then
  rm -f "$FRONTEND_PUBLISH_LOG"
  fail "The frontend publish ran without the WASM optimizer (STD-WASMOPT): install the wasm-tools workload on this host."
fi
rm -f "$FRONTEND_PUBLISH_LOG"
[ -s "$OUTPUT/frontend/wwwroot/index.html" ] || fail "The frontend publish produced no site."

echo ">>> Publishing the static server that hosts it"
# --locked-mode, like the image did: the lock file is what pins this host's dependency set.
#
# -p:Configuration=Release is what makes that true (restore has no -c switch). Directory.Build.props sends every non-Release restore to
# obj/packages.$(Configuration).lock.json so a local Debug build cannot rewrite the tracked,
# Release-canonical lock. A restore with no configuration has an EMPTY $(Configuration), so it was
# validating against obj/packages..lock.json: a file the run generates itself, which pins nothing,
# and which on a developer machine survives from an older package set. It did exactly that on
# 2026-09-20, refusing the publish with NU1004 over a 10.0.11 entry the central manifest had moved
# to 10.0.12 six days earlier. With the property set the pin is the tracked deploy/docker/packages.lock.json,
# which is what the comment above always claimed.
"$DOTNET" restore "$WORKSPACE/deploy/docker/StaticServer.csproj" -p:Configuration=Release --locked-mode
"$DOTNET" build "$WORKSPACE/deploy/docker/StaticServer.csproj" -c Release --no-restore -p:RunAnalyzers=false --nologo
"$DOTNET" publish "$WORKSPACE/deploy/docker/StaticServer.csproj" \
  -c Release -o "$OUTPUT/static-server" --no-restore --no-build --nologo
[ -s "$OUTPUT/static-server/StaticServer.dll" ] || fail "The static server publish produced no assembly."
fi

echo ">>> Host publish ($PARTS) complete in $OUTPUT"
