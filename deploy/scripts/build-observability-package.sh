#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

KIND="${1:-}"
VERSION="${PACKAGE_VERSION:-}"
case "$KIND" in telemetry|web-analytics-dotnet|web-analytics-browser) ;; *)
  echo "Usage: build-observability-package.sh <telemetry|web-analytics-dotnet|web-analytics-browser>" >&2
  exit 2
esac
case "$VERSION" in ''|*[!0-9A-Za-z.+-]*) echo "PACKAGE_VERSION is invalid." >&2; exit 2 ;; esac

ROOT="${WORKSPACE:-$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)}"
OUT="$ROOT/.package-candidate/$KIND"
REPRO="$(mktemp -d)"
cleanup() { rm -rf "$REPRO"; }
trap cleanup EXIT HUP INT TERM
rm -rf "$OUT"
mkdir -p "$OUT" "$REPRO"

DOTNET="${DOTNET:-$(sh "$ROOT/deploy/scripts/ensure-dotnet-sdk.sh")}"
# `node` and `npm` are prerequisites of this script. A release build must not depend on whatever the
# host happens to have on PATH: the mirror lost a hand-installed /usr/local/bin/node when its image
# was rebuilt from the repository, and the whole package set stopped building with "node: not found"
# (publisher run 1236). Resolve the same pinned, checksummed runtime the rest of the delivery uses,
# and put its bin directory on PATH so `npm` comes from it too.
NODE="${NODE:-$(sh "$ROOT/deploy/scripts/ensure-node-runtime.sh")}"
PATH="$(dirname "$NODE"):$PATH"
export PATH
COMMIT="$(git -C "$ROOT" rev-parse HEAD)"
SOURCE_DATE_EPOCH="$(git -C "$ROOT" show -s --format=%ct "$COMMIT")"
export SOURCE_DATE_EPOCH

build_nuget()
{
  PROJECT="$1"
  ID="$2"
  EXTRA="$3"
  for DESTINATION in "$OUT" "$REPRO"
  do
    # EXTRA is a fixed, source-controlled MSBuild option, never user input.
    # shellcheck disable=SC2086
    "$DOTNET" pack "$ROOT/$PROJECT" --configuration Release --output "$DESTINATION" \
      -p:PackageVersion="$VERSION" -p:ContinuousIntegrationBuild=true $EXTRA
  done
  "$DOTNET" restore \
    "$ROOT/deploy/tools/Aetheus.PackageNormalizer/Aetheus.PackageNormalizer.csproj"
  "$DOTNET" run --project "$ROOT/deploy/tools/Aetheus.PackageNormalizer/Aetheus.PackageNormalizer.csproj" \
    --configuration Release --no-restore -- \
    "$OUT/$ID.$VERSION.nupkg" "$REPRO/$ID.$VERSION.nupkg"
  cmp "$OUT/$ID.$VERSION.nupkg" "$REPRO/$ID.$VERSION.nupkg"

  "$DOTNET" list "$ROOT/$PROJECT" package --include-transitive --format json > "$OUT/dependencies.json"
  node "$ROOT/deploy/scripts/generate-package-sbom.mjs" nuget \
    "$OUT/dependencies.json" "$ID" "$VERSION" "$COMMIT" "$OUT/$ID.$VERSION.spdx.json"
  (cd "$OUT" && sha256sum -- *.nupkg *.spdx.json > SHA256SUMS)
}

