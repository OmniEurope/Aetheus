#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Everything the production environment needs BEFORE the native blue-green steps act on it.
#
# The cutover - migrate, start the idle colour, move traffic, probe, commit, roll back - belongs to
# the `bluegreen-*` and `smoke` step types through the host-bluegreen-deploy template. What stays here
# is what those types deliberately do not know about, because it is specific to THIS environment
# rather than to blue-green deployment:
#
#   1. the provenance contract: this workflow runs from the protected branch, and the payload it is
#      about to deploy is the immutable candidate that was sealed, not a local rebuild;
#   2. the production state directory, which lives outside the tree an agent reinstall purges;
#   3. secret-zero, which is reused verbatim and NEVER regenerated while persistent state exists;
#   4. the payload, loaded as images tagged by revision and verified against those tags;
#   5. the run variables Compose interpolates, and the run-local upstream template the switch renders.
#
# It performs no cutover and holds no transaction: a failure here leaves the live colour serving.
set -eu

fail() {
  echo "FATAL: $*" >&2
  exit 1
}

# --- 1. Provenance -------------------------------------------------------------------------------
[ "${AETHEUS_USE_PIPELINE_ARTIFACTS:-false}" = true ] \
  || fail "Production deployment requires the immutable CI artifact; local image builds are forbidden."
# Require the deployment instructions from develop and verify that it contains the release revision;
# main advances only after successful deployment so it records the live version. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
[ "${BUILD_SOURCEBRANCH:-}" = develop ] \
  || fail "Production deployment workflow must run from the develop branch."

WORKSPACE="${WORKSPACE:?WORKSPACE is required}"
ARTIFACT_DIR="$WORKSPACE/.pipeline-artifacts"
ARTIFACT_COMMIT="$(tr -d '\r\n' < "$ARTIFACT_DIR/source-commit")"
[ "${#ARTIFACT_COMMIT}" -ge 7 ] || fail "CI artifact has no valid source revision."
case "$ARTIFACT_COMMIT" in *[!0-9a-fA-F]*) fail "CI artifact source revision is not hexadecimal." ;; esac
# Two distinct provenances, deliberately not compared to each other (M-051): the workflow's own
# revision says which pipeline definition is running, the artifact's says which candidate is being
# promoted. They are legitimately different.
echo ">>> Deploy workflow ${BUILD_SOURCEVERSION:-unknown} is promoting immutable candidate source $ARTIFACT_COMMIT"

# Resolve production state paths from the environment library so host-specific locations remain
# configurable. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
ENV_FILE="${ENV_FILE:?ENV_FILE is required}"
LEGACY_ENV_FILE="${LEGACY_ENV_FILE:-}"
DECLARED_STATE_DIR="${STATE_DIR:-}"
STATE_DIR="$(dirname "$ENV_FILE")"
# The blue-green steps are handed STATE_DIR and this script works in ENV_FILE's directory: the two
# must be one directory, or the journal and the secrets would live apart.
[ -z "$DECLARED_STATE_DIR" ] || [ "$DECLARED_STATE_DIR" = "$STATE_DIR" ] \
  || fail "ENV_FILE ($ENV_FILE) does not live in STATE_DIR ($DECLARED_STATE_DIR)."
