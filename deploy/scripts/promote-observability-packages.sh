#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Agent-embedded release harness. This file is materialized from the installed agent assembly;
# the source checkout is data only and cannot replace the program that receives release secrets.
set -eu

ROOT="${AETHEUS_WORKING_DIR:-}"
# A run definition from before PLAN-002 C8 still carries one PACKAGE_VERSION for the whole set. It
# is honoured for one cycle as a constraint only: every candidate must then carry exactly it.
LEGACY_SET_VERSION="${PACKAGE_VERSION:-}"
unset PACKAGE_VERSION
PUBLISH_SCRIPT="${AETHEUS_TRUSTED_PUBLISH_SCRIPT:-}"
case "$LEGACY_SET_VERSION" in *[!0-9A-Za-z.+-]*) echo "PACKAGE_VERSION is invalid." >&2; exit 2 ;; esac
test -d "$ROOT" || { echo "AETHEUS_WORKING_DIR is invalid." >&2; exit 2; }
test -f "$PUBLISH_SCRIPT" || { echo "Trusted publication harness is unavailable." >&2; exit 2; }

# Each package is published under its own version (PLAN-002 C8): the one its child pipeline read from
# the package's csproj or package.json and attested as packageVersion in the candidate's
# provenance.json. One package's version is never applied to another.
candidate_version()
{
  PROVENANCE="$ROOT/.package-candidate/$1/provenance.json"
  test -f "$PROVENANCE" || { echo "The $1 candidate has no provenance.json." >&2; return 2; }
  CANDIDATE_VERSION="$(sed -n \
    's/^[[:space:]]*"packageVersion"[[:space:]]*:[[:space:]]*"\([^"]*\)".*$/\1/p' "$PROVENANCE")"
  case "$CANDIDATE_VERSION" in ''|*[!0-9A-Za-z.+-]*)
    echo "The $1 candidate does not attest one valid packageVersion." >&2; return 2 ;;
  esac
  if [ -n "$LEGACY_SET_VERSION" ] && [ "$CANDIDATE_VERSION" != "$LEGACY_SET_VERSION" ]; then
    echo "The $1 candidate is $CANDIDATE_VERSION, not the requested PACKAGE_VERSION." >&2; return 2
  fi
  printf '%s\n' "$CANDIDATE_VERSION"
}
TELEMETRY_VERSION="$(candidate_version telemetry)"
WEB_ANALYTICS_DOTNET_VERSION="$(candidate_version web-analytics-dotnet)"
WEB_ANALYTICS_BROWSER_VERSION="$(candidate_version web-analytics-browser)"
version_of()
{
  case "$1" in
    telemetry) printf '%s' "$TELEMETRY_VERSION" ;;
    web-analytics-dotnet) printf '%s' "$WEB_ANALYTICS_DOTNET_VERSION" ;;
    web-analytics-browser) printf '%s' "$WEB_ANALYTICS_BROWSER_VERSION" ;;
  esac
}
echo "Release set: Aetheus.Telemetry $TELEMETRY_VERSION, Aetheus.WebAnalytics $WEB_ANALYTICS_DOTNET_VERSION," \
  "@aetheus/web-analytics $WEB_ANALYTICS_BROWSER_VERSION."
test -n "${AETHEUS_NUGET_SIGNING_PFX_BASE64:-}" || {
  echo "AETHEUS_NUGET_SIGNING_PFX_BASE64 is required." >&2; exit 1;
}
test -n "${AETHEUS_NUGET_SIGNING_PFX_PASSWORD:-}" || {
  echo "AETHEUS_NUGET_SIGNING_PFX_PASSWORD is required." >&2; exit 1;
}
test -n "${AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT:-}" || {
  echo "AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT is required." >&2; exit 1;
}
EXPECTED_FINGERPRINT="$(printf '%s' "$AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT" \
  | tr '[:lower:]' '[:upper:]')"
case "$EXPECTED_FINGERPRINT" in *[!0-9A-F]*)
  echo "AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT must be hexadecimal." >&2; exit 1 ;;