case "$KIND" in
  telemetry)
    build_nuget src/Aetheus.Telemetry/Aetheus.Telemetry.csproj Aetheus.Telemetry ""
    ;;
  web-analytics-dotnet)
    build_nuget src/Aetheus.WebAnalytics/Aetheus.WebAnalytics.csproj Aetheus.WebAnalytics \
      "-p:StaticAssetVersion=$VERSION"
    ;;
  web-analytics-browser)
    SOURCE="$ROOT/packages/aetheus-web-analytics"
    PACKAGE_DIR="$REPRO/package"
    cp -a "$SOURCE" "$PACKAGE_DIR"
    npm pkg set "version=$VERSION" --prefix "$PACKAGE_DIR"
    npm pack "$PACKAGE_DIR" --pack-destination "$OUT" --ignore-scripts
    npm pack "$PACKAGE_DIR" --pack-destination "$REPRO" --ignore-scripts
    ARCHIVE="aetheus-web-analytics-$VERSION.tgz"
    cmp "$OUT/$ARCHIVE" "$REPRO/$ARCHIVE"
    node "$ROOT/deploy/scripts/generate-package-sbom.mjs" npm \
      "$PACKAGE_DIR/package.json" @aetheus/web-analytics "$VERSION" "$COMMIT" \
      "$OUT/aetheus-web-analytics.$VERSION.spdx.json"
    SCRIPT="$PACKAGE_DIR/src/aetheus-web-analytics.js"
    cp "$SCRIPT" "$OUT/aetheus-web-analytics.$VERSION.js"
    printf 'sha384-%s\n' \
      "$(openssl dgst -sha384 -binary "$OUT/aetheus-web-analytics.$VERSION.js" | openssl base64 -A)" \
      > "$OUT/aetheus-web-analytics.$VERSION.sri"
    (cd "$OUT" && sha256sum -- *.tgz *.js *.spdx.json *.sri > SHA256SUMS)
    ;;
esac

printf '%s\n' "$COMMIT" > "$OUT/source-commit"
node - "$KIND" "$VERSION" "$COMMIT" "$OUT/provenance.json" <<'NODE'
const fs = require("node:fs");
const crypto = require("node:crypto");
const [kind, version, commit, output] = process.argv.slice(2);
const directory = output.slice(0, output.lastIndexOf("/"));
const entries = fs.readdirSync(directory)
  .filter(name => /\.(nupkg|tgz|js|sri|json)$/.test(name) && name !== "provenance.json")
  .sort()
  .map(name => ({
    name,
    sha256: crypto.createHash("sha256")
      .update(fs.readFileSync(`${directory}/${name}`)).digest("hex")
  }));
fs.writeFileSync(output, `${JSON.stringify({
  schema: 1,
  sourceCommit: commit,
  packageKind: kind,
  packageVersion: version,
  artifacts: entries
}, null, 2)}\n`);
NODE
(cd "$OUT" && find . -maxdepth 1 -type f \
  ! -name SHA256SUMS ! -name source-commit -print \
  | sort | while IFS= read -r file; do sha256sum "$file"; done > SHA256SUMS)
test -s "$OUT/SHA256SUMS"

# restore-artifacts refuses any artifact without .pipeline-artifacts/{source-commit,
# delivery-contract.json,artifact-provenance.json} (D-04, bcdbfe577). The package set's Assemble stage
# restores this one, so publish-observability-packages run 2670 died on "carries no
# .pipeline-artifacts/source-commit". The Package stage publishes these three files beside the candidate.
SEAL="$ROOT/.pipeline-artifacts"
mkdir -p "$SEAL"
rm -f "$SEAL/source-commit" "$SEAL/artifact-provenance.json" "$SEAL/delivery-contract.json"
printf '%s\n' "$COMMIT" > "$SEAL/source-commit"
node - "$KIND" "$VERSION" "$COMMIT" "$OUT" "$SEAL" <<'NODE'
const fs = require("node:fs");
const crypto = require("node:crypto");
const [kind, version, commit, candidate, seal] = process.argv.slice(2);
const sha256 = path => crypto.createHash("sha256").update(fs.readFileSync(path)).digest("hex");
const files = fs.readdirSync(candidate).sort()
  .map(name => ({ path: `.package-candidate/${kind}/${name}`, sha256: sha256(`${candidate}/${name}`) }));
fs.writeFileSync(`${seal}/artifact-provenance.json`,
  `${JSON.stringify({ schemaVersion: 1, sourceCommit: commit, files })}\n`);
fs.writeFileSync(`${seal}/delivery-contract.json`, `${JSON.stringify({
  schemaVersion: 1,
  adapter: "observability-package",
  packageKind: kind,
  packageVersion: version,
  sourceSha: commit,
  artifactProvenanceSha256: sha256(`${seal}/artifact-provenance.json`)
})}\n`);
NODE
test -s "$SEAL/source-commit"
test -s "$SEAL/artifact-provenance.json"
test -s "$SEAL/delivery-contract.json"