# Shape, not one literal path. The boundary this guard exists for is that the state directory is a
# real, direct child of /var/lib and not something a mis-set variable turned into /etc or a home
# directory: files are written and removed under it. Pinning the exact name on top of that only made
# the library unable to name its own directory.
case "$STATE_DIR" in
  *..*) fail "Production state directory contains a traversal: $STATE_DIR" ;;
  /var/lib/*/*) fail "Production state directory must be a direct child of /var/lib: $STATE_DIR" ;;
  /var/lib/?*) ;;
  *) fail "Production state directory must live under /var/lib: $STATE_DIR" ;;
esac
[ ! -L "$STATE_DIR" ] || fail "Production state directory is symbolic."

for command_name in awk cat chmod cmp cp date dirname docker find grep install mv openssl sed stat tar tr; do
  command -v "$command_name" >/dev/null || fail "$command_name is required to prepare production."
done
docker --version

# --- 3. Payload, loaded and verified by revision --------------------------------------------------
# Loaded before the state bootstrap below, which needs the backend image to run its one privileged
# step from an immutable, already-verified artifact rather than from anything on the host.
# shellcheck source=deploy-identity.sh
. "$WORKSPACE/deploy/scripts/deploy-identity.sh"
deploy_image_repos
gzip -dc "$ARTIFACT_DIR/aetheus-back.tar.gz" | docker load
gzip -dc "$ARTIFACT_DIR/aetheus-front.tar.gz" | docker load
gzip -dc "$ARTIFACT_DIR/aetheus-browser-smoke.tar.gz" | docker import \
  --change 'ENV DOTNET_ROOT=/usr/lib/dotnet-10' \
  --change 'ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright' \
  --change 'WORKDIR /src' \
  --change 'USER pwuser' \
  --change "LABEL org.opencontainers.image.revision=$ARTIFACT_COMMIT" \
  - "$PROJECT_SLUG-browser-smoke:${ARTIFACT_COMMIT}" >/dev/null

AETHEUS_BACK_IMAGE="$BACK_IMAGE_REPO:${ARTIFACT_COMMIT}"
AETHEUS_FRONT_IMAGE="$FRONT_IMAGE_REPO:${ARTIFACT_COMMIT}"
AETHEUS_BROWSER_SMOKE_IMAGE="$PROJECT_SLUG-browser-smoke:${ARTIFACT_COMMIT}"
for image in "$AETHEUS_BACK_IMAGE" "$AETHEUS_FRONT_IMAGE" "$AETHEUS_BROWSER_SMOKE_IMAGE"; do
  [ "$(docker image inspect --format '{{index .Config.Labels "org.opencontainers.image.revision"}}' "$image")" \
    = "$ARTIFACT_COMMIT" ] || fail "Image revision is invalid: $image"
done

# Read the version from the verified candidate image's agent manifest so the site and both agents
# report the same built version (ADR-033). See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
APP_VERSION="$(docker run --rm --network none --read-only --cap-drop ALL --security-opt no-new-privileges \
  --entrypoint cat "$AETHEUS_BACK_IMAGE" /app/wwwroot/downloads/agent-release-manifest.json \
  | sed -n 's/^[[:space:]]*"softwareVersion":[[:space:]]*"\([^"]*\)".*/\1/p' | head -n 1)"
case "$APP_VERSION" in
  ''|*[!0-9A-Za-z._-]*) fail "The candidate's agent manifest names no valid version: '$APP_VERSION'." ;;
esac
echo "##aetheus[setvariable name=APP_VERSION]$APP_VERSION"
echo ">>> Candidate version $APP_VERSION"

# The first canonical deploy can run on an agent installed before the dedicated production-state
# directory existed. The agent user cannot create children of /var/lib, but it already owns the Docker
# socket for this deployment. A root process inside that immutable image, with a bind limited to the
# one intended directory, only fixes ownership and mode so every later secret operation stays
# unprivileged and fail-closed. It refuses outright to remap a directory that already holds anything.
# The agent names its own work directory on every task (AETHEUS_AGENT_WORK_DIRECTORY), so the host
# layout is not restated here.
AGENT_WORK_DIR="${AETHEUS_AGENT_WORK_DIRECTORY:?AETHEUS_AGENT_WORK_DIRECTORY is required}"
[ -d "$AGENT_WORK_DIR" ] && [ ! -L "$AGENT_WORK_DIR" ] \
  || fail "Canonical agent work directory is missing or symbolic."
