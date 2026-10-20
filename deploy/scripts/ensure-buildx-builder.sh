#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Ensure a named docker-container Buildx builder is bootstrapped. Buildx can retain
# a local builder registration after its BuildKit container has disappeared.
set -eu

BUILDER="${1:-}"
case "$BUILDER" in
    ''|*[!a-zA-Z0-9._-]*)
        echo "Buildx builder name is missing or unsafe: '$BUILDER'" >&2
        exit 2
        ;;
esac

# Optional cap on how many build steps BuildKit runs at once, set per host, never in the repository.
# A Dockerfile whose stages are independent - `backend-build` and `ef-bundle` are - lets BuildKit run
# two dotnet publishes at the same time. That is exactly what we want on the production VPS and
# exactly what kills the build on a small host: on the 4 GB mirror VM the kernel OOM-killer took out
# `csc` mid-compile and the step surfaced only as `ResourceExhausted`. Capping the worker serialises
# those stages; it changes no gate, no test and no output, only how many run concurrently.
MAX_PARALLELISM="${AETHEUS_BUILDX_MAX_PARALLELISM:-}"
case "$MAX_PARALLELISM" in
    '') ;;
    0|*[!0-9]*)
        echo "AETHEUS_BUILDX_MAX_PARALLELISM must be a positive integer: '$MAX_PARALLELISM'" >&2
        exit 2
        ;;
esac

CONFIG_ARGS=""
if [ -n "$MAX_PARALLELISM" ]; then
    # Store BuildKit configuration at a stable agent-owned path because container restarts reread it
    # and the unprivileged agent cannot write under /var/lib. See
    # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
    CONFIG_DIR="${AETHEUS_BUILDX_CONFIG_DIR:-${HOME:-/tmp}/.aetheus/buildx}"
    mkdir -p "$CONFIG_DIR"
    CONFIG_FILE="$CONFIG_DIR/buildkitd-$BUILDER.toml"
    printf '[worker.oci]\n  max-parallelism = %s\n' "$MAX_PARALLELISM" > "$CONFIG_FILE"
    CONFIG_ARGS="--config $CONFIG_FILE"
    echo "BuildKit will run at most $MAX_PARALLELISM step(s) at a time on this host."
fi

# A healthy builder is left alone, so changing the cap means removing the builder first:
#   docker buildx rm <builder>
if docker buildx inspect "$BUILDER" --bootstrap >/dev/null 2>&1; then
    echo "Buildx builder '$BUILDER' is ready."
    exit 0
fi

echo "Buildx builder '$BUILDER' is absent or unhealthy; recreating its isolated BuildKit container."
if docker buildx rm --force "$BUILDER" >/dev/null 2>&1; then
    echo "Removed stale Buildx builder '$BUILDER'."
else
    echo "No removable Buildx builder '$BUILDER' was registered."
fi

# shellcheck disable=SC2086 # CONFIG_ARGS is either empty or exactly two words we built above.
docker buildx create \
    --name "$BUILDER" \
    --driver docker-container \
    $CONFIG_ARGS \
    --bootstrap >/dev/null
docker buildx inspect "$BUILDER" --bootstrap >/dev/null
echo "Buildx builder '$BUILDER' was recreated and bootstrapped."
