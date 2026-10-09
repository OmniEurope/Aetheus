# SPDX-License-Identifier: EUPL-1.2
#
# Verifies the restored payload's provenance and decides how QA will run: a full N/N-1 compatibility
# pass, or the bootstrap attestation used when there is no previous release to roll back to. Then
# loads the images and writes the environment the disposable QA stacks share.
#
# This was 183 lines inlined in .pipeline/aetheus-qa.yaml, the second largest shell block in the
# repository and the one that decides which qualification a candidate actually gets. A pipeline
# definition is not versioned, not tested and not reusable; moved here, it can be read and called.
# The body is relocated unchanged, deliberately: this is a move, not a rewrite.
#
# Inputs, all read from the environment (none positional):
#   BUILD_PROJECTNAME  the project name the QA stack, database and user are derived from
#   WORKSPACE            the run's checkout, and the parent of .pipeline-artifacts (required)
#   BUILD_BUILDID        the globally unique run id; QA derives its port slot and project names from it
#   BUILD_SOURCEVERSION  the immutable revision this run is pinned to
#
# Publishes the compatibility mode and the derived QA ports as run output variables.
set -eu
RUN="${BUILD_BUILDID:-}"
case "$RUN" in ''|*[!0-9]*) echo "BUILD_BUILDID is invalid." >&2; exit 1 ;; esac
. deploy/scripts/qa/qa-stack-identity.sh
STACK="$(qa_stack "$RUN")"
PORT_SLOT=$((RUN % 20000))
QA_PORT_FRONT_VALUE=$((20000 + PORT_SLOT * 2))
QA_PORT_BACK_VALUE=$((QA_PORT_FRONT_VALUE + 1))
echo "QA run ${RUN} uses isolated host ports ${QA_PORT_FRONT_VALUE}/${QA_PORT_BACK_VALUE}."
echo "##aetheus[setvariable name=QA_PORT_FRONT]${QA_PORT_FRONT_VALUE}"
echo "##aetheus[setvariable name=QA_PORT_BACK]${QA_PORT_BACK_VALUE}"
echo "##aetheus[setvariable name=AETHEUS_DAST_TARGET_URL]http://127.0.0.1:${QA_PORT_FRONT_VALUE}"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_0]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-0.json"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_1]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-1.json"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_2]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-2.json"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_3]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-3.json"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_4]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-4.json"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_5]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-5.json"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_6]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-6.json"
echo "##aetheus[setvariable name=AETHEUS_DAST_API_SPECIFICATION_URL_7]http://127.0.0.1:${QA_PORT_BACK_VALUE}/openapi/dast-7.json"
CURRENT="$(tr -d '\r\n' < "$WORKSPACE/.pipeline-artifacts/source-commit")"
CURRENT_CONTRACT="$(tr -d '\r\n' < "$WORKSPACE/.pipeline-artifacts/qa-rollback-contract" 2>/dev/null || true)"
PREVIOUS_PAYLOAD=0
if [ -d "$WORKSPACE/.nminus1" ] && [ -n "$(find "$WORKSPACE/.nminus1" -mindepth 1 -print -quit)" ]; then
  PREVIOUS_PAYLOAD=1
fi
PREVIOUS_CONTRACT="$(tr -d '\r\n' < "$WORKSPACE/.nminus1/.pipeline-artifacts/qa-rollback-contract" 2>/dev/null || true)"
test "$CURRENT_CONTRACT" = 3 || { echo "V artifact does not support QA rollback contract v3." >&2; exit 1; }
case "${#CURRENT}" in 40|64) ;; *) echo "Invalid V artifact commit length." >&2; exit 1 ;; esac
case "$CURRENT" in *[!0-9a-fA-F]*) echo "Invalid V artifact commit." >&2; exit 1 ;; esac
if [ -n "${BUILD_SOURCEVERSION:-}" ] && [ "$BUILD_SOURCEVERSION" != "$CURRENT" ]; then
  echo "V artifact does not match the pinned run commit." >&2; exit 1