AGENT_UID="$(stat -c %u "$AGENT_WORK_DIR")"
AGENT_GID="$(stat -c %g "$AGENT_WORK_DIR")"
case "$AGENT_UID" in ''|*[!0-9]*) fail "Invalid agent uid." ;; esac
case "$AGENT_GID" in ''|*[!0-9]*) fail "Invalid agent gid." ;; esac
STATE_BOOTSTRAP_REQUIRED=false
if [ -e "$STATE_DIR" ]; then
  [ -d "$STATE_DIR" ] || fail "Production state path is not a real directory."
  STATE_UID="$(stat -c %u "$STATE_DIR")"
  STATE_GID="$(stat -c %g "$STATE_DIR")"
  if [ "$STATE_UID:$STATE_GID" != "$AGENT_UID:$AGENT_GID" ]; then
    if find "$STATE_DIR" -mindepth 1 -print -quit | grep -q .; then
      fail "Refusing to remap a non-empty production state directory."
    fi
    STATE_BOOTSTRAP_REQUIRED=true
  else
    chmod 700 "$STATE_DIR"
  fi
else
  STATE_BOOTSTRAP_REQUIRED=true
fi
if [ "$STATE_BOOTSTRAP_REQUIRED" = true ]; then
  docker run --rm --network none --read-only --user 0:0 \
    --cap-drop ALL --cap-add CHOWN --cap-add FOWNER \
    --security-opt no-new-privileges --pids-limit 16 \
    --entrypoint /bin/sh --volume "$STATE_DIR:/state" \
    "$AETHEUS_BACK_IMAGE" -ceu \
    'if find /state -mindepth 1 -print -quit | grep -q .; then exit 41; fi; chown "$1:$2" /state; chmod 700 /state' \
    aetheus-state-bootstrap "$AGENT_UID" "$AGENT_GID"
fi
[ -d "$STATE_DIR" ] && [ ! -L "$STATE_DIR" ] \
  && [ "$(stat -c %u "$STATE_DIR")" = "$AGENT_UID" ] \
  && [ "$(stat -c %g "$STATE_DIR")" = "$AGENT_GID" ] \
  && [ "$(stat -c %a "$STATE_DIR")" = 700 ] \
  || fail "Production state directory bootstrap did not establish the required ownership and mode."

# --- 4. Secret-zero: migrate once, reuse verbatim, fail closed ------------------------------------
# Dated copies are named after the file they copy: .env-prod-20260912T101500Z in production.
ENV_BACKUP_PREFIX="$(basename "$ENV_FILE")"
[ ! -L "$ENV_FILE" ] || fail "Production environment file must not be a symbolic link."
if [ -n "$LEGACY_ENV_FILE" ]; then
  [ ! -L "$LEGACY_ENV_FILE" ] || fail "Legacy production environment file must not be a symbolic link."
  if [ -f "$ENV_FILE" ] && [ -f "$LEGACY_ENV_FILE" ] && ! cmp -s "$ENV_FILE" "$LEGACY_ENV_FILE"; then
    fail "Canonical and legacy production environments differ; refusing automatic selection."
  fi
  if [ ! -f "$ENV_FILE" ] && [ -f "$LEGACY_ENV_FILE" ]; then
    echo ">>> Migrating production secrets to $ENV_FILE"
    cp -p "$LEGACY_ENV_FILE" "$ENV_FILE.tmp.$$"
    chmod 600 "$ENV_FILE.tmp.$$"
    mv "$ENV_FILE.tmp.$$" "$ENV_FILE"
  fi
fi
if [ ! -f "$ENV_FILE" ]; then
  LATEST_ENV_BACKUP="$(find "$STATE_DIR" -maxdepth 1 -type f -name "$ENV_BACKUP_PREFIX-????????T??????Z" \
    -printf '%T@ %p\n' 2>/dev/null | sort -nr | sed -n '1s/^[^ ]* //p')"
  if [ -n "$LATEST_ENV_BACKUP" ]; then
    echo ">>> Restoring production secrets from installation backup $LATEST_ENV_BACKUP"
    cp -p "$LATEST_ENV_BACKUP" "$ENV_FILE.tmp.$$"
    chmod 600 "$ENV_FILE.tmp.$$"
    mv "$ENV_FILE.tmp.$$" "$ENV_FILE"
  fi
