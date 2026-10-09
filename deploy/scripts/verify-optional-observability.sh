#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

DOTNET="${DOTNET:-dotnet}"
NODE="${NODE:-node}"
ROOT="$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)"
OUTPUT="$(mktemp -d)"
trap 'rm -rf "$OUTPUT"' EXIT HUP INT TERM

"$DOTNET" pack "$ROOT/src/Aetheus.Telemetry/Aetheus.Telemetry.csproj" \
  --configuration Release --output "$OUTPUT/feed" --nologo
"$DOTNET" pack "$ROOT/src/Aetheus.WebAnalytics/Aetheus.WebAnalytics.csproj" \
  --configuration Release --output "$OUTPUT/feed" --nologo
# The normalizer takes the packed files by name, so read each version from its project instead of
# repeating it here: a version bump must not break this gate (run 2633).
package_version()
{
  sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$1" | head -n 1
}
TELEMETRY_PACKAGE_VERSION="$(package_version "$ROOT/src/Aetheus.Telemetry/Aetheus.Telemetry.csproj")"
WEB_ANALYTICS_PACKAGE_VERSION="$(package_version "$ROOT/src/Aetheus.WebAnalytics/Aetheus.WebAnalytics.csproj")"
test -n "$TELEMETRY_PACKAGE_VERSION"
test -n "$WEB_ANALYTICS_PACKAGE_VERSION"
"$DOTNET" restore \
  "$ROOT/deploy/tools/Aetheus.PackageNormalizer/Aetheus.PackageNormalizer.csproj"
"$DOTNET" run \
  --project "$ROOT/deploy/tools/Aetheus.PackageNormalizer/Aetheus.PackageNormalizer.csproj" \
  --configuration Release --no-restore -- \
  "$OUTPUT/feed/Aetheus.Telemetry.$TELEMETRY_PACKAGE_VERSION.nupkg" \
  "$OUTPUT/feed/Aetheus.WebAnalytics.$WEB_ANALYTICS_PACKAGE_VERSION.nupkg"

PROJECT="$ROOT/examples/optional-observability/OptionalObservabilityExample.csproj"
MANIFEST="$ROOT/examples/optional-observability/aetheus.integrations.json"
TELEMETRY_VERSION="$("$NODE" -e '
  const value = require(process.argv[1]);
  process.stdout.write(value.packages.find(item => item.id === "Aetheus.Telemetry").version);
' "$MANIFEST")"
WEB_ANALYTICS_VERSION="$("$NODE" -e '
  const value = require(process.argv[1]);
  process.stdout.write(value.packages.find(item => item.id === "Aetheus.WebAnalytics").version);
' "$MANIFEST")"
test -n "$TELEMETRY_VERSION"
test -n "$WEB_ANALYTICS_VERSION"

# This first restore deliberately knows only nuget.org. It proves that a public/open-source clone
# does not need the private Aetheus feed while both products keep their default disabled value.
"$DOTNET" restore "$PROJECT" \
  --configfile "$ROOT/NuGet.config" --locked-mode \
  --packages "$OUTPUT/packages-none" \
  -p:Configuration=Release \
  -p:NuGetLockFilePath=packages.lock.json \
  -p:BaseIntermediateOutputPath="$OUTPUT/none/obj/"
"$DOTNET" build "$PROJECT" --configuration Release --no-restore \
  -p:BaseOutputPath="$OUTPUT/none/bin/" -p:BaseIntermediateOutputPath="$OUTPUT/none/obj/"

verify_variant()
{
  name="$1"
  telemetry="$2"
  analytics="$3"
  generated_packages="$OUTPUT/packages-$name-generated"
  locked_packages="$OUTPUT/packages-$name-locked"
  lock_file="$OUTPUT/$name/packages.lock.json"
  common="-p:EnableAetheusTelemetry=$telemetry -p:EnableAetheusWebAnalytics=$analytics -p:AetheusTelemetryVersion=$TELEMETRY_VERSION -p:AetheusWebAnalyticsVersion=$WEB_ANALYTICS_VERSION"

  # Candidate packages contain revision- and build-environment-specific metadata, so a committed
  # content hash would reject a valid package built in another checkout. Resolve the normalized
  # candidate into an isolated lock, then prove a fresh restore against that exact lock.
  # shellcheck disable=SC2086
  "$DOTNET" restore "$PROJECT" --configfile "$ROOT/NuGet.config" \
    --packages "$generated_packages" --use-lock-file --force-evaluate \
    -p:RestoreLockedMode=false \
    -p:Configuration=Release \
    -p:NuGetLockFilePath="$lock_file" \
    -p:RestoreAdditionalProjectSources="$OUTPUT/feed" \
    -p:BaseIntermediateOutputPath="$OUTPUT/$name/generated-obj/" $common

  test -s "$lock_file"
  generated_lock_hash="$(sha256sum "$lock_file" | awk '{print $1}')"

  # shellcheck disable=SC2086
  "$DOTNET" restore "$PROJECT" --configfile "$ROOT/NuGet.config" \
    --packages "$locked_packages" --locked-mode \
    -p:Configuration=Release \
    -p:NuGetLockFilePath="$lock_file" \
    -p:RestoreAdditionalProjectSources="$OUTPUT/feed" \
    -p:BaseIntermediateOutputPath="$OUTPUT/$name/locked-obj/" $common

  locked_lock_hash="$(sha256sum "$lock_file" | awk '{print $1}')"
  test "$generated_lock_hash" = "$locked_lock_hash"

  # shellcheck disable=SC2086
  "$DOTNET" build "$PROJECT" --configuration Release --no-restore \
    -p:BaseOutputPath="$OUTPUT/$name/bin/" \
    -p:BaseIntermediateOutputPath="$OUTPUT/$name/locked-obj/" $common
}

verify_variant telemetry true false
verify_variant web-analytics false true
verify_variant both true true
