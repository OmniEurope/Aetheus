#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

SOURCE_ROOT="${1:?source root is required}"
FRONT="${2:?front container is required}"
BACK="${3:?back container is required}"
ADMIN_PASSWORD_FILE="${4:?admin password file is required}"
RUN_LABEL="${5:?run label is required}"
PRESERVE_DATABASE="${6:-false}"
IMAGE="${7:-}"
EXPECTED_COMMIT="${8:-}"
WORKSPACE="${WORKSPACE:?WORKSPACE is required}"

case "$RUN_LABEL" in *[!a-zA-Z0-9_-]*|'') echo "Invalid E2E run label." >&2; exit 2 ;; esac
# PLAN-007 lot 6: NUnit workers, from the pipeline. 1 keeps the suite sequential, as before.
E2E_WORKERS="${AETHEUS_E2E_WORKERS:-1}"
case "$E2E_WORKERS" in 1|2|3|4) ;; *) echo "AETHEUS_E2E_WORKERS must be 1 to 4." >&2; exit 2 ;; esac
echo "E2E suite $RUN_LABEL runs with $E2E_WORKERS NUnit worker(s)."
case "$PRESERVE_DATABASE" in true|false) ;; *) echo "Preserve-database flag must be true or false." >&2; exit 2 ;; esac
# true only for a V-1 payload-only baseline running V's suite (run-previous-e2e-gate.sh): the tests
# marked NewSinceDeployedBaseline are then reported skipped, never passed.
PREVIOUS_PAYLOAD_ONLY="${E2E_PREVIOUS_PAYLOAD_ONLY:-false}"
case "$PREVIOUS_PAYLOAD_ONLY" in true|false) ;; *) echo "E2E_PREVIOUS_PAYLOAD_ONLY must be true or false." >&2; exit 2 ;; esac
echo "E2E suite $RUN_LABEL targets a payload-only V-1: $PREVIOUS_PAYLOAD_ONLY."
test -s "$ADMIN_PASSWORD_FILE"
CONTEXT=""
BUILT_IMAGE=false
if [ -n "$IMAGE" ] || [ -n "$EXPECTED_COMMIT" ]; then
  test -n "$IMAGE" -a -n "$EXPECTED_COMMIT" || {
    echo "Immutable E2E image and expected source commit must be provided together." >&2
    exit 2
  }
  case "$EXPECTED_COMMIT" in
    ????????????????????????????????????????) ;;
    *) echo "Expected E2E source commit must contain 40 hexadecimal characters." >&2; exit 2 ;;
  esac
  case "$EXPECTED_COMMIT" in *[!0-9a-fA-F]*) echo "Expected E2E source commit is not hexadecimal." >&2; exit 2 ;; esac
  ACTUAL_COMMIT="$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$IMAGE")"
  test "$ACTUAL_COMMIT" = "$EXPECTED_COMMIT" || {
    echo "Immutable E2E image revision $ACTUAL_COMMIT does not match $EXPECTED_COMMIT." >&2
    exit 2
  }
else
  test -s "$SOURCE_ROOT/tests/Aetheus.E2E/bin/Release/net10.0/Aetheus.E2E.dll"
  test -s "$SOURCE_ROOT/tests/Aetheus.E2E/obj/project.assets.json"
  PLAYWRIGHT_VERSION="$(sed -n 's/.*PackageVersion Include="Microsoft.Playwright.NUnit" Version="\([^"]*\)".*/\1/p' "$SOURCE_ROOT/Directory.Packages.props")"
  case "$PLAYWRIGHT_VERSION" in
    [0-9]*.[0-9]*.[0-9]*) ;;
    *) echo "Could not resolve the Playwright version from $SOURCE_ROOT/Directory.Packages.props." >&2; exit 2 ;;
  esac
  test "$PLAYWRIGHT_VERSION" = 1.63.0 || {
    echo "Playwright package $PLAYWRIGHT_VERSION has no reviewed immutable image digest; update both together." >&2
    exit 2
  }
  PLAYWRIGHT_IMAGE="mcr.microsoft.com/playwright/dotnet:v${PLAYWRIGHT_VERSION}-noble@sha256:aa3eaf29a65df8c93fc32ab20cf9359f4d8ecc53e6e89f79378647ace87b46b3"
  CONTEXT="$WORKSPACE/.qa-e2e-context-$RUN_LABEL"
  IMAGE="aetheus-e2e:$RUN_LABEL"
  BUILT_IMAGE=true
  rm -rf "$CONTEXT"
  mkdir -p "$CONTEXT/tests/Aetheus.E2E"
  cp "$SOURCE_ROOT/Directory.Build.props" "$SOURCE_ROOT/Directory.Packages.props" "$SOURCE_ROOT/NuGet.config" "$CONTEXT/"
  cp -a "$SOURCE_ROOT/tests/Aetheus.E2E/." "$CONTEXT/tests/Aetheus.E2E/"
  docker build --build-arg "PLAYWRIGHT_IMAGE=$PLAYWRIGHT_IMAGE" \
    -f "$SOURCE_ROOT/deploy/docker/Dockerfile.e2e" -t "$IMAGE" "$CONTEXT"