fi
BOOTSTRAP=0
PREVIOUS=""
PREVIOUS_BROWSER_COMMIT=""
# 1 when V-1 is a payload-only fast baseline: its E2E then runs THIS suite, and the tests marked
# NewSinceDeployedBaseline (features V-1 does not have) are reported skipped instead of failing.
PREVIOUS_PAYLOAD_ONLY=0
rm -rf "$WORKSPACE/.nminus1-source"
if [ "$PREVIOUS_PAYLOAD" = 1 ]; then
  case "$PREVIOUS_CONTRACT" in
    3|4) ;;
    *)
      echo "Retained V-1 payload exists but its rollback contract is missing or invalid; refusing bootstrap mode." >&2
      exit 1
      ;;
  esac
  test -s "$WORKSPACE/.nminus1/.pipeline-artifacts/source-commit" || {
    echo "Retained V-1 rollback contract has no source commit." >&2
    exit 1
  }
  PREVIOUS="$(tr -d '\r\n' < "$WORKSPACE/.nminus1/.pipeline-artifacts/source-commit")"
  case "${#PREVIOUS}" in 40|64) ;; *) echo "Invalid V-1 artifact commit length." >&2; exit 1 ;; esac
  case "$PREVIOUS" in *[!0-9a-fA-F]*) echo "Invalid V-1 artifact commit." >&2; exit 1 ;; esac
  test "$CURRENT" != "$PREVIOUS" || { echo "V and V-1 resolve to the same commit." >&2; exit 1; }
fi
# The 2026-10-06 history rewrite (secret scrub) left the deployed release 76a48b8a on a commit the
# new history does not contain, so no N/N-1 pass can be built against it. That exact commit, and
# only it, falls back to bootstrap; the seal still refuses Bootstrap unless the candidate was
# launched with allowBootstrap=true. Once a release from the new history is deployed it becomes
# V-1 and this exemption can no longer match.
REWRITTEN_HISTORY_BASELINE=76a48b8ac1978c24dd3dc2bb03eb813f99ed124b
if [ "$PREVIOUS_PAYLOAD" = 1 ] && ! git cat-file -e "${PREVIOUS}^{commit}" 2>/dev/null; then
  test "$PREVIOUS" = "$REWRITTEN_HISTORY_BASELINE" || {
    echo "Retained V-1 commit is outside the configured Git history window." >&2; exit 1;
  }
  echo ">>> Retained V-1 $PREVIOUS predates the history rewrite and is absent from the new history."
  PREVIOUS_PAYLOAD=0
  PREVIOUS=""
fi
if [ "$PREVIOUS_PAYLOAD" = 1 ]; then
  git merge-base --is-ancestor "$PREVIOUS" "$CURRENT" || {
    echo "Retained V-1 is not an ancestor of V." >&2; exit 1;
  }
  RUNTIME_ROOT="$WORKSPACE/.nminus1"
  RUNTIME_SOURCE_COMMIT="$PREVIOUS"
  PREVIOUS_BROWSER_COMMIT="$PREVIOUS"
  if [ "$PREVIOUS_CONTRACT" = 4 ]; then
    RUNTIME_ROOT="$WORKSPACE"
    RUNTIME_SOURCE_COMMIT="$CURRENT"
    PREVIOUS_BROWSER_COMMIT="$CURRENT"
    PREVIOUS_PAYLOAD_ONLY=1
    echo ">>> Payload-only fast baseline found; Candidate's current QA runtime will exercise the retained V-1 application images."
  fi
  mkdir -p "$WORKSPACE/.nminus1-source"
  git archive "$RUNTIME_SOURCE_COMMIT" | tar -x -C "$WORKSPACE/.nminus1-source"
  for PATH_TO_COPY in \
    tests/Aetheus.Back.IntegrationTests/bin/Release/net10.0 \
    tests/Aetheus.Back.IntegrationTests/obj/project.assets.json \
    tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.props \
    tests/Aetheus.Back.IntegrationTests/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.targets
  do
    test -e "$RUNTIME_ROOT/$PATH_TO_COPY" || { echo "QA runtime misses $PATH_TO_COPY." >&2; exit 1; }
    rm -rf "$WORKSPACE/.nminus1-source/$PATH_TO_COPY"
    mkdir -p "$(dirname "$WORKSPACE/.nminus1-source/$PATH_TO_COPY")"
    cp -a "$RUNTIME_ROOT/$PATH_TO_COPY" "$WORKSPACE/.nminus1-source/$PATH_TO_COPY"
  done
  echo ">>> Compatible V-1 artifact found; the full N/N-1 gate is mandatory."
