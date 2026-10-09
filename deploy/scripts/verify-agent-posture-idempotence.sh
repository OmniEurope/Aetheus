#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# Behavioral proof that re-running the installer never silently disarms an agent.
#
# An agent is disposable: re-running the enrolment command shown by the web UI must be
# idempotent. apply_sudoers revokes (rm -f) every capability whose ENABLE_* flag is 0, so
# derive_existing_posture is the only thing standing between a plain reinstall and an agent
# that lost certbot, Apache, docker and deployment rights. This proof extracts and executes
# the installer function itself so it cannot silently diverge from production code.
set -eu

INSTALLER="${1:-/src/deploy/scripts/install-agent-linux.sh}"
TEST_ROOT="$(mktemp -d)"
cleanup() { rm -rf -- "$TEST_ROOT"; }
trap cleanup EXIT

log_info() { printf '[INFO] %s\n' "$1"; }
log_error() { printf '[ERROR] %s\n' "$1" >&2; }

FUNCTIONS_FILE="$TEST_ROOT/installer-function.sh"
for function in mark_explicit is_explicit preserve derive_existing_posture; do
    sed -n "/^${function}() {$/,/^}$/p" "$INSTALLER" >> "$FUNCTIONS_FILE"
    grep -q "^${function}() {$" "$FUNCTIONS_FILE"
done
# shellcheck disable=SC1090
. "$FUNCTIONS_FILE"

# Point every witness at the disposable tree; the function only ever tests -f on them.
SUDOERS_FILE="$TEST_ROOT/aetheus-agent"
APACHE_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-apache"
CERTBOT_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-certbot"
RKHUNTER_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-rkhunter"
CRON_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-cron"
PORTSENTRY_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-portsentry"
SERVICE_ENABLE_SUDOERS_FILE="$TEST_ROOT/aetheus-service-enable"
PACKAGE_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-package"
PATCH_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-patch"
FIREWALL_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-firewall"
MAIL_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-mail"
TEAMSPEAK_SETUP_SUDOERS_FILE="$TEST_ROOT/aetheus-teamspeak"
DEPLOY_MANAGE_SUDOERS_FILE="$TEST_ROOT/aetheus-deploy"
INSTALL_DIR="$TEST_ROOT/opt"
AGENT_USER="aetheus-posture-user-does-not-exist"

reset_flags() {
    POSTURE_EXPLICIT=0
    EXPLICIT_CAPABILITIES=" "
    ENABLE_SERVICE_CONTROL=0; ENABLE_APACHE=0; ENABLE_APACHE_MANAGE=0
    ENABLE_CERTBOT=0; ENABLE_CERTBOT_MANAGE=0; ENABLE_RKHUNTER_MANAGE=0
    ENABLE_DOCKER=0; ENABLE_TEAMSPEAK=0; ENABLE_CRON_MANAGE=0
    ENABLE_PORTSENTRY_MANAGE=0; ENABLE_SERVICE_ENABLE=0; ENABLE_PACKAGE_MANAGE=0
    ENABLE_PATCH_MANAGE=0; ENABLE_FIREWALL_MANAGE=0; ENABLE_MAIL_SETUP=0
    ENABLE_TEAMSPEAK_SETUP=0; ENABLE_DEPLOYMENT=0
}

assert_one() {
    if [ "$2" -ne 1 ]; then
        log_error "Reinstall would have revoked $1 (flag=$2)."
        exit 1
    fi
}

# A fresh host has no witness at all: nothing is derived, nothing is claimed.
reset_flags
derive_existing_posture
if [ "$ENABLE_CERTBOT_MANAGE" -ne 0 ] || [ "$ENABLE_SERVICE_CONTROL" -ne 0 ]; then
    log_error "A pristine host must not derive any capability."
    exit 1
fi
log_info "Pristine host derives nothing - OK"

# An established host: every witness present must survive the reinstall.
for witness in "$SUDOERS_FILE" "$APACHE_MANAGE_SUDOERS_FILE" "$CERTBOT_MANAGE_SUDOERS_FILE" \
    "$RKHUNTER_MANAGE_SUDOERS_FILE" "$CRON_MANAGE_SUDOERS_FILE" "$PORTSENTRY_MANAGE_SUDOERS_FILE" \
    "$SERVICE_ENABLE_SUDOERS_FILE" "$PACKAGE_MANAGE_SUDOERS_FILE" "$PATCH_MANAGE_SUDOERS_FILE" \
    "$FIREWALL_MANAGE_SUDOERS_FILE" "$MAIL_MANAGE_SUDOERS_FILE" "$TEAMSPEAK_SETUP_SUDOERS_FILE" \
    "$DEPLOY_MANAGE_SUDOERS_FILE"; do
    printf '# witness\n' > "$witness"
done

reset_flags
derive_existing_posture
assert_one "service-control"   "$ENABLE_SERVICE_CONTROL"
assert_one "apache-manage"     "$ENABLE_APACHE_MANAGE"
assert_one "apache read ACL"   "$ENABLE_APACHE"
assert_one "certbot-manage"    "$ENABLE_CERTBOT_MANAGE"
assert_one "certbot read ACL"  "$ENABLE_CERTBOT"
assert_one "rkhunter-manage"   "$ENABLE_RKHUNTER_MANAGE"
assert_one "cron-manage"       "$ENABLE_CRON_MANAGE"
assert_one "portsentry-manage" "$ENABLE_PORTSENTRY_MANAGE"
assert_one "service-enable"    "$ENABLE_SERVICE_ENABLE"
assert_one "package-manage"    "$ENABLE_PACKAGE_MANAGE"
assert_one "patch-manage"      "$ENABLE_PATCH_MANAGE"
assert_one "firewall-manage"   "$ENABLE_FIREWALL_MANAGE"
assert_one "mail-setup"        "$ENABLE_MAIL_SETUP"
assert_one "teamspeak-setup"   "$ENABLE_TEAMSPEAK_SETUP"
assert_one "deployment"        "$ENABLE_DEPLOYMENT"
log_info "Established host keeps every capability across a reinstall - OK"

# An explicit --enable-*/--disable-* on the command line still wins over the derivation, for the
# capability it names only: the others keep what the host shows (audit DEP-021).
reset_flags
mark_explicit CERTBOT_MANAGE
derive_existing_posture
if [ "$ENABLE_CERTBOT_MANAGE" -ne 0 ]; then
    log_error "Explicit posture flags must override the on-disk derivation."
    exit 1
fi
assert_one "deployment beside an explicit certbot flag" "$ENABLE_DEPLOYMENT"
assert_one "cron-manage beside an explicit certbot flag" "$ENABLE_CRON_MANAGE"
log_info "Explicit flags override the derivation for what they name - OK"

log_info "Agent posture idempotence proof passed."
