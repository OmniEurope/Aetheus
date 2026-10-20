#!/bin/sh
# Standalone production deploy used only by aetheus-release-fast.
# The preceding step built both immutable images from BUILD_SOURCEVERSION.
set -eu

# Read ENV_FILE and UPSTREAM_DEFINE from the production library so this path shares the
# environment's deployment identity. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
required_vars="WORKSPACE BUILD_SOURCEVERSION ENV_FILE COMPOSE_PROJECT BG_COMPOSE PORT_FRONT_BLUE PORT_BACK_BLUE PORT_FRONT_GREEN PORT_BACK_GREEN UPSTREAM_CONF UPSTREAM_LINK UPSTREAM_DEFINE PUBLIC_APP_URL PUBLIC_API_URL AETHEUS_DEPLOY_TARGET"
for name in $required_vars; do
  eval "value=\${$name:-}"
  if [ -z "$value" ]; then
    echo "Required variable is missing: $name" >&2
    exit 1
  fi
done

SOURCE_COMMIT="$(git rev-parse HEAD)"
case "${#SOURCE_COMMIT}" in 40|64) ;; *) echo "Invalid source revision length." >&2; exit 1 ;; esac
case "$SOURCE_COMMIT" in *[!0-9a-fA-F]*) echo "Invalid source revision." >&2; exit 1 ;; esac
if [ "$SOURCE_COMMIT" != "$BUILD_SOURCEVERSION" ]; then
  echo "Checked-out revision does not match BUILD_SOURCEVERSION." >&2
  exit 1
fi

# shellcheck source=deploy-identity.sh
. "$WORKSPACE/deploy/scripts/deploy-identity.sh"
deploy_image_repos
export AETHEUS_BACK_IMAGE="$BACK_IMAGE_REPO:${SOURCE_COMMIT}"
export AETHEUS_FRONT_IMAGE="$FRONT_IMAGE_REPO:${SOURCE_COMMIT}"
BACK_IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$AETHEUS_BACK_IMAGE")"
FRONT_IMAGE_ID="$(docker image inspect --format '{{.Id}}' "$AETHEUS_FRONT_IMAGE")"
[ -n "$BACK_IMAGE_ID" ] && [ -n "$FRONT_IMAGE_ID" ] || {
  echo "Fast-release images are missing after the build step." >&2
  exit 1
}

STATE_DIR="$(dirname "$ENV_FILE")"
COLOR_FILE="$STATE_DIR/live-color"
TRANSACTION_DIR="$STATE_DIR/fast-deployment-transaction"
[ -f "$ENV_FILE" ] || {
  echo "Fast release requires an existing production installation and $ENV_FILE." >&2
  exit 1
}

LIVE="$(cat "$COLOR_FILE" 2>/dev/null || echo none)"
case "$LIVE" in
  blue) IDLE=green; IDLE_FRONT="$PORT_FRONT_GREEN"; IDLE_BACK="$PORT_BACK_GREEN" ;;
  green) IDLE=blue; IDLE_FRONT="$PORT_FRONT_BLUE"; IDLE_BACK="$PORT_BACK_BLUE" ;;
  *) echo "Fast release requires an existing blue-green live colour." >&2; exit 1 ;;
esac
echo ">>> live=$LIVE -> deploying idle=$IDLE"

COMPOSE="docker compose -p $COMPOSE_PROJECT --env-file $ENV_FILE -f $BG_COMPOSE"
COMPOSE_IMAGES="$($COMPOSE --profile "$IDLE" config --images)"
printf '%s\n' "$COMPOSE_IMAGES" | grep -Fx "$AETHEUS_BACK_IMAGE" >/dev/null || {
  echo "Compose does not reference the expected backend commit image." >&2
  exit 1
}
printf '%s\n' "$COMPOSE_IMAGES" | grep -Fx "$AETHEUS_FRONT_IMAGE" >/dev/null || {
  echo "Compose does not reference the expected frontend commit image." >&2
  exit 1
}