fi
if [ ! -f "$ENV_FILE" ]; then
  # Regeneration would produce a DB_PASSWORD the existing volume rejects and an ENCRYPTION_KEY that
  # cannot read the existing rows: the two could never be reconciled. Production data is
  # irreplaceable, so this fails closed rather than recreating an environment nothing can open.
  PRODUCTION_STATE_PRESENT=false
  for RESOURCE in db-data git-repos dp-keys artifacts packages; do
    if docker volume inspect "${COMPOSE_PROJECT:?COMPOSE_PROJECT is required}-$RESOURCE" >/dev/null 2>&1; then
      PRODUCTION_STATE_PRESENT=true
    fi
  done
  if docker ps -a --format '{{.Names}}' | grep -qE "^${COMPOSE_PROJECT}-"; then
    PRODUCTION_STATE_PRESENT=true
  fi
  if [ "$PRODUCTION_STATE_PRESENT" = true ]; then
    echo "Secret regeneration is forbidden; no valid dated installation backup was found." >&2
    fail "Persistent production state exists but $ENV_FILE is missing."
  fi
  echo ">>> First deploy: generating persistent secrets at $ENV_FILE"
  # Restored below: this mask exists for the secret file written just after it, and the
  # extraction further down creates directories. Leaving 177 set strips their execute bit, so
  # tar cannot descend into what it just made ("Cannot mkdir: Permission denied", prod run
  # 1842). Only the back-fill branch had ever run late enough to reach the extraction.
  PREPARE_UMASK="$(umask)"
  umask 177
  ENV_FILE_TMP="$ENV_FILE.first-deploy.$$"
  # Render keys and non-secret values from the versioned template; generate each declared secret
  # locally so secrets never enter the repository. See
  # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
  ENV_SAMPLE="$WORKSPACE/deploy/env/prod.env.sample"
  if ! PUBLIC_API_URL="${PUBLIC_API_URL:?PUBLIC_API_URL is required}" \
    APP_VERSION="${APP_VERSION:?APP_VERSION is required}" \
    PUBLIC_APP_URL="${PUBLIC_APP_URL:?PUBLIC_APP_URL is required}" \
    sh "$WORKSPACE/deploy/scripts/render-env-sample.sh" "$ENV_SAMPLE" > "$ENV_FILE_TMP"; then
    rm -f "$ENV_FILE_TMP"
    fail "Could not render $ENV_SAMPLE; no production environment was written."
  fi
  chmod 600 "$ENV_FILE_TMP"
  if ! ln "$ENV_FILE_TMP" "$ENV_FILE" 2>/dev/null; then
    rm -f "$ENV_FILE_TMP"
    [ -f "$ENV_FILE" ] || fail "Could not persist production secrets."
    echo ">>> Another deployment created $ENV_FILE; reusing it."
  else
    rm -f "$ENV_FILE_TMP"
  fi
else
  echo ">>> Reusing existing secrets at $ENV_FILE (not regenerated)."
