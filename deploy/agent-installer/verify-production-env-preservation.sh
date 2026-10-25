#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Behavioral proof for the production secret-zero guard. Runs only in a disposable local
# filesystem (normally an Ubuntu container); it extracts and executes the installer function
# itself so the proof cannot silently diverge from production code.
set -eu

INSTALLER="${1:-/src/deploy/agent-installer/install-agent-linux.sh}"
TEST_ROOT="$(mktemp -d)"
cleanup() { rm -rf -- "$TEST_ROOT"; }
trap cleanup EXIT

WORK_DIR="$TEST_ROOT/var/lib/aetheus-agent"
PRODUCTION_STATE_DIR="$TEST_ROOT/var/lib/aetheus-production"
PRODUCTION_ENV_FILE="$PRODUCTION_STATE_DIR/.env-prod"
LEGACY_PRODUCTION_ENV_FILE="$WORK_DIR/aetheus-prod/.env-prod"
AGENT_USER="aetheus-env-guard-user-does-not-exist"
AGENT_GROUP="$AGENT_USER"
log_info() { printf '[INFO] %s\n' "$1"; }
log_error() { printf '[ERROR] %s\n' "$1" >&2; }

FUNCTIONS_FILE="$TEST_ROOT/installer-function.sh"
sed -n '/^preserve_production_environment() {$/,/^}$/p' "$INSTALLER" > "$FUNCTIONS_FILE"
grep -q '^preserve_production_environment() {$' "$FUNCTIONS_FILE"
# shellcheck disable=SC1090
. "$FUNCTIONS_FILE"

# A first installation has no secret to preserve yet, but must provision the durable
# directory so the non-root pipeline runner can create the initial canonical file.
preserve_production_environment
test -d "$PRODUCTION_STATE_DIR"
test "$(stat -c '%a' "$PRODUCTION_STATE_DIR")" = 700

mkdir -p "$(dirname "$LEGACY_PRODUCTION_ENV_FILE")"
cat > "$LEGACY_PRODUCTION_ENV_FILE" <<'EOF'
ADMIN_PASSWORD=admin-must-not-change
JWT_KEY=jwt-must-not-change
ENCRYPTION_KEY=encryption-must-not-change
EOF
EXPECTED_HASH="$(sha256sum "$LEGACY_PRODUCTION_ENV_FILE" | cut -d ' ' -f 1)"

preserve_production_environment
test -f "$PRODUCTION_ENV_FILE"
test "$(stat -c '%a' "$PRODUCTION_ENV_FILE")" = 600
test "$(sha256sum "$PRODUCTION_ENV_FILE" | cut -d ' ' -f 1)" = "$EXPECTED_HASH"
BACKUP_FILE="$(find "$PRODUCTION_STATE_DIR" -maxdepth 1 -type f \
    -name '.env-prod-????????T??????Z' -print -quit)"
test -n "$BACKUP_FILE"
test "$(stat -c '%a' "$BACKUP_FILE")" = 600
test "$(sha256sum "$BACKUP_FILE" | cut -d ' ' -f 1)" = "$EXPECTED_HASH"

# Execute the same destructive boundary as the clean installer. The canonical secret and its
# installation-dated backup must remain byte-identical outside WORK_DIR.
rm -rf "${WORK_DIR:?}"/* "${WORK_DIR:?}"/.[!.]* 2>/dev/null || true
test ! -e "$LEGACY_PRODUCTION_ENV_FILE"
test "$(sha256sum "$PRODUCTION_ENV_FILE" | cut -d ' ' -f 1)" = "$EXPECTED_HASH"
test "$(sha256sum "$BACKUP_FILE" | cut -d ' ' -f 1)" = "$EXPECTED_HASH"

# A conflicting legacy file must fail before changing or cleaning either source.
mkdir -p "$(dirname "$LEGACY_PRODUCTION_ENV_FILE")"
printf '%s\n' 'ADMIN_PASSWORD=different' > "$LEGACY_PRODUCTION_ENV_FILE"
if (preserve_production_environment); then
    echo "conflicting production environments were accepted" >&2
    exit 1
fi
test -f "$LEGACY_PRODUCTION_ENV_FILE"
test "$(sha256sum "$PRODUCTION_ENV_FILE" | cut -d ' ' -f 1)" = "$EXPECTED_HASH"

rm -f "$LEGACY_PRODUCTION_ENV_FILE"
ln -s "$PRODUCTION_ENV_FILE" "$LEGACY_PRODUCTION_ENV_FILE"
if (preserve_production_environment); then
    echo "a production environment symbolic link was accepted" >&2
    exit 1
fi
test -L "$LEGACY_PRODUCTION_ENV_FILE"
test "$(sha256sum "$PRODUCTION_ENV_FILE" | cut -d ' ' -f 1)" = "$EXPECTED_HASH"

printf '%s\n' "PASS: .env-prod and its installation-dated backup survived the clean-install purge boundary."
