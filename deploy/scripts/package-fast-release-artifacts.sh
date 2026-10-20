#!/usr/bin/env sh
# SPDX-License-Identifier: EUPL-1.2
# Retain only the already-built production payload required by the next Candidate.
set -eu

COMMIT="$(git rev-parse HEAD)"
case "$COMMIT" in
  *[!0-9a-fA-F]*|'') echo "Resolved source revision is invalid." >&2; exit 1 ;;
esac
if [ "${BUILD_SOURCEVERSION:-}" != "$COMMIT" ]; then
  echo "Checked-out revision does not match BUILD_SOURCEVERSION." >&2
  exit 1
fi

WORKSPACE="${WORKSPACE:-$(pwd)}"
PACKAGE_DIR="$WORKSPACE/.pipeline-artifacts"
# shellcheck source=deploy-identity.sh
. deploy/scripts/deploy-identity.sh
deploy_image_repos
BACK_IMAGE="$BACK_IMAGE_REPO:$COMMIT"
FRONT_IMAGE="$FRONT_IMAGE_REPO:$COMMIT"
docker image inspect "$BACK_IMAGE" "$FRONT_IMAGE" >/dev/null

rm -rf "$PACKAGE_DIR"
mkdir -p "$PACKAGE_DIR"
printf '%s\n' "$COMMIT" > "$PACKAGE_DIR/source-commit"
# Contract 4 is a payload-only fast baseline. Candidate supplies its own already-built
# integration/browser runtimes when exercising these retained N-1 application images.
printf '%s\n' 4 > "$PACKAGE_DIR/qa-rollback-contract"

NODE="$(sh deploy/scripts/ensure-node-runtime.sh)"
# Use the same manifest writer in both packagers so the sealed digest covers identical bytes for the
# same integration inputs. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
"$NODE" deploy/scripts/write-integration-manifest.mjs \
  "$COMMIT" "$PACKAGE_DIR/integration-manifest.json"

# Export and verify both archives concurrently, using pigz when available and deterministic gzip
# headers; wait for both jobs and propagate any export, compression or verification failure. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
if command -v pigz >/dev/null 2>&1; then GZIP_TOOL=pigz; else GZIP_TOOL=gzip; fi
save_and_verify() {
  docker save "$1" > "$2.tar"
  "$GZIP_TOOL" -1 -n -f "$2.tar"
  "$GZIP_TOOL" -t "$2.tar.gz"
}
save_and_verify "$BACK_IMAGE" "$PACKAGE_DIR/aetheus-back" & BACK_JOB=$!
save_and_verify "$FRONT_IMAGE" "$PACKAGE_DIR/aetheus-front" & FRONT_JOB=$!
BACK_STATUS=0; FRONT_STATUS=0
wait "$BACK_JOB" || BACK_STATUS=$?
wait "$FRONT_JOB" || FRONT_STATUS=$?
[ "$BACK_STATUS" -eq 0 ] || { echo "Packaging the back image failed ($BACK_STATUS)." >&2; exit 1; }
[ "$FRONT_STATUS" -eq 0 ] || { echo "Packaging the front image failed ($FRONT_STATUS)." >&2; exit 1; }

export DELIVERY_BASELINE_BOOTSTRAP=true
export DELIVERY_CANDIDATE_VERSION_MODE=source
"$NODE" deploy/scripts/generate-delivery-contract.mjs \
  "$PACKAGE_DIR/integration-manifest.json" \
  "$PACKAGE_DIR/delivery-contract.json" \
  aetheus 1 "c-$COMMIT" "$COMMIT" \
  src/Aetheus.Back/Data/Migrations \
  "$PACKAGE_DIR/aetheus-back.tar.gz" \
  "$PACKAGE_DIR/aetheus-front.tar.gz"

test -s "$PACKAGE_DIR/delivery-contract.json"