esac
test "${#EXPECTED_FINGERPRINT}" -eq 64 || {
  echo "AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT must contain 64 characters." >&2; exit 1;
}

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null
command -v openssl >/dev/null
# Resolve the pinned Node runtime from the agent's installation when PATH has none because this
# embedded harness must not execute checkout scripts. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
if ! command -v node >/dev/null 2>&1; then
  for NODE_BIN_DIR in "${HOME:-${AETHEUS_AGENT_WORK_DIRECTORY:?HOME or AETHEUS_AGENT_WORK_DIRECTORY is required}}"/.aetheus/node-v*/bin
  do
    [ -x "$NODE_BIN_DIR/node" ] || continue
    PATH="$NODE_BIN_DIR:$PATH"
    export PATH
    break
  done
fi
command -v node >/dev/null
SECRET_DIR="$(mktemp -d)"
cleanup() { rm -rf "$SECRET_DIR"; }
trap cleanup EXIT HUP INT TERM
umask 077
printf '%s' "$AETHEUS_NUGET_SIGNING_PFX_BASE64" | base64 -d > "$SECRET_DIR/input.pfx"

# PKCS#12 containers are routinely written with the format's historical ciphers (RC2-40-CBC and
# friends). OpenSSL 3 moved those to the legacy provider, so the default read fails with
#   Error outputting keys and certificates
#   ...:unsupported:...:Algorithm (RC2-40-CBC : 0)
# and the whole promotion dies on the container's transport encoding, not on anything about the key.
# Retry with -legacy, and ONLY for that: a wrong password, a corrupt file or any other openssl
# complaint still fails on the first attempt. This weakens nothing - the certificate is pinned by
# SHA-256 fingerprint a few lines below, and the packages are signed with RSA/SHA-256 either way.
read_signing_material() {
    if openssl pkcs12 -in "$SECRET_DIR/input.pfx" \
        -passin env:AETHEUS_NUGET_SIGNING_PFX_PASSWORD -nodes \
        -out "$SECRET_DIR/signing.pem" 2> "$SECRET_DIR/openssl.err"
    then
        return 0
    fi
    if ! grep -q "unsupported" "$SECRET_DIR/openssl.err"; then
        cat "$SECRET_DIR/openssl.err" >&2
        echo "The signing container could not be opened; not retrying." >&2
        return 1
    fi
    echo ">>> The signing container uses a legacy PKCS#12 cipher; reopening it with OpenSSL's legacy provider."
    openssl pkcs12 -in "$SECRET_DIR/input.pfx" \
        -passin env:AETHEUS_NUGET_SIGNING_PFX_PASSWORD -nodes -legacy \
        -out "$SECRET_DIR/signing.pem"
}
read_signing_material

# Re-exported with explicit modern PKCS#12 encryption so a legacy input is never propagated into the
# container `dotnet nuget sign` consumes.
openssl pkcs12 -export -in "$SECRET_DIR/signing.pem" -out "$SECRET_DIR/signing.pfx" -passout pass: \
  -certpbe AES-256-CBC -keypbe AES-256-CBC -macalg sha256
ACTUAL_FINGERPRINT="$(openssl x509 -in "$SECRET_DIR/signing.pem" -noout -fingerprint -sha256)"
ACTUAL_FINGERPRINT="${ACTUAL_FINGERPRINT#*=}"
ACTUAL_FINGERPRINT="$(printf '%s' "$ACTUAL_FINGERPRINT" | tr -d ':' | tr '[:lower:]' '[:upper:]')"
test "$ACTUAL_FINGERPRINT" = "$EXPECTED_FINGERPRINT" || {
  echo "The signing certificate does not match the pinned fingerprint." >&2; exit 1;
}
TRUST_CONFIG="$SECRET_DIR/NuGet.Config"
cat > "$TRUST_CONFIG" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <trustedSigners>
    <author name="Aetheus Internal Package Signing">
      <certificate fingerprint="$EXPECTED_FINGERPRINT" hashAlgorithm="SHA256" allowUntrustedRoot="true" />
    </author>
  </trustedSigners>
</configuration>
EOF
unset AETHEUS_NUGET_SIGNING_PFX_BASE64 AETHEUS_NUGET_SIGNING_PFX_PASSWORD \
  AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT

