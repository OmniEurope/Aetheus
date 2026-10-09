# SPDX-License-Identifier: EUPL-1.2
#
# Packages the exact application that CI just validated: publishes the .NET output on the runner,
# builds the three images from it, exports and verifies their archives, and seals the delivery
# contract and the artifact provenance over the result.
#
# This was 184 lines inlined in .pipeline/aetheus-ci.yaml, the largest shell block in the repository
# and the one that decides what reaches production. A pipeline definition is not versioned, not
# tested and not reusable: moved here, it can at least be read, linted and called by another
# pipeline. Its behaviour is unchanged by the move, deliberately: this is a relocation, not a
# rewrite. The one substitution is the embedded JavaScript heredoc, now
# deploy/scripts/write-integration-manifest.mjs with its own tests.
#
# Inputs, all read from the environment (none positional):
#   WORKSPACE                     the run's checkout, and the parent of .pipeline-artifacts (required)
#   BUILD_SOURCEVERSION           the immutable revision this run is pinned to (required)
#   BUILD_BUILDID                 the globally unique run id, used for cache cohorts (required)
#   BUILD_RELEASENUMBER           the project release number, the last number of the version (required)
#   AETHEUS_CI_VERSION_SUFFIX     the version suffix of a build that is no release (the nightly's); empty otherwise
#   AETHEUS_CACHE_SCENARIO        warm (shared cache) or cold (per-run cache); defaults to warm
#   DELIVERY_BASELINE_SOURCE_SHA  the last delivered revision, to skip unaffected optional packages
#   DELIVERY_BASELINE_BOOTSTRAP   passed through to the delivery contract; defaults to true
#   AETHEUS_BUILDX_BUILDER        the buildx builder name; defaults to aetheus-$HOSTNAME
#
# Publishes OPTIONAL_OBSERVABILITY_AFFECTED as a run output variable.
set -eu
COMMIT="$(git rev-parse HEAD)"
test "$COMMIT" = "${BUILD_SOURCEVERSION:-}" || {
  echo "The application workspace does not match BUILD_SOURCEVERSION."
  exit 1
}
RUN_ID="${BUILD_BUILDID:-}"
case "$RUN_ID" in ''|*[!0-9]*) echo "Invalid BUILD_BUILDID."; exit 1 ;; esac
RELEASE_NUMBER="${BUILD_RELEASENUMBER:-}"
case "$RELEASE_NUMBER" in ''|*[!0-9]*) echo "Invalid BUILD_RELEASENUMBER."; exit 1 ;; esac
# The application version ends with the project's release number (recette R2-110, 2026-10-04): this
# CI runs under a candidate, whose number it inherits, so the site and the agents packaged here carry
# the release's version (1.3.0, 1.3.1...). Under a nightly it is the latest release number, with the
# nightly's suffix (AETHEUS_CI_VERSION_SUFFIX), so a nightly build never takes a release's version.
#
# The code's major.minor, from Directory.Build.props through the one script that composes versions:
# this used to be a literal "1.1." here, a third copy beside that file and the library entry.
APP_VERSION="$(env VERSION_SUFFIX="${AETHEUS_CI_VERSION_SUFFIX:-}" sh deploy/scripts/app-version.sh "$RELEASE_NUMBER")"
PACKAGE_DIR="$WORKSPACE/.pipeline-artifacts"
rm -rf "$PACKAGE_DIR"
mkdir -p "$PACKAGE_DIR"
printf '%s\n' "$COMMIT" > "$PACKAGE_DIR/source-commit"
# Contract 3 stores the browser runner as a flattened Chromium-only rootfs archive.
printf '%s\n' 3 > "$PACKAGE_DIR/qa-rollback-contract"
printf '%s\n' 1 > "$PACKAGE_DIR/agent-protocol-contract"
NODE="$(sh deploy/scripts/ensure-node-runtime.sh)"
"$NODE" deploy/scripts/write-integration-manifest.mjs "$COMMIT" "$PACKAGE_DIR/integration-manifest.json"

