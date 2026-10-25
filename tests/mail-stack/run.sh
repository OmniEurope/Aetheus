#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# End-to-end proof of the root-owned mail helpers (mail-setup, mail-manage), templates under
# deploy/agent-host-config. Renders them with the installer's render_host_config, runs them as root in a
# disposable Debian container booted with systemd, and executes tests/mail-stack/scenario.sh, which
# sends real mail through Postfix, Dovecot LMTP, OpenDKIM and rspamd.
#
# Usage: tests/mail-stack/run.sh [debian:bookworm-slim|debian:trixie-slim|ubuntu:24.04]
# Requires a local Docker daemon able to run a privileged systemd container. Exit code 0 only when
# every scenario check passed.
# KEEP_CONTAINER=1 leaves the container running for inspection (docker rm -f it afterwards).
set -eu

base_image="${1:-debian:bookworm-slim}"
here="$(cd "$(dirname "$0")" && pwd)"
repo="$(cd "$here/../.." && pwd)"
installer="$repo/deploy/agent-installer/install-agent-linux.sh"
work="$(mktemp -d)"
tag="aetheus-mailstack:$(printf '%s' "$base_image" | tr ':/' '--')"
name="aetheus-mailstack-$$"

cleanup() {
  [ "${KEEP_CONTAINER:-0}" = 1 ] && { echo "container kept: $name"; rm -rf "$work"; return; }
  docker rm -f "$name" >/dev/null 2>&1 || true
  rm -rf "$work"
}
trap cleanup EXIT

# Renders host-configuration templates through the installer's render_host_config with the default
# ACME web root.
log_error() { printf '[ERROR] %s\n' "$1" >&2; }
sed -n '/^render_host_config() {$/,/^}$/p' "$installer" > "$work/render.sh"
grep -q '^render_host_config() {$' "$work/render.sh" || { echo "FAIL: render_host_config not found in $installer" >&2; exit 1; }
# shellcheck disable=SC1091
. "$work/render.sh"
HOST_CONFIG_DIR="$repo/deploy/agent-host-config"
ACME_WEBROOT="/var/www/aetheus-acme"
render_host_config mail/mail-setup "$work/mail-setup"
render_host_config mail/mail-manage "$work/mail-manage"
cp "$here/scenario.sh" "$work/scenario.sh"

# Git Bash on Windows: stop MSYS from rewriting container paths and hand Docker native host paths.
export MSYS_NO_PATHCONV=1
hostpath() { if command -v cygpath >/dev/null 2>&1; then cygpath -m "$1"; else printf "%s" "$1"; fi; }
docker build -q --build-arg "BASE_IMAGE=$base_image" -t "$tag" "$(hostpath "$here")" >/dev/null
docker run -d --name "$name" --hostname mail.example.test --user 0:0 --privileged --cgroupns=host \
  -v /sys/fs/cgroup:/sys/fs/cgroup:rw --tmpfs /run --tmpfs /run/lock "$tag" >/dev/null

i=0
until state="$(docker exec "$name" systemctl is-system-running 2>/dev/null)"; [ "$state" = running ] || [ "$state" = degraded ]; do
  i=$((i + 1))
  [ "$i" -lt 60 ] || { echo "FAIL: systemd did not boot (state=$state)" >&2; exit 1; }
  sleep 1
done

docker exec "$name" mkdir -p /usr/local/lib/aetheus
docker cp "$(hostpath "$work/mail-setup")" "$name:/usr/local/lib/aetheus/mail-setup"
docker cp "$(hostpath "$work/mail-manage")" "$name:/usr/local/lib/aetheus/mail-manage"
docker cp "$(hostpath "$work/scenario.sh")" "$name:/root/scenario.sh"
docker exec "$name" sh -c 'chown root:root /usr/local/lib/aetheus/* && chmod 755 /usr/local/lib/aetheus/*'
docker exec "$name" sh /root/scenario.sh