verify_internal_nuget_signature()
{
  PACKAGE="$1"
  VERIFY_LOG="$SECRET_DIR/nuget-verify.log"
  if DOTNET_CLI_UI_LANGUAGE=en-US "$DOTNET" nuget verify "$PACKAGE" --all \
    --certificate-fingerprint "$EXPECTED_FINGERPRINT" \
    --configfile "$TRUST_CONFIG" > "$VERIFY_LOG" 2>&1
  then
    rm -f "$VERIFY_LOG"
    return 0
  fi

  # dotnet nuget verify on Linux validates against the SDK code-signing root bundle before
  # applying trustedSigners. A deliberately self-signed, fingerprint-pinned internal author
  # therefore exits 1 with only NU3018/NU3042 even when the CMS signature is otherwise valid.
  # Accept exactly that fail-closed condition; every other verification diagnostic remains fatal.
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

sign_candidate()
{
  KIND="$1"
  ID="$2"
  OUT="$ROOT/.package-candidate/$KIND"
  PACKAGE="$OUT/$ID.$(version_of "$KIND").nupkg"
  test -f "$PACKAGE"
  (cd "$OUT" && sha256sum --check SHA256SUMS)
  "$DOTNET" nuget sign "$PACKAGE" \
    --certificate-path "$SECRET_DIR/signing.pfx" \
    --certificate-password "" \
    --timestamper "http://timestamp.digicert.com"
  verify_internal_nuget_signature "$PACKAGE"
}

sign_candidate telemetry Aetheus.Telemetry
sign_candidate web-analytics-dotnet Aetheus.WebAnalytics

# Signing changes the NuGet bytes. Rebuild the attestation and checksums in the trusted boundary
# before any registry mutation, preserving the candidate's source identity.
for KIND in telemetry web-analytics-dotnet
do
  OUT="$ROOT/.package-candidate/$KIND"
  node - "$OUT/provenance.json" <<'NODE'
const fs = require("node:fs");
const crypto = require("node:crypto");
const [output] = process.argv.slice(2);
const directory = output.slice(0, output.lastIndexOf("/"));
const provenance = JSON.parse(fs.readFileSync(output, "utf8"));
provenance.artifacts = fs.readdirSync(directory)
  .filter(name => /\.(nupkg|tgz|js|sri|json)$/.test(name) && name !== "provenance.json")
  .sort()
  .map(name => ({
    name,
    sha256: crypto.createHash("sha256")
      .update(fs.readFileSync(`${directory}/${name}`)).digest("hex")
  }));
fs.writeFileSync(output, `${JSON.stringify(provenance, null, 2)}\n`);
NODE
  (cd "$OUT" && find . -maxdepth 1 -type f \
    ! -name SHA256SUMS ! -name source-commit -print \
    | sort | while IFS= read -r file; do sha256sum "$file"; done > SHA256SUMS)
done
(cd "$ROOT/.package-candidate/web-analytics-browser" && sha256sum --check SHA256SUMS)

# Conflict/readability preflight the complete release set before the first write. The registry
# cannot provide a cross-protocol transaction, so interruption recovery is a convergent saga:
# immutable identical versions are accepted and conflicting bytes always fail closed.
for KIND in telemetry web-analytics-dotnet web-analytics-browser
do
  AETHEUS_PUBLISH_PREFLIGHT_ONLY=true PACKAGE_VERSION="$(version_of "$KIND")" \
    AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT="$EXPECTED_FINGERPRINT" \
    WORKSPACE="$ROOT" DOTNET="$DOTNET" \
    sh "$PUBLISH_SCRIPT" "$KIND"
done
for KIND in telemetry web-analytics-dotnet web-analytics-browser
do
  PACKAGE_VERSION="$(version_of "$KIND")" \
    AETHEUS_NUGET_SIGNING_CERTIFICATE_FINGERPRINT="$EXPECTED_FINGERPRINT" \
    WORKSPACE="$ROOT" DOTNET="$DOTNET" sh "$PUBLISH_SCRIPT" "$KIND"
done
unset EXPECTED_FINGERPRINT
echo "The complete optional-observability release set is published and byte-verified."
