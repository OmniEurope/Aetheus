#!/usr/bin/env sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

ACTION="${1:?commit or rollback is required}"
ENV_FILE="${ENV_FILE:?ENV_FILE is required}"
STATE_DIR="$(dirname "$ENV_FILE")"
TRANSACTION_DIR="$STATE_DIR/fast-deployment-transaction"
EXPECTED_COMMIT="${BUILD_SOURCEVERSION:?BUILD_SOURCEVERSION is required}"

case "$ACTION" in
  commit)
    set +e
    if [ -d "$TRANSACTION_DIR" ] \
       && [ "$(cat "$TRANSACTION_DIR/state" 2>/dev/null)" = HOST_COMMITTED_PENDING_RELEASE ] \
       && [ "$(cat "$TRANSACTION_DIR/source-commit" 2>/dev/null)" = "$EXPECTED_COMMIT" ]; then
      printf '%s\n' COMMITTED > "$TRANSACTION_DIR/state"
      rm -rf "$TRANSACTION_DIR"
    fi
    exit 0
    ;;
  rollback) ;;
  *) echo "Action must be commit or rollback." >&2; exit 2 ;;
esac

# No transaction means no traffic switch occurred, so there is nothing to roll back and the original
# failure already determines the run's result. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
if [ ! -d "$TRANSACTION_DIR" ]; then
  echo ">>> No fast deployment transaction is open: the deployment never switched colour, nothing to roll back."
  exit 0
fi
test "$(cat "$TRANSACTION_DIR/state" 2>/dev/null)" = HOST_COMMITTED_PENDING_RELEASE \
  || { echo "FATAL: fast deployment transaction is not rollback-eligible." >&2; exit 1; }
test "$(cat "$TRANSACTION_DIR/source-commit" 2>/dev/null)" = "$EXPECTED_COMMIT" \
  || { echo "FATAL: fast deployment transaction belongs to another commit." >&2; exit 1; }

PREVIOUS_LIVE="$(cat "$TRANSACTION_DIR/previous-live")"
IDLE="$(cat "$TRANSACTION_DIR/idle")"
case "$PREVIOUS_LIVE" in
  blue) PREVIOUS_FRONT="${PORT_FRONT_BLUE:?}"; PREVIOUS_BACK="${PORT_BACK_BLUE:?}" ;;
  green) PREVIOUS_FRONT="${PORT_FRONT_GREEN:?}"; PREVIOUS_BACK="${PORT_BACK_GREEN:?}" ;;
  *) echo "FATAL: no previous colour is recorded." >&2; exit 1 ;;
esac
case "$IDLE" in blue|green) ;; *) echo "FATAL: invalid candidate colour." >&2; exit 1 ;; esac

# shellcheck source=deploy-identity.sh
. "$(dirname "$0")/deploy-identity.sh"
deploy_image_repos
export AETHEUS_BACK_IMAGE="$BACK_IMAGE_REPO:$EXPECTED_COMMIT"
export AETHEUS_FRONT_IMAGE="$FRONT_IMAGE_REPO:$EXPECTED_COMMIT"
COMPOSE="docker compose -p ${COMPOSE_PROJECT:?} --env-file $ENV_FILE -f ${BG_COMPOSE:?}"
echo ">>> Restoring the previous $PREVIOUS_LIVE colour (confirmation refused or not given in time, or release recording failed)."
$COMPOSE --profile "$PREVIOUS_LIVE" start "back-$PREVIOUS_LIVE" "front-$PREVIOUS_LIVE"
i=1
while [ "$i" -le 30 ]; do
  if curl -fsS "http://127.0.0.1:${PREVIOUS_BACK}/health/ready" >/dev/null 2>&1 \
     && curl -fsS "http://127.0.0.1:${PREVIOUS_FRONT}/" >/dev/null 2>&1; then break; fi
  if [ "$i" -eq 30 ]; then echo "FATAL: previous colour did not recover." >&2; exit 1; fi
  i=$((i + 1))
  sleep 3
done

if [ -f "$TRANSACTION_DIR/upstream.before" ]; then
  cp "$TRANSACTION_DIR/upstream.before" "${UPSTREAM_CONF:?}.rollback.$$"
  mv "${UPSTREAM_CONF}.rollback.$$" "$UPSTREAM_CONF"
else
  rm -f "${UPSTREAM_CONF:?}"
fi
rm -f "${UPSTREAM_LINK:?}"
PREVIOUS_LINK_TARGET="$(cat "$TRANSACTION_DIR/upstream-link-target" 2>/dev/null || true)"
if [ -n "$PREVIOUS_LINK_TARGET" ]; then ln -s "$PREVIOUS_LINK_TARGET" "$UPSTREAM_LINK"; fi
sudo systemctl reload apache2.service
printf '%s\n' "$PREVIOUS_LIVE" > "$STATE_DIR/live-color.rollback.$$"
mv "$STATE_DIR/live-color.rollback.$$" "$STATE_DIR/live-color"
$COMPOSE --profile "$IDLE" stop "back-$IDLE" "front-$IDLE"

if [ -f "$TRANSACTION_DIR/previous-source-commit" ]; then
  cp "$TRANSACTION_DIR/previous-source-commit" "$STATE_DIR/source-commit.rollback.$$"
  mv "$STATE_DIR/source-commit.rollback.$$" "$STATE_DIR/source-commit"
elif [ -f "$TRANSACTION_DIR/previous-source-commit-absent" ]; then
  rm -f "$STATE_DIR/source-commit"
fi
printf '%s\n' ROLLED_BACK > "$TRANSACTION_DIR/state"
rm -rf "$TRANSACTION_DIR"