fi
umask "${PREPARE_UMASK:-$(umask)}"
# Back-fill a missing bootstrap identity key so persistent environments can derive the smoke
# identity without rotating existing secrets or sessions. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
if ! grep -q "^DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=.\+" "$ENV_FILE"; then
  echo ">>> Back-filling DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY in $ENV_FILE"
  ENV_BACKFILL_BACKUP="$STATE_DIR/$ENV_BACKUP_PREFIX-$(date -u +"%Y%m%dT%H%M%SZ")"
  [ -f "$ENV_BACKFILL_BACKUP" ] || cp -p "$ENV_FILE" "$ENV_BACKFILL_BACKUP"
  chmod 600 "$ENV_BACKFILL_BACKUP"
  # Written through a temporary copy and moved into place, so a failure mid-write cannot leave the
  # production environment truncated.
  # Restored below: this mask exists for the secret file written just after it, and the
  # extraction further down creates directories. Leaving 177 set strips their execute bit, so
  # tar cannot descend into what it just made ("Cannot mkdir: Permission denied", prod run
  # 1842). Only the back-fill branch had ever run late enough to reach the extraction.
  PREPARE_UMASK="$(umask)"
  umask 177
  ENV_BACKFILL_TMP="$ENV_FILE.backfill.$$"
  { cat "$ENV_FILE"; echo "DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY=$(openssl rand -hex 32)"; } > "$ENV_BACKFILL_TMP"
  chmod 600 "$ENV_BACKFILL_TMP"
  mv "$ENV_BACKFILL_TMP" "$ENV_FILE"
  umask "$PREPARE_UMASK"
fi
# --- GIT_RUN_TOKEN_KEY back-fill (R-464) ---
# The dedicated key pipeline clone tokens are signed with (GitLight:RunTokenKey). Drawn once per
# environment, on the first prepare that finds it absent, then reused verbatim: a value already present
# is never replaced. Drawing it rotates nothing that matters: the backend checks a clone token under
# both this key and the Auth:EncryptionKey fallback, so tokens the previous colour minted stay valid
# across the switch. An empty line counts as absent and is replaced, not duplicated.
if ! grep -q "^GIT_RUN_TOKEN_KEY=.\+" "$ENV_FILE"; then
  echo ">>> Back-filling GIT_RUN_TOKEN_KEY in $ENV_FILE"
  GIT_RUN_TOKEN_KEY_VALUE="$(openssl rand -hex 32)"
  case "$GIT_RUN_TOKEN_KEY_VALUE" in
    *[!0-9a-f]*) fail "openssl did not draw a hexadecimal GIT_RUN_TOKEN_KEY." ;;
  esac
  [ "${#GIT_RUN_TOKEN_KEY_VALUE}" -eq 64 ] || fail "openssl did not draw a 32-byte GIT_RUN_TOKEN_KEY."
  RUN_TOKEN_BACKUP="$STATE_DIR/$ENV_BACKUP_PREFIX-$(date -u +"%Y%m%dT%H%M%SZ")"
  [ -f "$RUN_TOKEN_BACKUP" ] || cp -p "$ENV_FILE" "$RUN_TOKEN_BACKUP"
  chmod 600 "$RUN_TOKEN_BACKUP"
  PREPARE_UMASK="$(umask)"
  umask 177
  RUN_TOKEN_TMP="$ENV_FILE.run-token.$$"
  { sed '/^GIT_RUN_TOKEN_KEY=$/d' "$ENV_FILE"; echo "GIT_RUN_TOKEN_KEY=$GIT_RUN_TOKEN_KEY_VALUE"; } > "$RUN_TOKEN_TMP"
  chmod 600 "$RUN_TOKEN_TMP"
  mv "$RUN_TOKEN_TMP" "$ENV_FILE"
  umask "$PREPARE_UMASK"
fi
# --- end of GIT_RUN_TOKEN_KEY back-fill ---
# Refresh only API_BASE_URL and FRONT_URL from the libraries, preserving secrets; keep a dated copy
# and replace the file atomically to avoid a partial environment. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
sh "$WORKSPACE/deploy/scripts/reconcile-env-urls.sh" "$ENV_FILE" \
  "API_BASE_URL=${PUBLIC_API_URL:?PUBLIC_API_URL is required}" \
  "FRONT_URL=${PUBLIC_APP_URL:?PUBLIC_APP_URL is required}"