DOTNET="$(sh deploy/scripts/ensure-dotnet-sdk.sh)"
case "${AETHEUS_CACHE_SCENARIO:-warm}" in
  warm) CACHE_COHORT=shared ;;
  cold)
    CACHE_COHORT="run-$RUN_ID"
    export NUGET_PACKAGES="$WORKSPACE/.pipeline-cache/nuget-$RUN_ID"
    ;;
  *) echo "AETHEUS_CACHE_SCENARIO must be warm or cold." >&2; exit 1 ;;
esac
OPTIONAL_BASELINE="${DELIVERY_BASELINE_SOURCE_SHA:-}"
OPTIONAL_CHANGED=true
if printf '%s' "$OPTIONAL_BASELINE" | grep -Eq '^[0-9a-fA-F]{40}$' \
  && git cat-file -e "${OPTIONAL_BASELINE}^{commit}" 2>/dev/null \
  && git merge-base --is-ancestor "$OPTIONAL_BASELINE" "$COMMIT" \
  && git diff --quiet "$OPTIONAL_BASELINE..$COMMIT" -- \
    src/Aetheus.Telemetry tests/Aetheus.Telemetry.Tests \
    src/Aetheus.WebAnalytics tests/Aetheus.WebAnalytics.Tests \
    examples/optional-observability deploy/scripts/verify-optional-observability.sh \
    Directory.Packages.props Directory.Build.props Directory.Build.targets NuGet.config
then
  OPTIONAL_CHANGED=false
fi
if [ "$OPTIONAL_CHANGED" = true ]; then
  sh deploy/scripts/run-with-progress.sh "Affected optional package variants" \
    env DOTNET="$DOTNET" NODE="$NODE" sh deploy/scripts/verify-optional-observability.sh
else
  echo "Optional observability variants are unaffected; full matrix remains nightly."
fi
echo "##aetheus[setvariable name=OPTIONAL_OBSERVABILITY_AFFECTED]$OPTIONAL_CHANGED"
# The images no longer recompile what Compile already built. This publishes the backend,
# the WebAssembly site and its static host once, on the runner, and the two Dockerfiles
# copy the result. The EF bundle deliberately stays inside the backend image: its apphost
# follows the BUILD host's operating system, so building it here would tie the deployed
# binary to whoever ran the pipeline.
# The long, silent commands below run under run-with-progress.sh (PLAN-007 lot 6): run #2328 logged
# nothing for 4 min 39 s in this step, which reads exactly like a hung agent. Behaviour is unchanged;
# each command gets a start line, a heartbeat every 30 s and its duration.
sh deploy/scripts/run-with-progress.sh "Publish the application" \
  env DOTNET="$DOTNET" APP_VERSION="$APP_VERSION" \
  sh deploy/scripts/publish-application.sh .pipeline-publish
BUILDER="${AETHEUS_BUILDX_BUILDER:-aetheus-$(hostname 2>/dev/null || echo agent)}"
sh deploy/scripts/ensure-buildx-builder.sh "$BUILDER"
BACK_IMAGE="aetheus-back:$COMMIT"
FRONT_IMAGE="aetheus-front:$COMMIT"
sh deploy/scripts/run-with-progress.sh "Build the backend image" \
  sh deploy/scripts/buildx-build-load.sh --builder "$BUILDER" --load \
  --label "org.opencontainers.image.revision=$COMMIT" \
  --build-arg "APP_VERSION=$APP_VERSION" \
  --build-arg "SOURCE_COMMIT=$COMMIT" \
  --build-arg "CACHE_COHORT=$CACHE_COHORT" \
  -f deploy/docker/Dockerfile.back -t "$BACK_IMAGE" .