$COMPOSE up -d --wait --no-build database
APPNAME_VALUE="$(sed -n 's/^APPNAME=//p' "$ENV_FILE" | tail -n 1)"
ENV_VALUE="$(sed -n 's/^ENV=//p' "$ENV_FILE" | tail -n 1)"
DB_USER_VALUE="$(sed -n 's/^DB_USER=//p' "$ENV_FILE" | tail -n 1)"
DB_NAME_VALUE="$(sed -n 's/^DB_NAME=//p' "$ENV_FILE" | tail -n 1)"
DB_NAME_VALUE="${DB_NAME_VALUE:-$APPNAME_VALUE}"
[ -n "$APPNAME_VALUE" ] && [ -n "$ENV_VALUE" ] && [ -n "$DB_USER_VALUE" ] || {
  echo "FATAL: APPNAME, ENV or DB_USER is missing from $ENV_FILE." >&2
  exit 1
}
DB_CONTAINER="${APPNAME_VALUE}-${ENV_VALUE}-database"

if ! HISTORY_TABLE="$(docker exec "$DB_CONTAINER" psql -U "$DB_USER_VALUE" -d "$DB_NAME_VALUE" -Atc \
  "SELECT to_regclass('\"__EFMigrationsHistory\"');")"; then
  echo "FATAL: EF migration history cannot be read." >&2
  exit 1
fi
if [ -z "$HISTORY_TABLE" ]; then
  echo "FATAL: an existing fast-release installation has no EF migration history." >&2
  exit 1
fi
if ! APPLIED_MIGRATIONS="$(docker exec "$DB_CONTAINER" psql -U "$DB_USER_VALUE" -d "$DB_NAME_VALUE" -Atc \
  'SELECT "MigrationId" FROM "__EFMigrationsHistory" ORDER BY "MigrationId";')"; then
  echo "FATAL: EF migration history cannot be read." >&2
  exit 1
fi
PENDING_MIGRATION_FILES=""
for MIGRATION_FILE in "$WORKSPACE"/src/Aetheus.Back/Data/Migrations/[0-9]*_*.cs; do
  [ -f "$MIGRATION_FILE" ] || continue
  case "$MIGRATION_FILE" in *.Designer.cs) continue ;; esac
  MIGRATION_ID="$(basename "$MIGRATION_FILE" .cs)"
  if ! printf '%s\n' "$APPLIED_MIGRATIONS" | grep -Fxq "$MIGRATION_ID"; then
    echo ">>> Pending EF migration: $MIGRATION_ID"
    PENDING_MIGRATION_FILES="$PENDING_MIGRATION_FILES $MIGRATION_FILE"
  fi
done
# Paths are generated EF migration paths and cannot contain shell separators or whitespace.
# shellcheck disable=SC2086
sh "$WORKSPACE/deploy/scripts/check-expand-migration-up.sh" $PENDING_MIGRATION_FILES

# Require expand/contract compatibility before migration so the previous colour remains usable on
# the shared schema; this path deliberately takes no pre-migration database dump. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
echo ">>> Running the migration bundle once."
$COMPOSE --profile "$IDLE" run --rm --no-deps \
  -e AETHEUS_RUN_MIGRATIONS=true -e AETHEUS_MIGRATE_ONLY=true "back-$IDLE"
$COMPOSE --profile "$IDLE" up -d --wait --no-build
$COMPOSE --profile "$IDLE" ps

echo ">>> Readiness-checking idle colour $IDLE."
i=1
while [ "$i" -le 30 ]; do
  if curl -fsS "http://127.0.0.1:${IDLE_BACK}/health/ready" >/dev/null 2>&1; then break; fi
  if [ "$i" -eq 30 ]; then
    echo "Idle backend never became ready; Apache was not changed." >&2
    exit 1
  fi
  i=$((i + 1))
  sleep 3
done
FRONT_URL="http://127.0.0.1:${IDLE_FRONT}/"
FRONT_HTML="$(curl -fsS "$FRONT_URL")"
printf '%s\n' "$FRONT_HTML" | grep -qi '<html' || {
  echo "Idle frontend is not serving HTML; Apache was not changed." >&2
  exit 1
}
# Run 2247 shipped an index.html that still carried the SDK's "#[.{fingerprint}]" placeholders: a
# build without the fingerprint property followed by a --no-build publish. It contained <html, it
# contained a versioned component-library script, and the browser got "Blazor is not defined" - the
# two checks below are the ones that would have refused it. Only Apache is spared here; the
# containers stay up so the run can still be read.
printf '%s\n' "$FRONT_HTML" | grep -q '#\[' && {
  echo "Idle frontend index.html still contains unresolved asset placeholders; Apache was not changed." >&2
  exit 1
}
BLAZOR_BOOT_PATH="$(printf '%s\n' "$FRONT_HTML" \
  | sed -n 's/.*src="\([^"]*blazor\.webassembly[^"]*\.js\)".*/\1/p' \
  | head -n 1)"