for REQUIRED_SECRET in APPNAME ENV DB_USER DB_PASSWORD JWT_KEY ADMIN_PASSWORD ENCRYPTION_KEY ENCRYPTION_SALT \
  DEPLOYMENT_BOOTSTRAP_IDENTITY_KEY; do
  grep -q "^${REQUIRED_SECRET}=.\+" "$ENV_FILE" \
    || fail "$REQUIRED_SECRET is missing or empty in $ENV_FILE."
done
# The very first deploy happens after the agent installation, so the installer could not back up a
# file that did not exist yet. Create the initial dated recovery copy here; later reinstalls create
# additional dated copies before any cleanup.
if ! find "$STATE_DIR" -maxdepth 1 -type f -name "$ENV_BACKUP_PREFIX-????????T??????Z" -print -quit | grep -q .; then
  ENV_BACKUP="$STATE_DIR/$ENV_BACKUP_PREFIX-$(date -u +"%Y%m%dT%H%M%SZ")"
  cp -p "$ENV_FILE" "$ENV_BACKUP.tmp.$$"
  chmod 600 "$ENV_BACKUP.tmp.$$"
  mv "$ENV_BACKUP.tmp.$$" "$ENV_BACKUP"
  echo ">>> Created initial production-secret backup $ENV_BACKUP"
fi

# --- 5. Vitrine payload, staged for the publication stage -----------------------------------------
VITRINE_SOURCE="$WORKSPACE/.delivery-vitrine"
rm -rf "$VITRINE_SOURCE"
mkdir -p "$VITRINE_SOURCE"
tar -xzf "$ARTIFACT_DIR/aetheus-vitrine.tar.gz" -C "$VITRINE_SOURCE"
[ -s "$VITRINE_SOURCE/site/index.html" ] || fail "The candidate carries no publishable vitrine."
# The documentation site travels in the same payload. A candidate built before it existed carries no
# docs directory, so its absence is reported and skipped rather than failing the deployment.
if [ -s "$VITRINE_SOURCE/docs-site/public/index.html" ]; then
  echo ">>> Documentation site staged for publication."
else
  echo ">>> This candidate carries no documentation site; its publication will be skipped."
fi

# --- 6. The upstream template the switch renders --------------------------------------------------
# bluegreen-switch substitutes the ports and the colour and nothing else; the Define name belongs to
# the environment (UPSTREAM_DEFINE, aetheus.prod) and is resolved here, into a copy local to this run.
UPSTREAM_DEFINE="${UPSTREAM_DEFINE:?UPSTREAM_DEFINE is required}" \
  sh "$WORKSPACE/deploy/scripts/render-apache-upstream.sh" --define-only \
  "$WORKSPACE/.pipeline/configs/apache/aetheus-upstream.conf" "$WORKSPACE/.bluegreen-upstream.conf"

# --- 7. Run variables the Compose files interpolate -----------------------------------------------
# Published, not exported: the cutover steps run as separate tasks in separate processes, and a
# `compose_env:` entry the run does not define is refused before a task is ever created.
#
# The bootstrap smoke identity is deliberately NOT here. A setvariable directive is echoed into the
# run log, so it is derived by the control plane for the two steps that need it instead of travelling.
echo "##aetheus[setvariable name=AETHEUS_BACK_IMAGE]$AETHEUS_BACK_IMAGE"
echo "##aetheus[setvariable name=AETHEUS_FRONT_IMAGE]$AETHEUS_FRONT_IMAGE"
echo "##aetheus[setvariable name=BACK_IMAGE_REPO]$BACK_IMAGE_REPO"
echo "##aetheus[setvariable name=FRONT_IMAGE_REPO]$FRONT_IMAGE_REPO"
echo "##aetheus[setvariable name=SOURCE_COMMIT]$ARTIFACT_COMMIT"
echo "##aetheus[setvariable name=AETHEUS_BROWSER_SMOKE_IMAGE]$AETHEUS_BROWSER_SMOKE_IMAGE"
echo "Production host prepared for $ARTIFACT_COMMIT; the cutover steps own the environment from here."