else
  BOOTSTRAP=1
  echo ">>> No compatible V-1 rollback-contract artifact exists."
  echo ">>> Honest bootstrap mode: V integration/E2E remain mandatory; this successful release becomes the next run's V-1."
fi
gzip -dc "$WORKSPACE/.pipeline-artifacts/aetheus-back.tar.gz" | docker load
gzip -dc "$WORKSPACE/.pipeline-artifacts/aetheus-front.tar.gz" | docker load
# The browser archive is restored whole (BrowserRuntime-artifacts), with the digest CI wrote beside it.
verify_browser_smoke_digest() {
  test -s "$1" || { echo "$2 browser runtime archive is missing." >&2; exit 1; }
  test -s "$1.sha256" || { echo "$2 browser runtime archive has no recorded digest." >&2; exit 1; }
  test "$(sha256sum "$1" | cut -d ' ' -f 1)" = "$(tr -d '\r\n' < "$1.sha256")" || {
    echo "$2 browser runtime archive does not match the digest recorded in CI." >&2; exit 1;
  }
}
verify_browser_smoke_digest "$WORKSPACE/.browser-current/.pipeline-artifacts/aetheus-browser-smoke.tar.gz" V
mv "$WORKSPACE/.browser-current/.pipeline-artifacts/aetheus-browser-smoke.tar.gz" \
  "$WORKSPACE/.pipeline-artifacts/aetheus-browser-smoke.tar.gz"
import_browser_smoke() {
  ARCHIVE="$1"
  IMAGE="$2"
  COMMIT="$3"
  gzip -t "$ARCHIVE"
  gzip -dc "$ARCHIVE" | docker import \
    --change 'ENV DOTNET_ROOT=/usr/lib/dotnet-10' \
    --change 'ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright' \
    --change 'WORKDIR /src' \
    --change 'USER pwuser' \
    --change "LABEL org.opencontainers.image.revision=$COMMIT" \
    - "$IMAGE" >/dev/null
  test "$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$IMAGE")" = "$COMMIT"
}
import_browser_smoke \
  "$WORKSPACE/.pipeline-artifacts/aetheus-browser-smoke.tar.gz" \
  "aetheus-browser-smoke:$CURRENT" "$CURRENT"
if [ "$BOOTSTRAP" = 0 ]; then
  gzip -dc "$WORKSPACE/.nminus1/.pipeline-artifacts/aetheus-back.tar.gz" | docker load
  gzip -dc "$WORKSPACE/.nminus1/.pipeline-artifacts/aetheus-front.tar.gz" | docker load
  if [ "$PREVIOUS_CONTRACT" = 3 ]; then
    verify_browser_smoke_digest "$WORKSPACE/.nminus1/.pipeline-artifacts/aetheus-browser-smoke.tar.gz" V-1
    import_browser_smoke \
      "$WORKSPACE/.nminus1/.pipeline-artifacts/aetheus-browser-smoke.tar.gz" \
      "aetheus-browser-smoke:$PREVIOUS" "$PREVIOUS"
  fi
