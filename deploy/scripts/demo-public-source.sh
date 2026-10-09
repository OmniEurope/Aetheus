#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# aetheus-demo, first stage (recette R-523): takes the version published on GitHub, proves it is the
# public distribution of one of the last commits of the internal main, and builds the demo's images
# from those public sources, on this host.
#
#   1. clone the public repository (PUBLIC_SOURCE_URL, from the aetheus.demo library) into
#      $WORKSPACE/.public-source;
#   2. export-public-distribution.mjs --verify against the last PUBLIC_MATCH_DEPTH commits of the
#      checked-out main (documentation ignored on both sides);
#   3. build the backend and frontend images with the public tree's own build script, and the
#      browser smoke image from its E2E project, all labelled and tagged with the public commit.
#
# When the verification refuses, the demo is still reset (user decision, 2026-10-02): the public
# commit the live demo runs, which passed the verification the day it was deployed, is fetched and
# built instead. It is read from the running demo itself (the colour UPSTREAM_CONF names, then the
# revision label of that colour's backend), since nothing is kept on the host between runs. Without a
# running demo the run fails here, before the reset, and the demo stays as it is.
#
# Nothing is reused from the internal repository's build: the images are what anyone cloning the
# public repository would build. The internal checkout only provides the verification and the
# deployment files (.pipeline/ is not published).
#
# Inputs, read from the environment:
#   WORKSPACE            the internal checkout (main)
#   PUBLIC_SOURCE_URL    the public repository to clone
#   PUBLIC_MATCH_DEPTH   how many main commits may be the published one (default 20)
#   APP_VERSION          the version stamped into the images
#   COMPOSE_PROJECT      names the browser smoke image, as the preparation expects it
#   UPSTREAM_CONF        the demo's Apache upstream; its "# Active colour:" line names the live colour
# Publishes PUBLIC_COMMIT (the public commit built) and PUBLIC_MATCHED_COMMIT (the internal one,
# "unknown" on the fallback: it was proved the day that version was deployed and is kept nowhere).
set -eu

fail() {
  echo "FATAL: $*" >&2
  exit 1
}

WORKSPACE="${WORKSPACE:?WORKSPACE is required}"
PUBLIC_SOURCE_URL="${PUBLIC_SOURCE_URL:?PUBLIC_SOURCE_URL is required (aetheus.demo library)}"
PUBLIC_MATCH_DEPTH="${PUBLIC_MATCH_DEPTH:-20}"
APP_VERSION="${APP_VERSION:?APP_VERSION is required}"
COMPOSE_PROJECT="${COMPOSE_PROJECT:?COMPOSE_PROJECT is required}"
case "$PUBLIC_SOURCE_URL" in https://*) ;; *) fail "PUBLIC_SOURCE_URL must be an https URL." ;; esac
case "$PUBLIC_MATCH_DEPTH" in ''|*[!0-9]*) fail "PUBLIC_MATCH_DEPTH must be a number." ;; esac

cd "$WORKSPACE"
PUBLIC_DIR="$WORKSPACE/.public-source"
rm -rf "$PUBLIC_DIR"
git clone --quiet --depth 1 "$PUBLIC_SOURCE_URL" "$PUBLIC_DIR"
PUBLIC_COMMIT="$(git -C "$PUBLIC_DIR" rev-parse HEAD)"
echo ">>> Public commit: $PUBLIC_COMMIT"

# The verification needs the commits it may match. They come with the checkout (AETHEUS_GIT_HISTORY_DEPTH
# in aetheus-demo.yaml): this step holds no Git credential to fetch more, and a deepen asked the
# internal repository for a username (run #2534, 2026-10-03).
if [ "$(git rev-parse --is-shallow-repository)" = true ]; then
  HELD="$(git rev-list --count HEAD)"
  [ "$HELD" -ge "$PUBLIC_MATCH_DEPTH" ] \
    || fail "The checkout holds $HELD commit(s) of main and the verification needs $PUBLIC_MATCH_DEPTH: raise AETHEUS_GIT_HISTORY_DEPTH."
fi
NODE="$(sh deploy/scripts/ensure-node-runtime.sh)"
VERIFY_LOG="$(mktemp)"
is_sha() {
  [ "${#1}" -eq 40 ] && case "$1" in *[!0-9a-f]*) false ;; *) true ;; esac
}

if "$NODE" deploy/scripts/export-public-distribution.mjs --verify "$PUBLIC_DIR" \
  --branch HEAD --depth "$PUBLIC_MATCH_DEPTH" >"$VERIFY_LOG" 2>&1; then
  cat "$VERIFY_LOG"
  PUBLIC_MATCHED_COMMIT="$(sed -n 's/^Verified: .* commit \([0-9a-f]\{40\}\)\.$/\1/p' "$VERIFY_LOG" | tail -n 1)"
  rm -f "$VERIFY_LOG"
  [ -n "$PUBLIC_MATCHED_COMMIT" ] || fail "The verification passed without naming the matched commit."
