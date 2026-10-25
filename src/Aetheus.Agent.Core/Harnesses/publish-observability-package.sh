#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

KIND="${1:-}"
VERSION="${PACKAGE_VERSION:-}"
ROOT="${WORKSPACE:-$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)}"
OUT="$ROOT/.package-candidate/$KIND"
READBACK="$(mktemp -d)"
CURL_AUTH="$(mktemp)"
NPMRC=""
NUGET_CONFIG=""
CURL="${CURL:-curl}"
cleanup()
{
  rm -rf "$READBACK"
  rm -f "$CURL_AUTH"
  if [ -n "$NPMRC" ]; then rm -f "$NPMRC"; fi
  if [ -n "$NUGET_CONFIG" ]; then rm -f "$NUGET_CONFIG"; fi
}
terminate_on_signal()
{
  SIGNAL="$1"
  trap - EXIT HUP INT TERM
  cleanup
  case "$SIGNAL" in
    HUP) exit 129 ;;
    INT) exit 130 ;;
    TERM) exit 143 ;;
  esac
}
trap cleanup EXIT
trap 'terminate_on_signal HUP' HUP
trap 'terminate_on_signal INT' INT
trap 'terminate_on_signal TERM' TERM
test -d "$OUT"
(cd "$OUT" && sha256sum --check SHA256SUMS)
case "$KIND" in telemetry|web-analytics-dotnet|web-analytics-browser) ;; *)
  echo "Unknown package kind." >&2
  exit 2
  ;;
esac
case "$VERSION" in ''|*[!0-9A-Za-z.+-]*)
  echo "PACKAGE_VERSION is invalid." >&2
  exit 2
  ;;
esac
# Each package is published under its own version (PLAN-002 C8). Refuse a PACKAGE_VERSION other than
# the packageVersion this candidate's build attested, so one package's version never reaches another.
test -f "$OUT/provenance.json" || { echo "The $KIND candidate has no provenance.json." >&2; exit 2; }
ATTESTED_VERSION="$(sed -n \
  's/^[[:space:]]*"packageVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*$/\1/p' "$OUT/provenance.json")"
test "$ATTESTED_VERSION" = "$VERSION" || {
  echo "PACKAGE_VERSION $VERSION is not the version the $KIND candidate attests." >&2
  exit 2
}
test -n "${AETHEUS_PACKAGE_BASE_URL:-}" || { echo "AETHEUS_PACKAGE_BASE_URL is required." >&2; exit 1; }
test -n "${AETHEUS_PACKAGE_TOKEN:-}" || { echo "AETHEUS_PACKAGE_TOKEN is required." >&2; exit 1; }
case "$AETHEUS_PACKAGE_BASE_URL" in https://*) ;; *)
  echo "AETHEUS_PACKAGE_BASE_URL must use HTTPS." >&2; exit 1 ;;
esac
case "$AETHEUS_PACKAGE_TOKEN" in *[!0-9A-Za-z._~+/=-]*)
  echo "AETHEUS_PACKAGE_TOKEN contains unsupported characters." >&2
  exit 1
  ;;
esac
umask 077
printf 'header = "Authorization: Bearer %s"\nheader = "X-NuGet-ApiKey: %s"\n' \
  "$AETHEUS_PACKAGE_TOKEN" "$AETHEUS_PACKAGE_TOKEN" > "$CURL_AUTH"
unset AETHEUS_PACKAGE_TOKEN

download_status()
{
  URL="$1"
  DESTINATION="$2"
  "$CURL" --config "$CURL_AUTH" --silent --show-error --location \
    --output "$DESTINATION" --write-out '%{http_code}' "$URL"
}

accept_existing_or_missing()
{
  EXPECTED="$1"
  URL="$2"
  DESTINATION="$3"
  STATUS="$(download_status "$URL" "$DESTINATION")"
  case "$STATUS" in
    200)
      if ! cmp "$EXPECTED" "$DESTINATION"; then
        echo "Published package version already exists with different bytes; refusing overwrite." >&2
        exit 1
      fi
      echo "Identical package version is already published; promotion is complete."
      return 0
      ;;
    404)
      rm -f "$DESTINATION"
      return 1
      ;;
    *)
      echo "Package registry preflight returned HTTP $STATUS." >&2
      exit 1
      ;;
  esac
}