fi
printf '%s\n' "$CURRENT" > "$WORKSPACE/.qa-current-commit"
printf '%s\n' "$PREVIOUS" > "$WORKSPACE/.qa-previous-commit"
printf '%s\n' "$PREVIOUS_BROWSER_COMMIT" > "$WORKSPACE/.qa-previous-browser-commit"
printf '%s\n' "$BOOTSTRAP" > "$WORKSPACE/.qa-bootstrap-mode"
printf '%s\n' "$PREVIOUS_PAYLOAD_ONLY" > "$WORKSPACE/.qa-previous-payload-only"
AGENT_BOOTSTRAP="$BOOTSTRAP"
if [ "$BOOTSTRAP" = 0 ]; then
  rm -rf "$WORKSPACE/.nminus1-agent-release"
  NODE="$(sh deploy/scripts/ensure-node-runtime.sh)"
  sh deploy/scripts/extract-agent-release-from-image.sh \
    "aetheus-back:${PREVIOUS}" "$WORKSPACE/.nminus1-agent-release" "$PREVIOUS" "$NODE" \
    full historical-compatible
  if [ "$PREVIOUS_CONTRACT" = 3 ]; then
    test "$(tr -d '\r\n' < "$WORKSPACE/.nminus1/.pipeline-artifacts/agent-protocol-contract" 2>/dev/null || true)" = 1 || {
      echo "Retained V-1 payload exists but its AgentRelease protocol contract is incomplete." >&2
      exit 1
    }
    test -s "$WORKSPACE/.nminus1/.pipeline-artifacts/agent-release/agent-release-manifest.json" || {
      echo "Retained V-1 payload exists but its AgentRelease manifest is incomplete." >&2
      exit 1
    }
    cmp "$WORKSPACE/.nminus1/.pipeline-artifacts/agent-release/agent-release-manifest.json" \
      "$WORKSPACE/.nminus1-agent-release/agent-release-manifest.json" || {
      echo "Retained V-1 AgentRelease manifest does not match its immutable backend image." >&2
      exit 1
    }
  fi
fi
printf '%s\n' "$AGENT_BOOTSTRAP" > "$WORKSPACE/.qa-agent-bootstrap-mode"
ADMIN_PWD="$(openssl rand -hex 16)"
DB_PWD="$(openssl rand -hex 16)"
printf '%s\n' "$ADMIN_PWD" > "$WORKSPACE/.qa-admin-pwd"
cat > "$WORKSPACE/.env-qa-rollback" <<EOF
APPNAME=${QA_APP}
ENV=qa-rollback-${RUN}
PORT_FRONT=${QA_PORT_FRONT_VALUE}
PORT_BACK=${QA_PORT_BACK_VALUE}
API_BASE_URL=http://${STACK}-back:8080
APP_VERSION=rollback-gate-${RUN}
RATE_LIMITING_DISABLED=true
ASPNETCORE_ENVIRONMENT=Development
AETHEUS_ENVIRONMENT_TIER=qa
SEED_DEMO=true
SEED_DEMO_DATE=2026-07-14
SERVER_HEARTBEAT_TIMEOUT=08:00:00
DB_NAME=${QA_DB_NAME}
DB_USER=${QA_DB_USER}
DB_PASSWORD=${DB_PWD}
JWT_KEY=$(openssl rand -hex 32)
ADMIN_PASSWORD=${ADMIN_PWD}
ENCRYPTION_KEY=$(openssl rand -hex 32)
ENCRYPTION_SALT=$(openssl rand -hex 16)
FRONT_URL=http://${STACK}-front:8080
FRONT_URL_ACCEPT=http://${STACK}-front:8080
FRONT_URL_PROD=http://${STACK}-front:8080
EOF
docker compose -p "$STACK" --env-file "$WORKSPACE/.env-qa-rollback" \
  -f deploy/compose/remote.compose.yml down -v --remove-orphans 2>/dev/null || true