else
  cat "$VERIFY_LOG" >&2
  rm -f "$VERIFY_LOG"
  LIVE_COLOR=""
  if [ -n "${UPSTREAM_CONF:-}" ] && [ -f "$UPSTREAM_CONF" ]; then
    LIVE_COLOR="$(awk '/^# Active colour:/ { colour = $4 } END { print colour }' "$UPSTREAM_CONF")"
  fi
  case "$LIVE_COLOR" in
    blue|green) ;;
    *) fail "The public repository is not the distribution of a recent main commit and no live demo colour is known; nothing is deployed." ;;
  esac
  LIVE_BACK="$COMPOSE_PROJECT-$LIVE_COLOR-back"
  [ "$(docker inspect --format '{{.State.Running}}' "$LIVE_BACK" 2>/dev/null)" = true ] \
    || fail "The public repository is refused and the live demo backend $LIVE_BACK is not running; nothing is deployed."
  LAST_PUBLIC="$(docker inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$LIVE_BACK")"
  is_sha "$LAST_PUBLIC" || fail "The live demo backend $LIVE_BACK carries no readable revision: '$LAST_PUBLIC'"
  LAST_MATCHED="unknown"
  echo "WARNING: the public repository is refused; the demo is reset with the version it runs, public commit $LAST_PUBLIC ($LIVE_COLOR)." >&2
  git -C "$PUBLIC_DIR" fetch --quiet --depth 1 origin "$LAST_PUBLIC" \
    || fail "The last deployed public commit $LAST_PUBLIC cannot be fetched from the public repository; nothing is deployed."
  git -C "$PUBLIC_DIR" checkout --quiet --detach "$LAST_PUBLIC"
  [ "$(git -C "$PUBLIC_DIR" rev-parse HEAD)" = "$LAST_PUBLIC" ] \
    || fail "The public checkout is not at the last deployed commit $LAST_PUBLIC."
  PUBLIC_COMMIT="$LAST_PUBLIC"
  PUBLIC_MATCHED_COMMIT="$LAST_MATCHED"
fi

# --- Build, from the public tree only ------------------------------------------------------------
cd "$PUBLIC_DIR"
# WORKSPACE is the public tree for the whole build: publish-application.sh writes .pipeline-publish/
# under $WORKSPACE, and the inherited internal checkout sent the backend there while Docker read the
# public tree (run #2551: "/.pipeline-publish/backend": not found).
WORKSPACE="$PUBLIC_DIR" BUILD_SOURCEVERSION="$PUBLIC_COMMIT" APP_VERSION="$APP_VERSION" \
  sh deploy/scripts/build-fast-release-images.sh

DOTNET="$(sh deploy/scripts/ensure-dotnet-sdk.sh)"
"$DOTNET" build tests/Aetheus.E2E/Aetheus.E2E.csproj --configuration Release --verbosity minimal
CONTEXT="$PUBLIC_DIR/.browser-smoke-context"
rm -rf "$CONTEXT"
mkdir -p "$CONTEXT/tests/Aetheus.E2E/bin/Release" "$CONTEXT/tests/Aetheus.E2E/obj"
cp Directory.Build.props Directory.Packages.props NuGet.config "$CONTEXT/"
cp tests/Aetheus.E2E/Aetheus.E2E.csproj "$CONTEXT/tests/Aetheus.E2E/"
cp -a tests/Aetheus.E2E/bin/Release/net10.0 "$CONTEXT/tests/Aetheus.E2E/bin/Release/"
# Same trimming as the CI packaging: only the Linux driver of Playwright goes into the image.
find "$CONTEXT/tests/Aetheus.E2E/bin/Release/net10.0/.playwright/node" \
  -mindepth 1 -maxdepth 1 ! -name linux-x64 -exec rm -rf {} +
test -x "$CONTEXT/tests/Aetheus.E2E/bin/Release/net10.0/.playwright/node/linux-x64/node"
cp tests/Aetheus.E2E/obj/project.assets.json tests/Aetheus.E2E/obj/*.nuget.g.props \
  tests/Aetheus.E2E/obj/*.nuget.g.targets "$CONTEXT/tests/Aetheus.E2E/obj/"
BROWSER_IMAGE="$COMPOSE_PROJECT-browser-smoke:$PUBLIC_COMMIT"
BUILDER="${AETHEUS_BUILDX_BUILDER:-aetheus-$(hostname 2>/dev/null || echo agent)}"
sh deploy/scripts/buildx-build-load.sh --builder "$BUILDER" --load \
  --label "org.opencontainers.image.revision=$PUBLIC_COMMIT" \
  -f deploy/docker/Dockerfile.e2e -t "$BROWSER_IMAGE" "$CONTEXT"
rm -rf "$CONTEXT"

# shellcheck source=deploy-identity.sh
. deploy/scripts/deploy-identity.sh
deploy_image_repos
for image in "$BACK_IMAGE_REPO:$PUBLIC_COMMIT" "$FRONT_IMAGE_REPO:$PUBLIC_COMMIT" "$BROWSER_IMAGE"; do
  [ "$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image")" = "$PUBLIC_COMMIT" ] \
    || fail "Image revision is invalid: $image"
done

echo "##aetheus[setvariable name=PUBLIC_COMMIT]$PUBLIC_COMMIT"
echo "##aetheus[setvariable name=PUBLIC_MATCHED_COMMIT]$PUBLIC_MATCHED_COMMIT"
echo "Public commit $PUBLIC_COMMIT (main $PUBLIC_MATCHED_COMMIT) built for the demo."