sh deploy/scripts/run-with-progress.sh "Build the frontend image" \
  sh deploy/scripts/buildx-build-load.sh --builder "$BUILDER" --load \
  --label "org.opencontainers.image.revision=$COMMIT" \
  -f deploy/docker/Dockerfile.front -t "$FRONT_IMAGE" .
BROWSER_SMOKE_CONTEXT="$WORKSPACE/.browser-smoke-context"
rm -rf "$BROWSER_SMOKE_CONTEXT"
mkdir -p "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/bin/Release" \
  "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/obj"
cp Directory.Build.props Directory.Packages.props NuGet.config "$BROWSER_SMOKE_CONTEXT/"
cp tests/Aetheus.E2E/Aetheus.E2E.csproj "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/"
cp -a tests/Aetheus.E2E/bin/Release/net10.0 \
  "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/bin/Release/"
find "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/bin/Release/net10.0/.playwright/node" \
  -mindepth 1 -maxdepth 1 ! -name linux-x64 -exec rm -rf {} +
test -x "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/bin/Release/net10.0/.playwright/node/linux-x64/node"
cp tests/Aetheus.E2E/obj/project.assets.json \
  tests/Aetheus.E2E/obj/*.nuget.g.props \
  tests/Aetheus.E2E/obj/*.nuget.g.targets \
  "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/obj/"
test -s "$BROWSER_SMOKE_CONTEXT/tests/Aetheus.E2E/bin/Release/net10.0/Aetheus.E2E.dll"
BROWSER_SMOKE_IMAGE="aetheus-browser-smoke:$COMMIT"
sh deploy/scripts/run-with-progress.sh "Build the browser smoke image" \
  sh deploy/scripts/buildx-build-load.sh --builder "$BUILDER" --load \
  --label "org.opencontainers.image.revision=$COMMIT" \
  -f "$WORKSPACE/deploy/docker/Dockerfile.e2e" -t "$BROWSER_SMOKE_IMAGE" \
  "$BROWSER_SMOKE_CONTEXT"
rm -rf "$BROWSER_SMOKE_CONTEXT"
# Real 289348608-byte backend export benchmark: gzip -1 = 9.920s/287724019 bytes,
# -6 = 13.343s/287669328 and -9 = 17.202s/287669172. Level 1 is 42% faster than
# level 9 for only 0.019% more bytes and remains far below the 1 GiB upload boundary.
# The dense level-9 browser reference was already about 1.14 GB in CI #1484, so its
# four-part publication remains required; no gzip level offers a 15% safety margin.
# save-image-archive.sh is the same `docker save | gzip -1`, failing when the save fails: inline, the
# pipe returned gzip's status and a failed save sealed an empty archive (DEP-014).
sh deploy/scripts/run-with-progress.sh "Export the backend image" \
  sh deploy/scripts/ci/save-image-archive.sh "$BACK_IMAGE" "$PACKAGE_DIR/aetheus-back.tar.gz"
sh deploy/scripts/run-with-progress.sh "Export the frontend image" \
  sh deploy/scripts/ci/save-image-archive.sh "$FRONT_IMAGE" "$PACKAGE_DIR/aetheus-front.tar.gz"
BROWSER_SMOKE_CONTAINER="$(docker create "$BROWSER_SMOKE_IMAGE" true)"
cleanup_browser_smoke_container() {
  if [ -n "${BROWSER_SMOKE_CONTAINER:-}" ]; then
    docker rm -f "$BROWSER_SMOKE_CONTAINER" >/dev/null 2>&1 || true
  fi
}
trap cleanup_browser_smoke_container EXIT INT TERM
sh deploy/scripts/run-with-progress.sh "Export the browser smoke rootfs" \
  sh -c 'docker export "$1" | gzip -1 > "$2"' export-browser \
  "$BROWSER_SMOKE_CONTAINER" "$PACKAGE_DIR/aetheus-browser-smoke.tar.gz"
cleanup_browser_smoke_container
BROWSER_SMOKE_CONTAINER=""
trap - EXIT INT TERM
gzip -t "$PACKAGE_DIR/aetheus-back.tar.gz"
gzip -t "$PACKAGE_DIR/aetheus-front.tar.gz"
# One decompression of the 1.14 GB archive instead of two (PLAN-007 lot 6). `tar -tzf` covers both
# checks of the `gzip -t` + `gzip -dc | tar -tf -` pair it replaces: GNU tar exits 2 when its gzip
# child reports a CRC, stream or truncation error, which the pipe alone, without pipefail, let
# through with tar's own exit 0 (only the separate `gzip -t` caught it).
tar -tzf "$PACKAGE_DIR/aetheus-browser-smoke.tar.gz" > "$PACKAGE_DIR/browser-smoke-rootfs.manifest"
grep -q '^src/tests/Aetheus.E2E/bin/Release/net10.0/Aetheus.E2E.dll$' "$PACKAGE_DIR/browser-smoke-rootfs.manifest"
grep -q '^ms-playwright/chromium-' "$PACKAGE_DIR/browser-smoke-rootfs.manifest"
if grep -Eq '^ms-playwright/(firefox|webkit)-' "$PACKAGE_DIR/browser-smoke-rootfs.manifest"; then
  echo "Browser smoke rootfs unexpectedly contains unused browser engines." >&2
  exit 1
fi
rm "$PACKAGE_DIR/browser-smoke-rootfs.manifest"
tar -C "$WORKSPACE" -czf "$PACKAGE_DIR/aetheus-vitrine.tar.gz" site docs-site/public
sh deploy/scripts/extract-agent-release-from-image.sh \
  "$BACK_IMAGE" "$PACKAGE_DIR/agent-release" "$COMMIT" "$NODE" manifest-only
test -s "$PACKAGE_DIR/aetheus-back.tar.gz"
test -s "$PACKAGE_DIR/aetheus-front.tar.gz"
test -s "$PACKAGE_DIR/aetheus-vitrine.tar.gz"
test -s "$PACKAGE_DIR/aetheus-browser-smoke.tar.gz"
test -s "$PACKAGE_DIR/integration-manifest.json"
test -s "$PACKAGE_DIR/agent-release/agent-release-manifest.json"
export DELIVERY_BASELINE_BOOTSTRAP="${DELIVERY_BASELINE_BOOTSTRAP:-true}"
export DELIVERY_CANDIDATE_VERSION_MODE=source
"$NODE" deploy/scripts/generate-delivery-contract.mjs \
  "$PACKAGE_DIR/integration-manifest.json" \
  "$PACKAGE_DIR/delivery-contract.json" \
  aetheus 1 "c-$COMMIT" "$COMMIT" \
   src/Aetheus.Back/Data/Migrations \
   "$PACKAGE_DIR/aetheus-back.tar.gz" \
   "$PACKAGE_DIR/aetheus-front.tar.gz" \
   "$PACKAGE_DIR/aetheus-vitrine.tar.gz" \
   "$PACKAGE_DIR/aetheus-browser-smoke.tar.gz"
"$NODE" deploy/scripts/generate-artifact-provenance.mjs \
  "$PACKAGE_DIR/artifact-provenance.json" "$COMMIT"
# The browser-smoke image exists only to be exported: QA re-imports the rootfs archive
# under its own tag. Left behind, each one is 4 to 6 GB of a nested Docker store that no
# host-side prune can see, and a failed run never reaches any cleanup. Drop this run's
# image now that its archive is verified, bound what earlier failed runs leaked, and keep
# the builder cache inside the same limits QA applies.
docker image rm "$BROWSER_SMOKE_IMAGE" >/dev/null
sh deploy/scripts/retain-docker-images.sh aetheus-browser-smoke
AETHEUS_BUILDX_BUILDER="$BUILDER" sh deploy/scripts/prune-qa-buildx-cache.sh
# The two gate statuses are NOT republished here. Gate unit tests already published them,
# this stage only reads them back, and re-emitting the same values under the same names was