[ -n "$BLAZOR_BOOT_PATH" ] || {
  echo "Idle frontend index.html does not reference the Blazor bootstrap script; Apache was not changed." >&2
  exit 1
}
curl -fsSI "${FRONT_URL}${BLAZOR_BOOT_PATH}" >/dev/null 2>&1 || {
  echo "Idle frontend references a Blazor bootstrap script it does not serve (${BLAZOR_BOOT_PATH}); Apache was not changed." >&2
  exit 1
}
OMNI_CSS_PATH="$(printf '%s\n' "$FRONT_HTML" \
  | sed -n 's/.*href="\([^"]*_content\/OmniEurope\.Blazor\/omnieurope\.blazor\.css\)".*/\1/p' \
  | head -n 1)"
[ -n "$OMNI_CSS_PATH" ] || {
  echo "Idle frontend does not reference the OmniEurope.Blazor stylesheet; Apache was not changed." >&2
  exit 1
}
OMNI_SMOKE_FILE="$WORKSPACE/.aetheus-omni-smoke.$$"
if ! curl -fsS "${FRONT_URL}${OMNI_CSS_PATH}" > "$OMNI_SMOKE_FILE" \
   || ! grep -q '\.omni-' "$OMNI_SMOKE_FILE" \
   || ! curl -fsS "${FRONT_URL}_content/OmniEurope.Blazor/omniInterop.js" > "$OMNI_SMOKE_FILE" \
   || ! grep -q 'focusFirstInvalid' "$OMNI_SMOKE_FILE"; then
  rm -f "$OMNI_SMOKE_FILE"
  echo "Idle frontend OmniEurope.Blazor assets are missing or incompatible; Apache was not changed." >&2
  exit 1
fi
rm -f "$OMNI_SMOKE_FILE"
FRONT_HEADERS="$(curl -fsSI "$FRONT_URL" | tr -d '\r')"
printf '%s\n' "$FRONT_HEADERS" | grep -Eqi '^Cache-Control:.*no-store' || {
  echo "Idle frontend index.html is cacheable; Apache was not changed." >&2
  exit 1
}

# A leftover transaction is not always a deployment in flight. Run 2240 flipped Apache, recorded its
# release, and then lost the step that closes the transaction (that step only writes COMMITTED and
# removes this directory). Refusing here would have locked every later fast deploy behind a
# directory nobody could remove without a shell on the host. So: a transaction whose state says the
# host was committed and whose candidate colour is the colour that is live right now is a finished
# deployment that was never closed. Close it and carry on. Anything else - another state, or a live
# colour that contradicts the transaction - is a host in an unknown shape, and that refusal stays.
if [ -e "$TRANSACTION_DIR" ]; then
  LEFTOVER_STATE="$(cat "$TRANSACTION_DIR/state" 2>/dev/null || true)"
  LEFTOVER_IDLE="$(cat "$TRANSACTION_DIR/idle" 2>/dev/null || true)"
  LEFTOVER_COMMIT="$(cat "$TRANSACTION_DIR/source-commit" 2>/dev/null || true)"
  RECORDED_COMMIT="$(cat "$STATE_DIR/source-commit" 2>/dev/null || true)"
  if [ "$LEFTOVER_STATE" = HOST_COMMITTED_PENDING_RELEASE ] && [ "$LEFTOVER_IDLE" = "$LIVE" ] \
     && { [ -z "$RECORDED_COMMIT" ] || [ "$RECORDED_COMMIT" = "$LEFTOVER_COMMIT" ]; }; then
    echo ">>> Closing the fast deployment transaction of ${LEFTOVER_COMMIT:-an unknown commit}: it went live as $LIVE and was never committed."
    printf '%s\n' COMMITTED > "$TRANSACTION_DIR/state"
    rm -rf "$TRANSACTION_DIR"
  else
    echo "FATAL: an unfinished fast deployment transaction already exists at $TRANSACTION_DIR (state=${LEFTOVER_STATE:-?}, candidate=${LEFTOVER_IDLE:-?}, live=$LIVE)." >&2
    exit 1
  fi
fi
TRANSACTION_TMP="$TRANSACTION_DIR.tmp.$$"
mkdir -m 700 "$TRANSACTION_TMP"
printf '%s\n' "$LIVE" > "$TRANSACTION_TMP/previous-live"
printf '%s\n' "$IDLE" > "$TRANSACTION_TMP/idle"
printf '%s\n' "$SOURCE_COMMIT" > "$TRANSACTION_TMP/source-commit"
if [ -L "$UPSTREAM_LINK" ]; then readlink "$UPSTREAM_LINK" > "$TRANSACTION_TMP/upstream-link-target"; fi
if [ -f "$UPSTREAM_CONF" ]; then cp "$UPSTREAM_CONF" "$TRANSACTION_TMP/upstream.before"; fi
if [ -f "$STATE_DIR/source-commit" ]; then
  cp "$STATE_DIR/source-commit" "$TRANSACTION_TMP/previous-source-commit"
else
  : > "$TRANSACTION_TMP/previous-source-commit-absent"
fi
printf '%s\n' HOST_PREPARED > "$TRANSACTION_TMP/state"
mv "$TRANSACTION_TMP" "$TRANSACTION_DIR"
UPSTREAM_BACKUP="$TRANSACTION_DIR/upstream.before"
UPSTREAM_LINK_TARGET="$(cat "$TRANSACTION_DIR/upstream-link-target" 2>/dev/null || true)"
FLIPPED=no
DEPLOY_COMMITTED=no
rollback_go_live() {
  echo ">>> Restoring the complete pre-go-live Apache state."
  if [ -f "$UPSTREAM_BACKUP" ]; then
    RESTORE_TMP="${UPSTREAM_CONF}.restore.$$"
    cp "$UPSTREAM_BACKUP" "$RESTORE_TMP"
    mv "$RESTORE_TMP" "$UPSTREAM_CONF"
  else
    rm -f "$UPSTREAM_CONF"
  fi
  rm -f "$UPSTREAM_LINK"
  if [ -n "$UPSTREAM_LINK_TARGET" ]; then ln -s "$UPSTREAM_LINK_TARGET" "$UPSTREAM_LINK"; fi
  sudo systemctl reload apache2.service || true
  echo "$LIVE" > "$COLOR_FILE"
  $COMPOSE --profile "$IDLE" stop "back-$IDLE" "front-$IDLE" || true
  rm -rf "$TRANSACTION_DIR"
}
on_deploy_exit() {
  rc=$?
  trap - EXIT
  if [ "$rc" -ne 0 ] && [ "$DEPLOY_COMMITTED" != yes ]; then
    if [ "$FLIPPED" = yes ]; then rollback_go_live; else rm -rf "$TRANSACTION_DIR"; fi
  fi
  exit "$rc"
}
trap on_deploy_exit EXIT

UPSTREAM_DEFINE="$UPSTREAM_DEFINE" UPSTREAM_CONF="$UPSTREAM_CONF" \
  sh "$WORKSPACE/deploy/scripts/render-apache-upstream.sh" \
  "$WORKSPACE/.pipeline/configs/apache/aetheus-upstream.conf" \
  "$UPSTREAM_CONF" "$IDLE_FRONT" "$IDLE_BACK" "$IDLE"
ln -sfn "$UPSTREAM_CONF" "$UPSTREAM_LINK"
if ! sudo systemctl reload apache2.service; then
  rollback_go_live
  exit 1
fi
FLIPPED=yes
echo "$IDLE" > "$COLOR_FILE"

curl_public() {
  if [ "$AETHEUS_DEPLOY_TARGET" = local ]; then
    [ -n "${TLS_CA_FILE:-}" ] && [ -f "$TLS_CA_FILE" ] || {
      echo "Local TLS trust file is missing: ${TLS_CA_FILE:-unset}" >&2
      return 1
    }
    curl --cacert "$TLS_CA_FILE" "$@"
  else
    curl "$@"
  fi
}
if ! curl_public -fsS --connect-timeout 5 --max-time 15 --retry 5 --retry-delay 2 --retry-max-time 30 --retry-connrefused -o /dev/null "$PUBLIC_APP_URL" \
   || ! curl_public -fsS --connect-timeout 5 --max-time 15 --retry 5 --retry-delay 2 --retry-max-time 30 --retry-connrefused -o /dev/null "${PUBLIC_API_URL%/}/health/ready"; then
  echo ">>> Public smoke failed; rolling back immediately." >&2
  exit 1
fi

printf '%s\n' "$SOURCE_COMMIT" > "$STATE_DIR/source-commit.fast.$$"
mv "$STATE_DIR/source-commit.fast.$$" "$STATE_DIR/source-commit"
printf '%s\n' HOST_COMMITTED_PENDING_RELEASE > "$TRANSACTION_DIR/state"
DEPLOY_COMMITTED=yes
if ! $COMPOSE --profile "$LIVE" stop "back-$LIVE" "front-$LIVE"; then
  echo "WARNING: production $SOURCE_COMMIT is active on $IDLE, but the previous $LIVE colour could not be stopped." >&2
  echo "##aetheus[setvariable name=FAST_DEPLOY_OLD_COLOR_STOPPED]false"
else
  echo "##aetheus[setvariable name=FAST_DEPLOY_OLD_COLOR_STOPPED]true"
fi
# The same confirmation window as aetheus-deploy-prod. The pipeline's Confirm stage asks a human for
# 10 minutes and rolls back without an answer; this host-side countdown covers a backend too broken
# to dispatch that rollback. It starts detached (setsid, its own log, no inherited stdout/stderr, or
# the agent would wait for it) with copies of what the rollback needs, since the workspace may be gone.
FAST_CONFIRM_MINUTES="${FAST_CONFIRM_MINUTES:-0}"
case "$FAST_CONFIRM_MINUTES" in ''|*[!0-9]*) echo "FAST_CONFIRM_MINUTES must be a whole number of minutes." >&2; FAST_CONFIRM_MINUTES=0 ;; esac
if [ "$FAST_CONFIRM_MINUTES" -gt 0 ]; then
  WATCH_DIR="$STATE_DIR/fast-confirm-$SOURCE_COMMIT"
  rm -rf "$WATCH_DIR"
  mkdir -p "$WATCH_DIR/deploy/scripts" "$WATCH_DIR/deploy/compose"
  chmod 700 "$WATCH_DIR"
  cp "$WORKSPACE/deploy/scripts/finalize-fast-release-transaction.sh" \
     "$WORKSPACE/deploy/scripts/deploy-identity.sh" \
     "$WORKSPACE/deploy/scripts/fast-release-confirm-watchdog.sh" "$WATCH_DIR/deploy/scripts/"
  cp "$WORKSPACE/$BG_COMPOSE" "$WATCH_DIR/deploy/compose/"
  {
    for name in ENV_FILE COMPOSE_PROJECT PORT_FRONT_BLUE PORT_BACK_BLUE PORT_FRONT_GREEN PORT_BACK_GREEN \
                UPSTREAM_CONF UPSTREAM_LINK BUILD_PROJECTNAME AGENT_HELPERS_DIR; do
      eval "value=\${$name:-}"
      printf "export %s='%s'\n" "$name" "$(printf '%s' "$value" | sed "s/'/'\\\\''/g")"
    done
    printf "export BUILD_SOURCEVERSION='%s'\n" "$SOURCE_COMMIT"
    printf "export BG_COMPOSE='%s'\n" "$WATCH_DIR/deploy/compose/$(basename "$BG_COMPOSE")"
  } > "$WATCH_DIR/watch.env"
  chmod 600 "$WATCH_DIR/watch.env"
  WATCH_LOG="$STATE_DIR/fast-confirm-$SOURCE_COMMIT.log"
  if command -v setsid >/dev/null 2>&1; then
    setsid sh "$WATCH_DIR/deploy/scripts/fast-release-confirm-watchdog.sh" "$WATCH_DIR" "$FAST_CONFIRM_MINUTES" \
      </dev/null >>"$WATCH_LOG" 2>&1 &
  else
    nohup sh "$WATCH_DIR/deploy/scripts/fast-release-confirm-watchdog.sh" "$WATCH_DIR" "$FAST_CONFIRM_MINUTES" \
      </dev/null >>"$WATCH_LOG" 2>&1 &
  fi
  echo ">>> Confirmation window armed: without a confirmation within $FAST_CONFIRM_MINUTES minutes, this host returns to $LIVE by itself (log: $WATCH_LOG)."
fi
echo ">>> Fast deployment completed on $IDLE with $SOURCE_COMMIT."