fi

READY="aetheus-$RUN_LABEL-ready"
RUNNER="aetheus-$RUN_LABEL-tests"
RESULTS_DIR="$WORKSPACE/.qa-test-results/$RUN_LABEL/e2e"
TRX="$RESULTS_DIR/e2e.trx"
RAW_EXIT_FILE="$RESULTS_DIR/raw-exit-code"
rm -rf "$RESULTS_DIR"
mkdir -p "$RESULTS_DIR"
cleanup() {
  docker rm -f "$READY" "$RUNNER" >/dev/null 2>&1 || true
  if [ "$BUILT_IMAGE" = true ]; then
    docker rmi "$IMAGE" >/dev/null 2>&1 || true
    rm -rf "$CONTEXT"
  fi
}
trap cleanup EXIT INT TERM

NET="$(docker inspect -f '{{range $k,$_ := .NetworkSettings.Networks}}{{$k}}{{"\n"}}{{end}}' "$FRONT" | grep -v '_backend$' | head -1)"
test -n "$NET" || { echo "Could not resolve the QA network for $FRONT." >&2; docker ps; exit 1; }

docker run --rm --name "$READY" --network "$NET" "$IMAGE" \
  sh -c 'for i in $(seq 1 40); do curl -sf "http://'$FRONT':8080/" -o /dev/null && exit 0; sleep 3; done; exit 1'

ADMIN_PASSWORD="$(cat "$ADMIN_PASSWORD_FILE")"
sh "$WORKSPACE/deploy/scripts/run-with-progress.sh" "Playwright E2E tests ($RUN_LABEL)" \
  docker run --name "$RUNNER" --network "$NET" \
  -e E2E_FRONTEND_URL="http://$FRONT:8080" \
  -e E2E_BACKEND_URL="http://$BACK:8080" \
  -e E2E_ADMIN_USER=admin \
  -e E2E_ADMIN_PASSWORD="$ADMIN_PASSWORD" \
  -e E2E_PRESERVE_DATABASE="$PRESERVE_DATABASE" \
  -e E2E_WORKERS="$E2E_WORKERS" \
  -e E2E_PREVIOUS_PAYLOAD_ONLY="$PREVIOUS_PAYLOAD_ONLY" \
  -e AETHEUS_E2E_RESULTS_DIR=/tmp/aetheus-e2e-results \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  "$IMAGE" \
  sh -c 'set -u
    RESULTS=/tmp/aetheus-e2e-results
    rm -rf "$RESULTS"
    mkdir -p "$RESULTS"
    TRX="$RESULTS/e2e.trx"
    RAW_EXIT=0
    dotnet test tests/Aetheus.E2E/Aetheus.E2E.csproj -c Release --no-build --no-restore \
      --logger "console;verbosity=minimal" --logger "trx;LogFileName=e2e.trx" \
      --results-directory "$RESULTS" --nologo \
      -- NUnit.NumberOfTestWorkers="$E2E_WORKERS" || RAW_EXIT=$?
    printf "%s\n" "$RAW_EXIT" > "$RESULTS/raw-exit-code"'

# Keep the browser process non-root and avoid host/container UID mapping entirely. The Docker
# daemon materializes the completed evidence only after the runner container has stopped.
docker cp "$RUNNER:/tmp/aetheus-e2e-results/." "$RESULTS_DIR/"

test -s "$RAW_EXIT_FILE"
RAW_TEST_EXIT="$(tr -d '\r\n' < "$RAW_EXIT_FILE")"
NODE="$(sh "$WORKSPACE/deploy/scripts/ensure-node-runtime.sh")"
STATUS="$("$NODE" "$WORKSPACE/deploy/scripts/classify-dotnet-test-result.mjs" "$RAW_TEST_EXIT" "$TRX" -)"

trap - EXIT INT TERM
cleanup
# Skipped tests are NotExecuted in the TRX, never Passed; say how many, and which, so a skip (a
# NewSinceDeployedBaseline marker on a payload-only V-1 above all) is read in the gate log itself.
SKIPPED="$(grep -o 'notExecuted="[0-9]*"' "$TRX" | head -n 1 | tr -dc '0-9')"
echo "E2E suite $RUN_LABEL produced an executed-test proof against $BACK with findings status $STATUS, ${SKIPPED:-0} test(s) skipped."
grep -o '<UnitTestResult [^>]*outcome="NotExecuted"[^>]*>' "$TRX" | grep -o 'testName="[^"]*"' | sed 's/^/  skipped: /' || true
# The failed names, for the gate script to publish beside its status.
if [ "$STATUS" != 0 ]; then
  "$NODE" "$WORKSPACE/deploy/scripts/list-failed-tests.mjs" record "$TRX" "$RESULTS_DIR/failed-tests.txt" \
    || echo "The failed test names could not be read from $TRX." >&2
fi
[ "$STATUS" = 0 ] || exit 10