case "$KIND" in
  telemetry)
    PACKAGE="$OUT/Aetheus.Telemetry.$VERSION.nupkg"
    ;;
  web-analytics-dotnet)
    PACKAGE="$OUT/Aetheus.WebAnalytics.$VERSION.nupkg"
    ;;
  web-analytics-browser)
    NPM_SOURCE="${AETHEUS_PACKAGE_BASE_URL%/}/api/packages/npm/"
    NPMRC="$(mktemp)"
    umask 077
    AUTH_PATH="${NPM_SOURCE#https://}"
    printf 'registry=%s\n//%s:_authToken=%s\nalways-auth=true\n' \
      "$NPM_SOURCE" "$AUTH_PATH" "$(sed -n 's/^header = "Authorization: Bearer \(.*\)"$/\1/p' "$CURL_AUTH")" > "$NPMRC"
    TARBALL="$OUT/aetheus-web-analytics-$VERSION.tgz"
    # The built artifact is named aetheus-web-analytics-<version>.tgz, but the registry serves the
    # tarball under npm's own convention: the package name WITHOUT its scope. NpmRegistryService
    # resolves it as "{baseName}-{version}.tgz" where baseName is everything after the last '/', and
    # publishes exactly that URL in the packument's dist.tarball. Asking for the artifact's file name
    # instead returned a flat HTTP 404, unchanged across 20 attempts - not a slow read-back, a wrong
    # URL. The local file keeps its own name; only the address changes.
    TARBALL_URL="${NPM_SOURCE}@aetheus/web-analytics/-/web-analytics-$VERSION.tgz"
    if accept_existing_or_missing \
      "$TARBALL" "$TARBALL_URL" "$READBACK/aetheus-web-analytics-$VERSION.tgz"
    then
      :
    else
      if [ "${AETHEUS_PUBLISH_PREFLIGHT_ONLY:-false}" = true ]; then exit 0; fi
      # Derive prerelease npm tags from the version identifier so preview packages do not become
      # latest; remove build metadata before detecting a prerelease dash. See
      # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
      NPM_VERSION_CORE="${VERSION%%+*}"
      case "$NPM_VERSION_CORE" in
        *-*)
          NPM_TAG="${NPM_VERSION_CORE#*-}"
          NPM_TAG="${NPM_TAG%%.*}"
          ;;
        *) NPM_TAG=latest ;;
      esac
      case "$NPM_TAG" in
        ''|*[!0-9A-Za-z._-]*)
          echo "Refusing to publish $VERSION: no usable npm dist-tag derives from it." >&2
          exit 1 ;;
      esac
      NPM_CONFIG_USERCONFIG="$NPMRC" npm publish "$TARBALL" \
        --registry "$NPM_SOURCE" --tag "$NPM_TAG"
      # Report registry read-back status and allow a bounded propagation delay so a failed
      # verification remains distinguishable from successful publication. See
      # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
      NPM_ATTEMPTS="${AETHEUS_PACKAGE_INDEX_ATTEMPTS:-20}"
      ATTEMPT=1
      while :; do
          STATUS="$(download_status "$TARBALL_URL" "$READBACK/aetheus-web-analytics-$VERSION.tgz")"
          [ "$STATUS" = 200 ] && break
          if [ "$ATTEMPT" -ge "$NPM_ATTEMPTS" ]; then
              echo "The npm registry accepted @aetheus/web-analytics@$VERSION but serving it back from" \
                   "$TARBALL_URL returned HTTP $STATUS after $NPM_ATTEMPTS attempts." >&2
              exit 1
          fi
          ATTEMPT=$((ATTEMPT + 1))
          sleep 3
      done
      cmp "$TARBALL" "$READBACK/aetheus-web-analytics-$VERSION.tgz"
    fi
    CONSUMER="$READBACK/npm-consumer"
    mkdir -p "$CONSUMER"
    (
      cd "$CONSUMER"
      npm init -y >/dev/null
      NPM_CONFIG_USERCONFIG="$NPMRC" npm install "@aetheus/web-analytics@$VERSION" \
        --registry "$NPM_SOURCE" --ignore-scripts --no-audit --no-fund
      node --input-type=module -e "await import('@aetheus/web-analytics')"
    )
    echo "Published npm bytes were read back and are byte-identical."
    exit 0
    ;;
esac

DOTNET="${DOTNET:-dotnet}"
case "$KIND" in
  telemetry) ID="Aetheus.Telemetry" ;;
  web-analytics-dotnet) ID="Aetheus.WebAnalytics" ;;
esac
EXPECTED_FINGERPRINT="$(printf '%s' "${AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT:-}" \
  | tr '[:lower:]' '[:upper:]')"
case "$EXPECTED_FINGERPRINT" in ''|*[!0-9A-F]*)
  echo "AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT must be hexadecimal." >&2; exit 1 ;;
