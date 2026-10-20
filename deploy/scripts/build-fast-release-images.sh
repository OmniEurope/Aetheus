# SPDX-License-Identifier: EUPL-1.2
# Verify the launched revision before publishing and building fast-release images because this path
# skips the qualification chain. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
set -eu
SOURCE_COMMIT="$(git rev-parse HEAD)"
case "${#SOURCE_COMMIT}" in 40|64) ;; *) echo "Resolved source revision has an invalid length."; exit 1 ;; esac
case "$SOURCE_COMMIT" in *[!0-9a-fA-F]*) echo "Resolved source revision is not hexadecimal."; exit 1 ;; esac
if [ -z "${BUILD_SOURCEVERSION:-}" ] || [ "$BUILD_SOURCEVERSION" != "$SOURCE_COMMIT" ]; then
  echo "Checked-out revision does not match BUILD_SOURCEVERSION; refusing an unpinned fast deploy." >&2
  exit 1
fi
docker --version
docker compose version

# Same builder as the CI (the image build of .pipeline/aetheus-ci.yaml), for two reasons that both cost
# minutes. A bare `docker build` runs on the daemon's default builder, which nobody maintains: the
# agent's storage maintenance only prunes its own named builder, so that cache grew unbounded and
# was trimmed by the daemon's own garbage collector between two runs - measured on this host as 14
# cached layers on 6 September, 6 the next day, 1 the day after, and a build that went from 10 to 19
# minutes on an unchanged Dockerfile. And the two builders share nothing, so the agent and EF-bundle
# stages the CI had just built for this very commit were rebuilt from scratch here every time.
BUILDER="${AETHEUS_BUILDX_BUILDER:-aetheus-$(hostname 2>/dev/null || echo agent)}"
sh deploy/scripts/ensure-buildx-builder.sh "$BUILDER"

# Overlap frontend publishing with the EF bundle build after the host backend compilation so
# CPU-heavy compiles do not compete; keep the bundle inside its target image because its apphost
# depends on the build OS. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
# The same warm build also covers the agent packages (the Linux tar.gz and the Windows zip, with
# aetheus-fn): they read nothing from the host publish and were the longest part of the image build
# (926 s on release-fast 2779), so they now run while the frontend publish does instead of after it.
DOTNET="$(sh deploy/scripts/ensure-dotnet-sdk.sh)"
DOTNET="$DOTNET" APP_VERSION="${APP_VERSION}" \
  sh deploy/scripts/publish-application.sh .pipeline-publish backend

WARM_LOG="$(mktemp)"
docker buildx build --builder "$BUILDER" \
  --target release-inputs-warm \
  --output type=cacheonly \
  --build-arg "APP_VERSION=${APP_VERSION}" \
  --build-arg "SOURCE_COMMIT=${SOURCE_COMMIT}" \
  --build-arg "CACHE_COHORT=shared" \
  --file deploy/docker/Dockerfile.back \
  . >"$WARM_LOG" 2>&1 &
WARM_PID="$!"
# Never leave the warm build running if the publish below fails and `set -e` ends the script.
trap 'kill "$WARM_PID" 2>/dev/null || true; rm -f "$WARM_LOG"' EXIT INT TERM

# Both Dockerfiles copy a host publish instead of compiling. This pipeline compiles nothing of its
# own, so it produces that publish here, exactly as CI does (the backend half ran above).
DOTNET="$DOTNET" APP_VERSION="${APP_VERSION}" \
  sh deploy/scripts/publish-application.sh .pipeline-publish frontend

# The warm build is an accelerator, never a verdict. If it failed, the full build below rebuilds the
# same stage from scratch and reports the real error with its own output; a green release is never
# declared on the strength of this one, so its failure is logged and not swallowed silently.
if wait "$WARM_PID"; then
  echo ">>> EF bundle and agent packages warmed while the host publish ran; the build below reuses them."
else
  echo ">>> Warming the EF bundle and agent packages failed; the full build will rebuild them and report why." >&2
  cat "$WARM_LOG" >&2
fi
trap - EXIT INT TERM
rm -f "$WARM_LOG"

# shellcheck source=deploy-identity.sh
. deploy/scripts/deploy-identity.sh
deploy_image_repos
sh deploy/scripts/buildx-build-load.sh --builder "$BUILDER" --load \
  --label "org.opencontainers.image.revision=${SOURCE_COMMIT}" \
  --build-arg "APP_VERSION=${APP_VERSION}" \
  --build-arg "SOURCE_COMMIT=${SOURCE_COMMIT}" \
  --build-arg "CACHE_COHORT=shared" \
  --file deploy/docker/Dockerfile.back \
  --tag "$BACK_IMAGE_REPO:${SOURCE_COMMIT}" .
sh deploy/scripts/buildx-build-load.sh --builder "$BUILDER" --load \
  --label "org.opencontainers.image.revision=${SOURCE_COMMIT}" \
  --file deploy/docker/Dockerfile.front \
  --tag "$FRONT_IMAGE_REPO:${SOURCE_COMMIT}" .
docker image inspect "$BACK_IMAGE_REPO:${SOURCE_COMMIT}" "$FRONT_IMAGE_REPO:${SOURCE_COMMIT}" >/dev/null
sh deploy/scripts/package-fast-release-artifacts.sh