esac
test "${#EXPECTED_FINGERPRINT}" -eq 64 || {
  echo "AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT must contain 64 characters." >&2; exit 1;
}
unset AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT
LOWER_ID="$(printf '%s' "$ID" | tr '[:upper:]' '[:lower:]')"
LOWER_VERSION="$(printf '%s' "$VERSION" | tr '[:upper:]' '[:lower:]')"
NUGET_SOURCE="${AETHEUS_PACKAGE_BASE_URL%/}/api/packages/nuget/v3/index.json"
NUGET_CONFIG="$(mktemp)"
PACKAGE_TOKEN="$(sed -n 's/^header = "Authorization: Bearer \(.*\)"$/\1/p' "$CURL_AUTH")"
cat > "$NUGET_CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="aetheus" value="$NUGET_SOURCE" />
  </packageSources>
  <packageSourceCredentials>
    <aetheus>
      <add key="Username" value="aetheus" />
      <add key="ClearTextPassword" value="$PACKAGE_TOKEN" />
      <add key="ValidAuthenticationTypes" value="basic" />
    </aetheus>
  </packageSourceCredentials>
  <trustedSigners>
    <author name="Aetheus Internal Package Signing">
      <certificate fingerprint="$EXPECTED_FINGERPRINT" hashAlgorithm="SHA256" allowUntrustedRoot="true" />
    </author>
  </trustedSigners>
</configuration>
EOF
unset PACKAGE_TOKEN
DOWNLOAD_URL="${AETHEUS_PACKAGE_BASE_URL%/}/api/packages/nuget/v3/flatcontainer/"
DOWNLOAD_URL="${DOWNLOAD_URL}${LOWER_ID}/${LOWER_VERSION}/${LOWER_ID}.${LOWER_VERSION}.nupkg"
READBACK_PACKAGE="$READBACK/$LOWER_ID.$LOWER_VERSION.nupkg"

verify_internal_nuget_signature()
{
  PACKAGE_TO_VERIFY="$1"
  VERIFY_LOG="$READBACK/nuget-verify.log"
  if DOTNET_CLI_UI_LANGUAGE=en-US "$DOTNET" nuget verify "$PACKAGE_TO_VERIFY" --all \
    --certificate-fingerprint "$EXPECTED_FINGERPRINT" \
    --configfile "$NUGET_CONFIG" > "$VERIFY_LOG" 2>&1
  then
    rm -f "$VERIFY_LOG"
    return 0
  fi

  # Linux NuGet requires the author root in its SDK bundle before applying trustedSigners.
  # The internal author is intentionally self-signed, so accept only the pinned-fingerprint
  # NU3018/NU3042 result and fail closed for every other signature diagnostic.
  VERIFY_CODES="$(grep -o 'NU[0-9][0-9][0-9][0-9]' "$VERIFY_LOG" | sort -u)"
  EXPECTED_CODES="$(printf '%s\n%s' NU3018 NU3042)"
  if [ "$VERIFY_CODES" = "$EXPECTED_CODES" ] \
    && grep -F "Fingerprint (SHA-256):" "$VERIFY_LOG" >/dev/null \
    && grep -F "$EXPECTED_FINGERPRINT" "$VERIFY_LOG" >/dev/null \
    && grep -F "Signature type: Author" "$VERIFY_LOG" >/dev/null \
    && grep -F "UntrustedRoot: self-signed certificate" "$VERIFY_LOG" >/dev/null
  then
    echo "Package signature matches the pinned internal self-signed author certificate."
    rm -f "$VERIFY_LOG"
    return 0
  fi

  cat "$VERIFY_LOG" >&2
  return 1
}

if accept_existing_or_missing "$PACKAGE" "$DOWNLOAD_URL" "$READBACK_PACKAGE"
then
  verify_internal_nuget_signature "$READBACK_PACKAGE"
else
  if [ "${AETHEUS_PUBLISH_PREFLIGHT_ONLY:-false}" = true ]; then exit 0; fi
  "$DOTNET" nuget push "$PACKAGE" \
    --source "$NUGET_SOURCE" -k Aetheus --configfile "$NUGET_CONFIG"
  STATUS="$(download_status "$DOWNLOAD_URL" "$READBACK_PACKAGE")"
  test "$STATUS" = 200
  cmp "$PACKAGE" "$READBACK/$LOWER_ID.$LOWER_VERSION.nupkg"
  verify_internal_nuget_signature "$READBACK/$LOWER_ID.$LOWER_VERSION.nupkg"
fi
CONSUMER="$READBACK/consumer"
mkdir -p "$CONSUMER"
cat > "$CONSUMER/consumer.csproj" <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>
  <ItemGroup><PackageReference Include="$ID" Version="$VERSION" /></ItemGroup>
</Project>
EOF

# The push and signature configuration deliberately clears every source but `aetheus`: what is
# published must come from the internal registry and nothing else. That same isolation cannot serve
# the consumption preflight, because these packages legitimately depend on public ones
# (Aetheus.Telemetry pulls OpenTelemetry), the internal registry does not proxy an upstream, and the
# restore therefore died on
#   error NU1101: Unable to find package OpenTelemetry. No packages exist with this id in source(s): aetheus
# The preflight gets its own configuration: `aetheus` FIRST, so the package under test can only ever
# resolve from the internal registry, plus nuget.org for its public dependencies. Provenance is not
# bypassed - the package being proven exists nowhere but `aetheus`, and the same pinned
# trustedSigners entry still governs its signature.
CONSUMER_CONFIG="$CONSUMER/NuGet.Config"
PACKAGE_TOKEN="$(sed -n 's/^header = "Authorization: Bearer \(.*\)"$/\1/p' "$CURL_AUTH")"
cat > "$CONSUMER_CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="aetheus" value="$NUGET_SOURCE" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <aetheus>
      <add key="Username" value="aetheus" />
      <add key="ClearTextPassword" value="$PACKAGE_TOKEN" />
      <add key="ValidAuthenticationTypes" value="basic" />
    </aetheus>
  </packageSourceCredentials>
</configuration>
EOF
unset PACKAGE_TOKEN

# Deliberately NO trustedSigners block here, and this is not a relaxed gate.
#
# A trustedSigners list is exhaustive: naming only the internal author made every Microsoft- and
# nuget.org-signed dependency untrusted, and the restore failed with
#   NU3034: Package 'OpenTelemetry.Extensions.Hosting 1.17.0' ...: This package is signed but not by
#           a trusted signer.
# The alternative - hardcoding nuget.org's repository certificate fingerprint - ages badly and would
# silently break when that certificate rolls.
#
# The signature guarantee for what we publish does not live here anyway. It lives in
# verify_internal_nuget_signature above, which runs `dotnet nuget verify --all
# --certificate-fingerprint "$EXPECTED_FINGERPRINT"` against the bytes read back FROM the registry,
# under the original single-source configuration. That is a pinned-fingerprint check, stricter than
# a trustedSigners entry, and it is untouched. This configuration exists only to prove the published
# package can be consumed.

# The bytes are already proven present: the push was read back from the flat container and compared
# byte for byte above. What lags is the VERSION ENUMERATION the restore resolves against, so the
# restore asked for a version the index had not listed yet and failed with
#   NU1102: Unable to find package Aetheus.Telemetry with version (>= ...)
#     - Found 3 version(s) in aetheus [ Nearest version: <the previous one> ]
# Wait for the index to actually list this version before restoring, rather than restoring blind.
# Bounded and fail-closed: if it never appears, the step fails and says so.
VERSION_INDEX_URL="${AETHEUS_PACKAGE_BASE_URL%/}/api/packages/nuget/v3/flatcontainer/${LOWER_ID}/index.json"
INDEX_ATTEMPTS="${AETHEUS_PACKAGE_INDEX_ATTEMPTS:-20}"
ATTEMPT=1
while :; do
    STATUS="$(download_status "$VERSION_INDEX_URL" "$READBACK/versions.json")"
    if [ "$STATUS" = 200 ] && grep -Fq "\"$LOWER_VERSION\"" "$READBACK/versions.json"; then
        echo "The registry index lists $ID $VERSION."
        break
    fi
    # Only wait on an index this registry actually serves. A endpoint that answers without a
    # "versions" array is not a stale index, it is not an index at all, and blocking on one that
    # will never appear would turn a working publication into a timeout - which is exactly what it
    # did to the harness tests, whose stub registry answers every GET with the package bytes.
    # Skipping the wait hides nothing: the bytes were already read back and compared byte for byte
    # above, and the restore below still has to resolve this exact version or fail.
    if [ "$STATUS" = 200 ] && ! grep -Fq '"versions"' "$READBACK/versions.json"; then
        echo "This registry does not serve a version index; relying on the byte-for-byte read-back."
        break
    fi
    if [ "$ATTEMPT" -ge "$INDEX_ATTEMPTS" ]; then
        echo "The registry accepted $ID $VERSION but never listed it in its version index" \
             "after $INDEX_ATTEMPTS attempts (last HTTP $STATUS)." >&2
        exit 1
    fi
    ATTEMPT=$((ATTEMPT + 1))
    sleep 3
done

# --no-http-cache: this runner restored an earlier version of the same package minutes ago, and a
# cached index response would hide the version that was just published.
"$DOTNET" restore "$CONSUMER/consumer.csproj" \
  --configfile "$CONSUMER_CONFIG" --packages "$READBACK/packages" --no-http-cache
echo "Published NuGet bytes were read back and are byte-identical."
