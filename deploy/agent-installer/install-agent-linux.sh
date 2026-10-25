#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
set -e

# =============================================================================
# install-agent-linux.sh - Aetheus Agent Installation Script
# =============================================================================
# Run this script from inside the extracted agent directory
# (e.g. /opt/aetheus-agent/install-agent-linux.sh).
#
# Modes (mutually exclusive):
#   (default)   Clean install: prompts for URL/token/name, wipes any previous
#               install, writes fresh config + service, starts and health-checks.
#               Idempotent by design - an agent is disposable, so re-running the
#               enrolment command from the web UI is safe: the elevation posture
#               already on the box is re-derived and re-applied, and privileged
#               state dirs are re-provisioned, without repeating any --enable-*.
#   --upgrade   Full integration refresh: binaries, systemd posture and root-owned helpers;
#               Same posture preservation as the default mode, but additionally
#               preserves appsettings.json, work dir and production secret-zero.
#               Must be run from a DIFFERENT directory than INSTALL_DIR
#               (extract the new tarball to /tmp first, then run from there).
#   --purge     Full uninstall: stop service, remove unit, sudoers, install and
#               work dirs, delete the system user. Prompts for confirmation
#               unless --yes is given.
#
# Usage:
#   sudo sh install-agent-linux.sh
#   sudo sh install-agent-linux.sh --server-url URL --token TOKEN [--name NAME]
#   sudo sh install-agent-linux.sh --upgrade
#   sudo sh install-agent-linux.sh --bootstrap-update-supervisor
#   sudo sh install-agent-linux.sh --purge [--yes]
# =============================================================================

AGENT_USER="aetheus-agent"
AGENT_GROUP="aetheus-agent"
INSTALL_DIR="/opt/aetheus-agent"
WORK_DIR="/var/lib/aetheus-agent"
# These are the host layout, which the deployment pipelines read from their Variable Libraries.
# They are defaults here rather than constants so a host that names them differently can say so at
# install time (--production-state-dir, --acme-webroot) instead of needing an edited copy of this
# script. Nothing else changes: the installer still owns creating them, because
# the deployment runs as the agent user and cannot create a directory under /var/lib.
PRODUCTION_STATE_DIR="${AETHEUS_PRODUCTION_STATE_DIR:-/var/lib/aetheus-production}"
PRODUCTION_ENV_FILE="$PRODUCTION_STATE_DIR/.env-prod"
LEGACY_PRODUCTION_ENV_FILE="$WORK_DIR/aetheus-prod/.env-prod"
# Only production Aetheus has a state directory on the host (user decision, 2026-10-02). The demo
# and the nightly QA keep theirs in the run's workspace, which the agent owns, so nothing is created
# for them here any more. A directory an older installer created (/var/lib/aetheus-demo,
# /var/lib/aetheus-nightly) is left as it is: removing data is the operator's call.
SERVICE_NAME="aetheus-agent"
AGENT_POSTURE_VERSION="4"
AGENT_POSTURE_VERSION_FILE="/etc/aetheus-agent-posture-version"
# Root-owned record that THIS installer added the agent account to the docker group (decision D12).
DOCKER_GROUP_MARKER_FILE="/etc/aetheus-agent-docker-group-added"
AGENT_UPDATE_URL_FILE="/etc/aetheus-agent-update-url"
AGENT_UPDATE_REQUEST_FILE="$WORK_DIR/.agent-posture-upgrade-request"
AGENT_UPDATE_WORKER_PATH="/usr/local/lib/aetheus/agent-posture-upgrade"
AGENT_UPDATE_SERVICE_PATH="/etc/systemd/system/aetheus-agent-upgrade.service"
AGENT_UPDATE_PATH_PATH="/etc/systemd/system/aetheus-agent-upgrade.path"
SUDOERS_FILE="/etc/sudoers.d/aetheus-agent"
# Item #6 - controlled sudo escalation for Apache management. Separate file so the broader
# service-control sudoers can stay untouched on a routine flag change.
APACHE_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-apache"
APACHE_CONFIGTEST_HELPER_PATH="/usr/local/lib/aetheus/aetheus-apache-configtest"
APACHE_RELOAD_HELPER_PATH="/usr/local/lib/aetheus/aetheus-apache-reload"
CERTBOT_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-certbot"
# Items #10.2/#10.3 - same recipe as Apache for RKHunter scan/update/propupd.
RKHUNTER_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-rkhunter"
# Root-owned helper the RKHunter grant names (ADR-019): rkhunter must exec its tools, so no noexec.
RKHUNTER_HELPER_PATH="/usr/local/lib/aetheus/aetheus-rkhunter"
# Phase 3 - cron management (sudo helper), portsentry IP unblock (sudo helper), and the
# fixed-unit service-enable allow-list. The two helpers are root-owned, agent-non-writable,
# input-validating binaries the agent invokes via NOPASSWD sudoers (the helper is the boundary).
CRON_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-cron"
PORTSENTRY_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-portsentry"
SERVICE_ENABLE_SUDOERS_FILE="/etc/sudoers.d/aetheus-service-enable"
# S-FEAT-W8KN - argv-exact apt-get install/purge allow-list (managed-package install/uninstall).
PACKAGE_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-package"
# ADR-024 4.1 - argv-exact apt-get upgrade grant (fleet OS patching / patch-manage capability).
PATCH_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-patch"
# ADR-024 4.2 - path-only grant for the root-owned firewall (ufw) helper (firewall-manage capability).
FIREWALL_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-firewall"
FIREWALL_HELPER_PATH="/usr/local/lib/aetheus/aetheus-firewall"
# S-FEAT-W8KN - argv-exact grant for the root-owned mail-setup helper (full postfix/dovecot/opendkim
# install+configure). Same root-owned-helper-boundary recipe as cron.
MAIL_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-mail"
AETHEUS_HELPER_DIR="/usr/local/lib/aetheus"
# Root-owned helper that aetheus-agent-read-grant.service (a oneshot unit of its own) runs before every agent start: it
# gives the agent account READ-only ACLs on the SSH configuration it monitors. No sudo, no capability.
AGENT_READ_GRANT_HELPER_PATH="$AETHEUS_HELPER_DIR/aetheus-agent-read-grant"
CRON_HELPER_PATH="$AETHEUS_HELPER_DIR/cron-apply"
UNBLOCK_HELPER_PATH="$AETHEUS_HELPER_DIR/unblock-ip"
# Root-owned helper for the typed PortsentrySetup op (apt-install portsentry + write mode/port lists into
# the config + enable the service). Same root-owned-helper-boundary recipe as unblock-ip; granted in the
# SAME aetheus-portsentry sudoers file via its own Cmnd_Alias.
PORTSENTRY_SETUP_HELPER_PATH="$AETHEUS_HELPER_DIR/portsentry-setup"
MAIL_SETUP_HELPER_PATH="$AETHEUS_HELPER_DIR/mail-setup"
# S-FEAT-W8KN (incremental) - root-owned helper for add-domain / add-account / add-alias / dkim-rotate
# on an already-provisioned mail stack. Same boundary recipe as mail-setup; granted in the SAME
# aetheus-mail sudoers file via its own Cmnd_Alias.
MAIL_MANAGE_HELPER_PATH="$AETHEUS_HELPER_DIR/mail-manage"
# argv-exact grant for the root-owned teamspeak-setup helper (full TS3 server install: user, tarball,
# systemd unit, credential extraction). Same root-owned-helper-boundary recipe as mail-setup.
TEAMSPEAK_SETUP_SUDOERS_FILE="/etc/sudoers.d/aetheus-teamspeak"
TEAMSPEAK_SETUP_HELPER_PATH="$AETHEUS_HELPER_DIR/teamspeak-setup"
# Cross-agent deployment module. Same root-owned-helper-boundary recipe as cron: the deploy-restart
# helper re-validates <app> and can ONLY restart aetheus-app@<app>.service. The unit TEMPLATE is
# posed at install (never generated from a pipeline YAML - anti-injection), and the per-app deploy
# tree lives under $WORK_DIR/deploy/<app>/{releases,current} owned by the agent (it flips 'current').
DEPLOY_MANAGE_SUDOERS_FILE="/etc/sudoers.d/aetheus-deploy"
DEPLOY_RESTART_HELPER_PATH="$AETHEUS_HELPER_DIR/deploy-restart"
# Certbot management. certbot is a GTFOBins root primitive with domain-variable argv, so it can't be
# argv-exact granted: instead a root-owned helper re-validates the domain and performs real webroot
# ACME issuance. ACME failure is fatal; it never manufactures a self-signed success.
CERTBOT_ISSUE_HELPER_PATH="$AETHEUS_HELPER_DIR/aetheus-certbot-issue"
# Certbot lifecycle management (renew/delete/revoke a named lineage). Same boundary model as the issue
# helper: root-owned, re-validates the cert name, runs a fixed certbot verb (no free-form argv).
CERTBOT_MANAGE_HELPER_PATH="$AETHEUS_HELPER_DIR/aetheus-certbot-manage"
# Use the ACME web root for automated and manual renewals so one validation path applies; the hook
# reloads Apache and the weekly check leaves evidence for the agent collector. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
CERTBOT_CLI_INI="/etc/letsencrypt/cli.ini"
CERTBOT_APACHE_DEPLOY_HOOK="/etc/letsencrypt/renewal-hooks/deploy/aetheus-apache"
CERTBOT_STATE_DIR="/var/lib/aetheus-certbot"
CERTBOT_RENEWAL_CHECK_UNIT="aetheus-certbot-renewal-check"
DEPLOY_UNIT_TEMPLATE_PATH="/etc/systemd/system/aetheus-app@.service"
DEPLOY_BASE_DIR="$WORK_DIR/deploy"
# Static web root the deployment module makes agent-writable (via ACL) so a pipeline can publish a
# static site (e.g. the vitrine) into it, served by Apache. Default /var/www; override with --web-root.
DEPLOY_WEB_ROOT="/var/www"
# S-FEAT-DPU2: dedicated read-only group that owns the deploy trees. The transient per-app DynamicUser
# joins it (SupplementaryGroups) to read its secret-bearing payload (mode 0750, never world) without
# joining the agent's own group - so a deployed app shares no surface with the agent.
DEPLOY_READ_GROUP="aetheus-deploy"
SYSTEMD_UNIT_PATH="/etc/systemd/system/${SERVICE_NAME}.service"
# The oneshot unit that runs the read-grant helper before every start of the agent.
AGENT_READ_GRANT_SERVICE_PATH="/etc/systemd/system/aetheus-agent-read-grant.service"
DEFAULT_SERVER_URL="https://aetheus.example.com"

# If the wizard command created a source URL marker, use it as prompt default.
SCRIPT_DIR_PREVIEW="$(cd "$(dirname "$0")" && pwd)"
URL_MARKER_PATH="$SCRIPT_DIR_PREVIEW/.aetheus-server-url"
if [ -f "$URL_MARKER_PATH" ]; then
    URL_FROM_MARKER="$(head -n 1 "$URL_MARKER_PATH" | tr -d '\r')"
    if [ -n "$URL_FROM_MARKER" ]; then
        DEFAULT_SERVER_URL="$URL_FROM_MARKER"
    fi
fi

RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
NC='\033[0m'

log_info()  { printf "${GREEN}[INFO]${NC} %s\n" "$1"; }
log_warn()  { printf "${YELLOW}[WARN]${NC} %s\n" "$1"; }
log_error() { printf "${RED}[ERROR]${NC} %s\n" "$1"; }

# Render host configuration from the versioned agent-host-config templates so installed units,
# helpers and settings share an explicit placeholder contract. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
if [ -d "$SCRIPT_DIR_PREVIEW/agent-host-config" ]; then
    HOST_CONFIG_DIR="$SCRIPT_DIR_PREVIEW/agent-host-config"
else
    HOST_CONFIG_DIR="$(cd "$SCRIPT_DIR_PREVIEW/.." && pwd)/agent-host-config"
fi

# Fails before anything on the host is touched when the templates are missing or incomplete: an
# installer extracted without its agent-host-config folder must stop, not half-configure a host.
require_host_config() {
    if [ ! -f "$HOST_CONFIG_DIR/manifest" ]; then
        log_error "Host configuration templates not found: $HOST_CONFIG_DIR/manifest"
        log_error "Extract the whole agent archive and run install-agent-linux.sh from the extracted folder."
        exit 1
    fi
    _rqc_missing="$(awk '/^[[:space:]]*(#|$)/ { next } { print $1 }' "$HOST_CONFIG_DIR/manifest" \
        | while IFS= read -r _rqc_rel; do [ -f "$HOST_CONFIG_DIR/$_rqc_rel" ] || printf '%s ' "$_rqc_rel"; done)"
    if [ -n "$_rqc_missing" ]; then
        log_error "Host configuration templates missing from $HOST_CONFIG_DIR: $_rqc_missing"
        exit 1
    fi
}

# Renders the template $1 (relative to HOST_CONFIG_DIR) into the file $2. Each #{NAME}# the manifest
# declares for the template becomes the value of the installer variable NAME, inserted literally
# (never re-read as shell or as another placeholder). A line that is exactly #{include:<path>}# is
# replaced by that template verbatim. The installation stops, with $2 left untouched, when the
# template is not in the manifest, a declared placeholder has no value or is not used, the template
# holds an undeclared or unterminated placeholder, or a carriage return. The destination is written
# with `cat >`, exactly as the heredoc each template replaces did (same inode, mode and owner).
render_host_config() {
    _rhc_rel="$1"
    _rhc_dest="$2"
    case "$_rhc_rel" in
        ''|/*|*..*) log_error "Invalid host configuration template name: $_rhc_rel"; exit 1 ;;
    esac
    if [ ! -f "$HOST_CONFIG_DIR/$_rhc_rel" ]; then
        log_error "Host configuration template missing: $HOST_CONFIG_DIR/$_rhc_rel"
        exit 1
    fi
    if ! _rhc_names="$(awk -v rel="$_rhc_rel" '
        $1 == rel { found = 1; for (i = 2; i <= NF; i++) printf "%s ", $i }
        END { exit found ? 0 : 1 }' "$HOST_CONFIG_DIR/manifest")"; then
        log_error "Host configuration template $_rhc_rel is not declared in $HOST_CONFIG_DIR/manifest"
        exit 1
    fi
    # A carriage return means a CRLF checkout slipped into the archive: it would end up in sudoers,
    # units and helpers. Counted with tr, which sees it on every platform.
    for _rhc_file in "$_rhc_rel" $(sed -n 's/^#{include:\(.*\)}#$/\1/p' "$HOST_CONFIG_DIR/$_rhc_rel"); do
        [ -f "$HOST_CONFIG_DIR/$_rhc_file" ] || continue
        if [ "$(LC_ALL=C tr -cd '\r' < "$HOST_CONFIG_DIR/$_rhc_file" | wc -c)" -ne 0 ]; then
            log_error "Refusing to install $_rhc_dest: template $_rhc_file has carriage returns (CRLF line endings)."
            exit 1
        fi
    done
    _rhc_tmp="$(mktemp "${TMPDIR:-/tmp}/aetheus-host-config.XXXXXX")"
    if ! (
        for _rhc_name in $_rhc_names; do
            case "$_rhc_name" in
                [A-Z_]*) ;;
                *) echo "render_host_config: invalid placeholder name '$_rhc_name' for $_rhc_rel" >&2; exit 1 ;;
            esac
            case "$_rhc_name" in
                *[!A-Z0-9_]*) echo "render_host_config: invalid placeholder name '$_rhc_name' for $_rhc_rel" >&2; exit 1 ;;
            esac
            eval "_rhc_isset=\${$_rhc_name+set}"
            if [ "$_rhc_isset" != set ]; then
                echo "render_host_config: #{$_rhc_name}# has no value for $_rhc_rel" >&2
                exit 1
            fi
            eval "AHC_VALUE_$_rhc_name=\$$_rhc_name"
            export "AHC_VALUE_$_rhc_name"
        done
        AHC_NAMES="$_rhc_names" AHC_DIR="$HOST_CONFIG_DIR" AHC_REL="$_rhc_rel" LC_ALL=C awk '
            function fail(msg) {
                printf "render_host_config: %s (%s line %d)\n", msg, ENVIRON["AHC_REL"], FNR > "/dev/stderr"
                failed = 1
                exit 1
            }
            BEGIN {
                n = split(ENVIRON["AHC_NAMES"], names, " ")
                for (i = 1; i <= n; i++) if (names[i] != "") used[names[i]] = 0
            }
            index($0, "\r") > 0 { fail("carriage return in template") }
            substr($0, 1, 10) == "#{include:" && substr($0, length($0) - 1) == "}#" {
                inc = substr($0, 11, length($0) - 12)
                if (inc == "" || index(inc, "..") > 0 || substr(inc, 1, 1) == "/") fail("invalid include " inc)
                path = ENVIRON["AHC_DIR"] "/" inc
                while ((r = (getline l < path)) > 0) {
                    if (index(l, "\r") > 0 || index(l, "#{") > 0) fail("included template " inc " holds a carriage return or a placeholder")
                    printf "%s\n", l
                }
                if (r < 0) fail("cannot read included template " inc)
                close(path)
                next
            }
            {
                line = $0
                out = ""
                while ((p = index(line, "#{")) > 0) {
                    out = out substr(line, 1, p - 1)
                    rest = substr(line, p + 2)
                    q = index(rest, "}#")
                    if (q == 0) fail("unterminated placeholder")
                    name = substr(rest, 1, q - 1)
                    if (!(name in used)) fail("undeclared placeholder #{" name "}#")
                    out = out ENVIRON["AHC_VALUE_" name]
                    used[name] = 1
                    line = substr(rest, q + 2)
                }
                printf "%s\n", out line
            }
            END {
                if (failed) exit 1
                for (name in used) if (!used[name]) {
                    printf "render_host_config: declared placeholder #{%s}# unused in %s\n", name, ENVIRON["AHC_REL"] > "/dev/stderr"
                    exit 1
                }
            }' "$HOST_CONFIG_DIR/$_rhc_rel"
    ) > "$_rhc_tmp"; then
        rm -f "$_rhc_tmp"
        log_error "Refusing to install $_rhc_dest: template $_rhc_rel did not render (see above)."
        exit 1
    fi
    cat "$_rhc_tmp" > "$_rhc_dest"
    rm -f "$_rhc_tmp"
}

preserve_production_environment() {
    if [ -L "$PRODUCTION_ENV_FILE" ] || [ -L "$LEGACY_PRODUCTION_ENV_FILE" ]; then
        log_error "Refusing to preserve a production environment through a symbolic link."
        exit 1
    fi

    if [ -f "$PRODUCTION_ENV_FILE" ] && [ -f "$LEGACY_PRODUCTION_ENV_FILE" ] \
        && ! cmp -s "$PRODUCTION_ENV_FILE" "$LEGACY_PRODUCTION_ENV_FILE"; then
        log_error "Production environment conflict: canonical and legacy .env-prod files differ."
        log_error "Installation stopped before cleanup; reconcile the files explicitly."
        exit 1
    fi

    install -d -m 700 "$PRODUCTION_STATE_DIR"
    if id "$AGENT_USER" >/dev/null 2>&1; then
        chown -R "$AGENT_USER:$AGENT_GROUP" "$PRODUCTION_STATE_DIR"
    fi

    if [ ! -f "$PRODUCTION_ENV_FILE" ] && [ ! -f "$LEGACY_PRODUCTION_ENV_FILE" ]; then
        return 0
    fi

    if [ ! -f "$PRODUCTION_ENV_FILE" ]; then
        log_info "Migrating production secrets outside the agent work directory..."
        cp -p "$LEGACY_PRODUCTION_ENV_FILE" "$PRODUCTION_ENV_FILE.tmp.$$"
        chmod 600 "$PRODUCTION_ENV_FILE.tmp.$$"
        mv "$PRODUCTION_ENV_FILE.tmp.$$" "$PRODUCTION_ENV_FILE"
    fi

    INSTALLATION_DATE="$(date -u +"%Y%m%dT%H%M%SZ")"
    PRODUCTION_ENV_BACKUP="$PRODUCTION_STATE_DIR/.env-prod-$INSTALLATION_DATE"
    log_info "Backing up production secrets to $PRODUCTION_ENV_BACKUP..."
    cp -p "$PRODUCTION_ENV_FILE" "$PRODUCTION_ENV_BACKUP.tmp.$$"
    chmod 600 "$PRODUCTION_ENV_BACKUP.tmp.$$"
    mv "$PRODUCTION_ENV_BACKUP.tmp.$$" "$PRODUCTION_ENV_BACKUP"

    if id "$AGENT_USER" >/dev/null 2>&1; then
        chown -R "$AGENT_USER:$AGENT_GROUP" "$PRODUCTION_STATE_DIR"
    fi
}

show_help() {
    cat <<EOF
Usage: sudo sh install-agent-linux.sh [OPTIONS]

Modes (mutually exclusive):
  (none)                    Clean install (default)
  --upgrade                 Replace binaries, service posture and helpers; preserve config/data
  --bootstrap-update-supervisor
                            Install only the root-owned self-update supervisor for an
                            existing pre-supervisor agent; never replace or stop the agent
  --purge                   Full uninstall (service, files, user)

Install options:
  --server-url URL          Backend API URL (e.g. https://aetheus-api.example.com)
  --token TOKEN             One-time registration token from the web UI
  --name NAME               Display name for this agent (default: hostname)

Capability modules:
  --module pipeline-runner  (DEFAULT ON) Install Git, sshpass, acl, libatomic and optional
                            Buildx support. Application SDKs run in locked OCI
                            images instead of being installed on the agent.
  --no-pipeline-runner      Skip the pipeline-runner toolchain install.
  --module server-management
                            Opt-in. Enable all server-administration capabilities
                            (service-control, Apache, Certbot, RKHunter, Docker,
                            TeamSpeak, cron-manage, portsentry-manage, service-enable)
                            - shorthand for the matching --enable-* flags below.
  --module deployment       Opt-in (independent of the two above). Make this host a
                            cross-agent deploy target: installs the aetheus-app@.service
                            unit template, the root-owned deploy-restart helper + its
                            argv-exact sudoers grant, and the agent-owned deploy tree.
                            Requires a sudo grant, so forces NoNewPrivileges=false.
  --web-root PATH           (deployment module) Static web root made agent-writable
                            (ACL) so a pipeline can publish a static site there for
                            Apache to serve. Default: /var/www.

Elevation options (ALL DISABLED BY DEFAULT - opt in with --enable-* or --module server-management):
  --enable-service-control  Allow the agent to start/stop/restart a fixed set of
                            managed services via a narrow NOPASSWD sudoers rule.
                            This is the ONLY option that requires sudo and forces
                            NoNewPrivileges=false (weaker systemd sandbox).
  --enable-apache-introspection
                            Grant the agent READ access (ACL, no sudo) to
                            /etc/apache2 + /etc/httpd so it can enumerate vhosts.
  --enable-certbot-introspection
                            Grant the agent READ access (ACL, no sudo) to
                            /etc/letsencrypt so it can read certificate expiry.
  --enable-apache-manage    Item #6: WRITE access on Apache sites-available + a
                            tight NOPASSWD sudoers rule for apache2ctl
                            configtest / graceful / systemctl reload apache2.
                            Strict allow-list (no wildcards), noexec, no sudo
                            timeout caching, env_reset, secure_path. Required
                            for the "Test config" / "Edit config" / "Reload"
                            buttons on the Apache section to work.
  --enable-certbot-manage   Provision HTTPS issuance: a root-owned certbot-issue
                            helper (real ACME via a non-disruptive webroot;
                            issuance failure stays a hard failure) plus a
                            root-owned certbot-manage helper
                            (renew/delete/revoke a named lineage) + a NOPASSWD
                            sudoers rule scoped to both. Implies
                            --enable-apache-manage. Required for the pipeline
                            'type: certbot' step and the Certbot section's
                            create/renew/delete/revoke buttons.
  --enable-rkhunter-manage  Items #10.2/#10.3: tight NOPASSWD sudoers rule for
                            rkhunter --check / --update / --propupd (no other
                            flags allowed). Same hardened recipe as
                            apache-manage. Required for the "Run Scan" button
                            on the RKHunter section to work.
  --enable-docker           Add the agent to the 'docker' group for container
                            collection (no sudo). Note: docker group ~= root.
  --enable-teamspeak        Provision TeamSpeak ServerQuery access: add the agent
                            to the 'teamspeak' group (read ts3server.ini for a
                            non-default query port; no sudo) and scaffold the
                            credentials dir. Detection itself works without this
                            (pidof + default port 10011); enable to manage TS via
                            the dashboard. Drop the serveradmin ServerQuery
                            password into INSTALL_DIR/teamspeak/query-credentials.
  --enable-cron-manage      Phase 3: deploy the root-owned cron-apply helper + a
                            NOPASSWD sudoers grant for it. Lets the agent write cron
                            jobs (the helper writes /etc/cron.d/aetheus-<id> only;
                            it re-validates every field). Required for the Cron
                            section's save/delete buttons to work.
  --enable-portsentry-manage
                            Phase 3: deploy the root-owned unblock-ip + portsentry-setup
                            helpers + a NOPASSWD sudoers grant for them. Lets the agent
                            unblock an IP (removes it from iptables/ip6tables +
                            /etc/hosts.deny) and install/configure portsentry. Required
                            for the Portsentry "Unblock" and "Setup" actions to work.
  --enable-service-enable   Phase 3: tight NOPASSWD sudoers rule for
                            'systemctl enable --now <unit>' over a FIXED list of
                            pre-existing units (no wildcards). Required for the server
                            Configuration YAML deploy to enable services on boot.
  --enable-package-manage   S-FEAT-W8KN: tight NOPASSWD sudoers rule for
                            'apt-get install|purge -y <pkg>' over a FIXED package list
                            (no wildcards). Required to install/uninstall managed server
                            components (docker/apache/mail/...) from Aetheus.
  --enable-patch-manage     ADR-024 4.1: tight NOPASSWD sudoers rule for
                            'apt-get upgrade -y' (argv-exact, no wildcards). Lets the fleet
                            APPLY pending OS updates; the agent runs a dry-run first and
                            aborts on any critical package. Pending-update visibility does
                            not need this grant (the dry-run probe is unprivileged).
  --enable-firewall-manage  ADR-024 4.2: deploy the root-owned aetheus-firewall helper +
                            a path-only NOPASSWD sudoers grant for it. Lets the agent control
                            ufw (open/close ports, enable/disable); the helper re-validates
                            every argument and enforces anti-lockout (never closes the SSH
                            port; auto-allows it before enabling). ufw status needs root, so
                            firewall visibility also depends on this grant.
  --enable-mail-setup       S-FEAT-W8KN: deploy the root-owned mail-setup helper + a
                            NOPASSWD sudoers grant for it. Lets the agent run the full
                            mail-stack setup (install + configure postfix/dovecot/opendkim
                            + enable services); the helper re-validates every argument.
                            Required for the Mail section's "Setup" action to work.
  --enable-teamspeak-setup  Deploy the root-owned teamspeak-setup helper + a NOPASSWD
                            sudoers grant for it. Lets the agent install a full TeamSpeak 3
                            server (system user, tarball, systemd unit, ports); the helper
                            re-validates every argument. Required for the TeamSpeak section's
                            "Install" action (also implies --enable-teamspeak).
  --enable-all              Enable everything (maximal capabilities).

Disable options (use on --upgrade to revoke previously-enabled capabilities):
  --disable-service-control       Remove sudoers rule for service start/stop/restart.
  --disable-apache-introspection  Remove Apache read ACL.
  --disable-apache-manage         Remove Apache write/reload privileges.
  --disable-rkhunter-manage       Remove rkhunter scan/update/propupd privileges.
  --disable-certbot-introspection Remove Certbot read ACL.
  --disable-docker                Remove the agent account from the docker group.
  --disable-teamspeak             Don't add agent to teamspeak group / provision TS.
  --disable-cron-manage           Remove cron-apply helper + sudoers.
  --disable-portsentry-manage     Remove unblock-ip helper + sudoers.
  --disable-service-enable        Remove the fixed-unit service-enable sudoers.
  --disable-package-manage        Remove the fixed-package apt-get sudoers.
  --disable-mail-setup            Remove mail-setup helper + sudoers.
  --disable-all                   Disable everything (fully sandboxed - the default).

  By default NO elevation is enabled (secure-by-default, per ADR-004).
  No flags => no sudoers, NoNewPrivileges=true.
  On --upgrade the previously-configured elevation posture is preserved automatically.

Other:
  --allow-insecure-certs    DEV ONLY: skip TLS certificate validation (backend API
                            + pipeline git clones). Auto-enabled when the server
                            URL is localhost. Never use in production.
  -y, --yes                 Skip confirmation prompts: auto-install missing dependencies
                            (git/sshpass/acl) without asking, and skip the --purge prompt.
  -h, --help                Show this help

The default and --upgrade modes run a one-shot runtime probe ("can the agent
itself reach the backend over the .NET HTTP stack?") right before starting
the service. This catches TLS / DNS / proxy issues invisible to curl that
would otherwise eat the 120 s systemd start timeout.
EOF
}

# Handle --help / -h before anything else so it works without root.
for _arg in "$@"; do
    case "$_arg" in
        -h|--help) show_help; exit 0 ;;
    esac
done

# --- Check root ---
if [ "$(id -u)" -ne 0 ]; then
    log_error "This script must be run as root (sudo)"
    exit 1
fi

# --- Parse arguments ---
MODE="install"
SERVER_URL=""
REG_TOKEN=""
AGENT_NAME=""
ASSUME_YES=0
ALLOW_INSECURE_CERTS=0     # dev only: TLS bypass for backend API + pipeline git clones

# Elevation posture - all DISABLED by default (secure-by-default per ADR-004).
# Use --enable-* flags to opt in. On --upgrade these are re-derived from the existing install.
ENABLE_SERVICE_CONTROL=0   # opt-in: sudo for service start/stop/restart
ENABLE_APACHE=0            # opt-in: read ACL on /etc/apache2
ENABLE_APACHE_MANAGE=0     # opt-in: write ACL on sites-available + tight Apache sudoers
ENABLE_RKHUNTER_MANAGE=0   # opt-in: tight rkhunter sudoers (scan/update/propupd)
ENABLE_CERTBOT=0           # opt-in: read ACL on /etc/letsencrypt
ENABLE_CERTBOT_MANAGE=0    # opt-in: root-owned webroot ACME helper + NOPASSWD sudoers
ENABLE_DOCKER=0            # opt-in: docker-group membership (~root)
ENABLE_TEAMSPEAK=0         # opt-in: teamspeak-group membership + credential dir (no sudo)
ENABLE_CRON_MANAGE=0       # opt-in: cron-apply sudo helper (writes /etc/cron.d/aetheus-<id>)
ENABLE_PORTSENTRY_MANAGE=0 # opt-in: unblock-ip sudo helper (iptables/ip6tables + hosts.deny)
ENABLE_SERVICE_ENABLE=0    # opt-in: systemctl enable --now on a FIXED unit list (argv-exact)
ENABLE_PACKAGE_MANAGE=0    # opt-in: apt-get install/purge -y on a FIXED package list (argv-exact)
ENABLE_PATCH_MANAGE=0      # opt-in: apt-get upgrade -y (fleet OS patching, argv-exact) - ADR-024 4.1
ENABLE_FIREWALL_MANAGE=0   # opt-in: ufw control via root-owned helper (firewall) - ADR-024 4.2
ENABLE_MAIL_SETUP=0        # opt-in: root-owned mail-setup helper (full postfix/dovecot/opendkim setup)
ENABLE_TEAMSPEAK_SETUP=0   # opt-in: root-owned teamspeak-setup helper (full TS3 server install)
POSTURE_EXPLICIT=0         # set if any --enable-*/--disable-* flag or module was passed explicitly
# Track explicit capability flags independently so an upgrade changes only named capabilities and
# preserves the host's remaining posture. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
EXPLICIT_CAPABILITIES=" "
mark_explicit() {
    POSTURE_EXPLICIT=1
    for _capability in "$@"; do
        EXPLICIT_CAPABILITIES="$EXPLICIT_CAPABILITIES$_capability "
    done
}
is_explicit() {
    case "$EXPLICIT_CAPABILITIES" in *" $1 "*) return 0 ;; esac
    return 1
}

# Capability modules (higher-level than the granular --enable-* flags below).
#   pipeline-runner    : ON by default. Installs Git, sshpass, libatomic and optional Buildx
#                        support. Application SDKs stay in locked OCI images.
#   server-management  : opt-in. Turns on the server-administration capabilities
#                        (service-control, Apache, Certbot, RKHunter, Docker,
#                        TeamSpeak) by expanding to the matching --enable-* flags below.
# The zero-elevation-by-default invariant (ADR-004) is preserved: pipeline-runner
# grants no sudoers/group/root, and server-management must be requested explicitly.
MODULE_PIPELINE_RUNNER=1
MODULE_SERVER_MANAGEMENT=0
#   deployment         : opt-in, independent of the two above. Makes this host a cross-agent deploy
#                        target - installs the aetheus-app@.service unit template, the root-owned
#                        deploy-restart helper + its argv-exact sudoers grant, and the agent-owned
#                        $WORK_DIR/deploy tree. A sudo grant is in play, so it relaxes NoNewPrivileges
#                        (like service-control/package-manage/mail-setup).
MODULE_DEPLOYMENT=0
ENABLE_DEPLOYMENT=0        # derived from MODULE_DEPLOYMENT (and preserved on --upgrade)
DOTNET_CHANNEL="10.0"
DOTNET_RUNTIME_VERSION="10.0.13"
DOTNET_INSTALLER_URL="https://raw.githubusercontent.com/dotnet/install-scripts/da3ce11ba63f3dbb0fb835d41bda2665d5c48e84/src/dotnet-install.sh"
DOTNET_INSTALLER_SHA256="082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e"

ACME_WEBROOT_OVERRIDE=""

# The host layout options below are paths this installer creates and chowns, so they are bounded
# before anything is done with them: a direct child of /var/lib (or /var/www), no traversal, no
# trailing slash. A typo must fail here rather than create a directory somewhere unexpected.
require_state_dir() {
    _candidate="$1"
    _label="$2"
    case "$_candidate" in
        *..*|*/) echo "Invalid --${_label}-state-dir: $_candidate" >&2; exit 1 ;;
        /var/lib/*/*) echo "--${_label}-state-dir must be a direct child of /var/lib: $_candidate" >&2; exit 1 ;;
        /var/lib/?*) printf '%s' "$_candidate" ;;
        *) echo "--${_label}-state-dir must live under /var/lib: $_candidate" >&2; exit 1 ;;
    esac
}

require_web_root() {
    _candidate="$1"
    case "$_candidate" in
        *..*|*/) echo "Invalid --acme-webroot: $_candidate" >&2; exit 1 ;;
        /var/www/*/*) echo "--acme-webroot must be a direct child of /var/www: $_candidate" >&2; exit 1 ;;
        /var/www/?*) printf '%s' "$_candidate" ;;
        *) echo "--acme-webroot must live under /var/www: $_candidate" >&2; exit 1 ;;
    esac
}

while [ "$#" -gt 0 ]; do
    case "$1" in
        --upgrade)    MODE="upgrade"; shift ;;
        --bootstrap-update-supervisor) MODE="bootstrap-update-supervisor"; shift ;;
        --purge)      MODE="purge"; shift ;;
        --server-url) SERVER_URL="$2"; shift 2 ;;
        --token)      REG_TOKEN="$2"; shift 2 ;;
        --name)       AGENT_NAME="$2"; shift 2 ;;
        # Host layout. Optional, and the defaults are what every current host uses; they exist so a
        # host that names these differently can say so here rather than needing an edited installer.
        --production-state-dir)
            PRODUCTION_STATE_DIR="$(require_state_dir "$2" production)"
            PRODUCTION_ENV_FILE="$PRODUCTION_STATE_DIR/.env-prod"
            shift 2 ;;
        # Obsolete since 2026-10-02 (the demo and the QA keep their state in the run workspace).
        # Still accepted so an existing install command does not break, and said to do nothing.
        --demo-state-dir|--nightly-state-dir)
            echo "Ignoring $1: the demo and the nightly QA no longer have a state directory on the host." >&2
            shift 2 ;;
        --acme-webroot)
            ACME_WEBROOT_OVERRIDE="$(require_web_root "$2")"
            shift 2 ;;
        --enable-service-control)       ENABLE_SERVICE_CONTROL=1; mark_explicit SERVICE_CONTROL; shift ;;
        --enable-apache-introspection)  ENABLE_APACHE=1; mark_explicit APACHE; shift ;;
        --enable-apache-manage)         ENABLE_APACHE_MANAGE=1; ENABLE_APACHE=1; mark_explicit APACHE_MANAGE APACHE; shift ;;
        --enable-rkhunter-manage)       ENABLE_RKHUNTER_MANAGE=1; mark_explicit RKHUNTER_MANAGE; shift ;;
        --enable-certbot-introspection) ENABLE_CERTBOT=1; mark_explicit CERTBOT; shift ;;
        --enable-certbot-manage)        ENABLE_CERTBOT_MANAGE=1; ENABLE_CERTBOT=1; ENABLE_APACHE_MANAGE=1; ENABLE_APACHE=1; mark_explicit CERTBOT_MANAGE CERTBOT APACHE_MANAGE APACHE; shift ;;
        --enable-docker)                ENABLE_DOCKER=1; mark_explicit DOCKER; shift ;;
        --enable-teamspeak)             ENABLE_TEAMSPEAK=1; mark_explicit TEAMSPEAK; shift ;;
        --enable-cron-manage)           ENABLE_CRON_MANAGE=1; mark_explicit CRON_MANAGE; shift ;;
        --enable-portsentry-manage)     ENABLE_PORTSENTRY_MANAGE=1; mark_explicit PORTSENTRY_MANAGE; shift ;;
        --enable-service-enable)        ENABLE_SERVICE_ENABLE=1; mark_explicit SERVICE_ENABLE; shift ;;
        --enable-package-manage)        ENABLE_PACKAGE_MANAGE=1; mark_explicit PACKAGE_MANAGE; shift ;;
        --enable-patch-manage)          ENABLE_PATCH_MANAGE=1; mark_explicit PATCH_MANAGE; shift ;;
        --enable-firewall-manage)       ENABLE_FIREWALL_MANAGE=1; mark_explicit FIREWALL_MANAGE; shift ;;
        --enable-mail-setup)            ENABLE_MAIL_SETUP=1; mark_explicit MAIL_SETUP; shift ;;
        --enable-teamspeak-setup)       ENABLE_TEAMSPEAK_SETUP=1; ENABLE_TEAMSPEAK=1; mark_explicit TEAMSPEAK_SETUP TEAMSPEAK; shift ;;
        --disable-service-control)       ENABLE_SERVICE_CONTROL=0; mark_explicit SERVICE_CONTROL; shift ;;
        --disable-apache-introspection)  ENABLE_APACHE=0; mark_explicit APACHE; shift ;;
        --disable-apache-manage)         ENABLE_APACHE_MANAGE=0; mark_explicit APACHE_MANAGE; shift ;;
        --disable-rkhunter-manage)       ENABLE_RKHUNTER_MANAGE=0; mark_explicit RKHUNTER_MANAGE; shift ;;
        --disable-certbot-introspection) ENABLE_CERTBOT=0; mark_explicit CERTBOT; shift ;;
        --disable-certbot-manage)        ENABLE_CERTBOT_MANAGE=0; mark_explicit CERTBOT_MANAGE; shift ;;
        --disable-docker)                ENABLE_DOCKER=0; mark_explicit DOCKER; shift ;;
        --disable-teamspeak)             ENABLE_TEAMSPEAK=0; mark_explicit TEAMSPEAK; shift ;;
        --disable-cron-manage)           ENABLE_CRON_MANAGE=0; mark_explicit CRON_MANAGE; shift ;;
        --disable-portsentry-manage)     ENABLE_PORTSENTRY_MANAGE=0; mark_explicit PORTSENTRY_MANAGE; shift ;;
        --disable-service-enable)        ENABLE_SERVICE_ENABLE=0; mark_explicit SERVICE_ENABLE; shift ;;
        --disable-package-manage)        ENABLE_PACKAGE_MANAGE=0; mark_explicit PACKAGE_MANAGE; shift ;;
        --disable-patch-manage)          ENABLE_PATCH_MANAGE=0; mark_explicit PATCH_MANAGE; shift ;;
        --disable-firewall-manage)       ENABLE_FIREWALL_MANAGE=0; mark_explicit FIREWALL_MANAGE; shift ;;
        --disable-mail-setup)            ENABLE_MAIL_SETUP=0; mark_explicit MAIL_SETUP; shift ;;
        --disable-teamspeak-setup)       ENABLE_TEAMSPEAK_SETUP=0; mark_explicit TEAMSPEAK_SETUP; shift ;;
        --enable-all)                    ENABLE_SERVICE_CONTROL=1; ENABLE_APACHE=1; ENABLE_APACHE_MANAGE=1; ENABLE_RKHUNTER_MANAGE=1; ENABLE_CERTBOT=1; ENABLE_CERTBOT_MANAGE=1; ENABLE_DOCKER=1; ENABLE_TEAMSPEAK=1; ENABLE_CRON_MANAGE=1; ENABLE_PORTSENTRY_MANAGE=1; ENABLE_SERVICE_ENABLE=1; ENABLE_PACKAGE_MANAGE=1; ENABLE_PATCH_MANAGE=1; ENABLE_FIREWALL_MANAGE=1; ENABLE_MAIL_SETUP=1; ENABLE_TEAMSPEAK_SETUP=1; mark_explicit SERVICE_CONTROL APACHE APACHE_MANAGE RKHUNTER_MANAGE CERTBOT CERTBOT_MANAGE DOCKER TEAMSPEAK CRON_MANAGE PORTSENTRY_MANAGE SERVICE_ENABLE PACKAGE_MANAGE PATCH_MANAGE FIREWALL_MANAGE MAIL_SETUP TEAMSPEAK_SETUP; shift ;;
        --disable-all)                   ENABLE_SERVICE_CONTROL=0; ENABLE_APACHE=0; ENABLE_APACHE_MANAGE=0; ENABLE_RKHUNTER_MANAGE=0; ENABLE_CERTBOT=0; ENABLE_CERTBOT_MANAGE=0; ENABLE_DOCKER=0; ENABLE_TEAMSPEAK=0; ENABLE_CRON_MANAGE=0; ENABLE_PORTSENTRY_MANAGE=0; ENABLE_SERVICE_ENABLE=0; ENABLE_PACKAGE_MANAGE=0; ENABLE_PATCH_MANAGE=0; ENABLE_FIREWALL_MANAGE=0; ENABLE_MAIL_SETUP=0; ENABLE_TEAMSPEAK_SETUP=0; ENABLE_DEPLOYMENT=0; MODULE_DEPLOYMENT=0; mark_explicit SERVICE_CONTROL APACHE APACHE_MANAGE RKHUNTER_MANAGE CERTBOT CERTBOT_MANAGE DOCKER TEAMSPEAK CRON_MANAGE PORTSENTRY_MANAGE SERVICE_ENABLE PACKAGE_MANAGE PATCH_MANAGE FIREWALL_MANAGE MAIL_SETUP TEAMSPEAK_SETUP DEPLOYMENT; shift ;;
        --allow-insecure-certs) ALLOW_INSECURE_CERTS=1; shift ;;
        --module)
            case "$2" in
                pipeline-runner)     MODULE_PIPELINE_RUNNER=1 ;;
                no-pipeline-runner)  MODULE_PIPELINE_RUNNER=0 ;;
                server-management)   MODULE_SERVER_MANAGEMENT=1 ;;
                deployment)          MODULE_DEPLOYMENT=1; mark_explicit DEPLOYMENT ;;
                *) log_warn "Unknown module '$2' (expected: pipeline-runner | server-management | deployment)" ;;
            esac
            shift 2 ;;
        --web-root)   DEPLOY_WEB_ROOT="$2"; shift 2 ;;
        --no-pipeline-runner)   MODULE_PIPELINE_RUNNER=0; shift ;;
        -y|--yes)     ASSUME_YES=1; shift ;;
        -h|--help)    show_help; exit 0 ;;
        *) log_warn "Ignoring unknown argument: $1"; shift ;;
    esac
done

# Expand the server-management module to the granular elevation flags it implies.
# (--enable-* flags can still be used on their own for fine-grained control.)
if [ "$MODULE_SERVER_MANAGEMENT" -eq 1 ]; then
    ENABLE_SERVICE_CONTROL=1; ENABLE_APACHE=1; ENABLE_APACHE_MANAGE=1
    ENABLE_RKHUNTER_MANAGE=1; ENABLE_CERTBOT=1; ENABLE_DOCKER=1; ENABLE_TEAMSPEAK=1
    ENABLE_CRON_MANAGE=1; ENABLE_PORTSENTRY_MANAGE=1; ENABLE_SERVICE_ENABLE=1
    ENABLE_PACKAGE_MANAGE=1; ENABLE_MAIL_SETUP=1; ENABLE_TEAMSPEAK_SETUP=1
    mark_explicit SERVICE_CONTROL APACHE APACHE_MANAGE RKHUNTER_MANAGE CERTBOT DOCKER TEAMSPEAK         CRON_MANAGE PORTSENTRY_MANAGE SERVICE_ENABLE PACKAGE_MANAGE MAIL_SETUP TEAMSPEAK_SETUP
fi

# The deployment module is independent (not part of server-management): it provisions the deploy
# target capability. Modelled as ENABLE_DEPLOYMENT so it slots into the same provisioning/teardown,
# posture-summary, NoNewPrivileges and upgrade-preservation machinery as the other capabilities.
if [ "$MODULE_DEPLOYMENT" -eq 1 ]; then
    ENABLE_DEPLOYMENT=1
fi

# R-249: the ACME web root the certbot and mail-manage helper templates declare as #{ACME_WEBROOT}#.
ACME_WEBROOT="${ACME_WEBROOT_OVERRIDE:-/var/www/aetheus-acme}"

# =============================================================================
# Helper functions
# =============================================================================

# Stop and disable the service if it exists; returns 0 always (idempotent).
stop_and_disable_service() {
    if systemctl list-unit-files 2>/dev/null | grep -q "^${SERVICE_NAME}\.service"; then
        if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
            log_info "  Stopping ${SERVICE_NAME}..."
            systemctl stop "$SERVICE_NAME" || log_warn "  Failed to stop ${SERVICE_NAME}"
        fi
        if systemctl is-enabled --quiet "$SERVICE_NAME" 2>/dev/null; then
            log_info "  Disabling ${SERVICE_NAME}..."
            systemctl disable "$SERVICE_NAME" 2>/dev/null || log_warn "  Failed to disable ${SERVICE_NAME}"
        fi
    fi
}

# Remove the systemd unit file and reload daemon; returns 0 always.
remove_systemd_unit() {
    if [ -f "$SYSTEMD_UNIT_PATH" ]; then
        log_info "  Removing systemd unit..."
        rm -f "$SYSTEMD_UNIT_PATH"
        systemctl daemon-reload
        systemctl reset-failed "$SERVICE_NAME" 2>/dev/null || true
    fi
}

install_agent_update_supervisor() {
    _update_server_url="$1"
    case "$_update_server_url" in
        https://*|http://localhost*|http://127.0.0.1*|http://\[::1\]*) ;;
        *)
            log_error "Agent update supervisor requires HTTPS (HTTP is allowed only for loopback)."
            return 1
            ;;
    esac

    install -d -m 755 "$AETHEUS_HELPER_DIR"
    printf '%s\n' "$_update_server_url" > "$AGENT_UPDATE_URL_FILE"
    chown root:root "$AGENT_UPDATE_URL_FILE"
    chmod 600 "$AGENT_UPDATE_URL_FILE"

    render_host_config agent-update/agent-posture-upgrade "$AGENT_UPDATE_WORKER_PATH.tmp.$$"
    mv "$AGENT_UPDATE_WORKER_PATH.tmp.$$" "$AGENT_UPDATE_WORKER_PATH"
    chown root:root "$AGENT_UPDATE_WORKER_PATH"
    chmod 755 "$AGENT_UPDATE_WORKER_PATH"

    render_host_config agent-update/aetheus-agent-upgrade.service "$AGENT_UPDATE_SERVICE_PATH.tmp.$$"
    mv "$AGENT_UPDATE_SERVICE_PATH.tmp.$$" "$AGENT_UPDATE_SERVICE_PATH"

    render_host_config agent-update/aetheus-agent-upgrade.path "$AGENT_UPDATE_PATH_PATH.tmp.$$"
    mv "$AGENT_UPDATE_PATH_PATH.tmp.$$" "$AGENT_UPDATE_PATH_PATH"
    chown root:root "$AGENT_UPDATE_SERVICE_PATH" "$AGENT_UPDATE_PATH_PATH"
    chmod 644 "$AGENT_UPDATE_SERVICE_PATH" "$AGENT_UPDATE_PATH_PATH"
    printf '%s\n' "$AGENT_POSTURE_VERSION" > "$AGENT_POSTURE_VERSION_FILE"
    chown root:root "$AGENT_POSTURE_VERSION_FILE"
    chmod 644 "$AGENT_POSTURE_VERSION_FILE"
    systemctl daemon-reload
    systemctl enable --now aetheus-agent-upgrade.path
}

# Revoke Docker membership when disabling the capability only for the dedicated account or when a
# root-owned marker proves the installer added it; otherwise report the shared account's
# root-equivalent access without changing membership. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
docker_group_added_by_installer() {
    [ -f "$DOCKER_GROUP_MARKER_FILE" ] && [ "$(cat "$DOCKER_GROUP_MARKER_FILE" 2>/dev/null)" = "$AGENT_USER" ]
}

record_docker_group_added() {
    printf '%s\n' "$AGENT_USER" > "$DOCKER_GROUP_MARKER_FILE"
    chown root:root "$DOCKER_GROUP_MARKER_FILE"
    chmod 644 "$DOCKER_GROUP_MARKER_FILE"
}

revoke_docker_group() {
    is_explicit DOCKER || return 0
    if ! id -nG "$AGENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx docker; then
        rm -f "$DOCKER_GROUP_MARKER_FILE"
        return 0
    fi
    if docker_group_added_by_installer; then
        log_info "  Removing $AGENT_USER from the docker group (docker disabled; membership added by this installer)..."
        if ! gpasswd -d "$AGENT_USER" docker >/dev/null; then
            log_error "  Could not remove $AGENT_USER from the docker group: the agent keeps docker access."
            return 1
        fi
        rm -f "$DOCKER_GROUP_MARKER_FILE"
        return 0
    fi
    _agent_entry="$(getent passwd "$AGENT_USER" 2>/dev/null || true)"
    _agent_home="$(printf '%s' "$_agent_entry" | cut -d: -f6)"
    _agent_shell="$(printf '%s' "$_agent_entry" | cut -d: -f7)"
    case "$_agent_shell" in
        /bin/false|/usr/sbin/nologin|/sbin/nologin|/usr/bin/false) _agent_dedicated=1 ;;
        *) _agent_dedicated=0 ;;
    esac
    # No marker: the membership predates the marker or was not added by this installer. A dedicated
    # account (the one this installer creates) is still cleaned, as an installer from before the marker
    # added it; a shared account keeps it and the agent reports the root-equivalent posture.
    if [ "$_agent_home" != "$WORK_DIR" ] || [ "$_agent_dedicated" -ne 1 ]; then
        log_warn "  $AGENT_USER is in the docker group, which this installer did not add, and is not the dedicated agent account (home '$_agent_home', shell '$_agent_shell'): membership left as it is (root-equivalent; the server page shows it). Remove it by hand if that is intended."
        return 0
    fi
    log_info "  Removing $AGENT_USER from the docker group (docker disabled)..."
    if ! gpasswd -d "$AGENT_USER" docker >/dev/null; then
        log_error "  Could not remove $AGENT_USER from the docker group: the agent keeps docker access."
        return 1
    fi
}

ensure_docker_group() {
    # Opt-in only (--enable-docker). Docker-group membership is ~= root, so it is
    # not granted by default. DockerCollector needs it for `docker ps -a` etc.
    if [ "$ENABLE_DOCKER" -ne 1 ]; then
        # A failed removal is logged and shown in the posture summary; it does not abort an upgrade
        # half-way through.
        revoke_docker_group || true
        return 0
    fi
    if ! getent group docker >/dev/null 2>&1; then
        log_info "  Docker group not found - skipping (install Docker to enable container collection)"
        return 0
    fi
    if id -nG "$AGENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx docker; then
        # An existing marker is kept; a membership found without one was not added here and is never
        # claimed, so a later --disable-docker or --purge cannot take it from a shared account.
        log_info "  $AGENT_USER already in docker group"
    else
        log_info "  Adding $AGENT_USER to docker group..."
        if usermod -aG docker "$AGENT_USER"; then
            record_docker_group_added
        else
            log_warn "  Failed to add to docker group (non-fatal)"
        fi
    fi
    # Container deploys (pipeline `type: deploy` with a compose file → `docker compose up -d --wait`)
    # need the Compose v2 plugin. The Ubuntu docker.io package has no compose plugin; Ubuntu ships it
    # separately as docker-compose-v2. Best-effort + idempotent (only when `docker compose` is missing).
    if ! docker compose version >/dev/null 2>&1; then
        log_info "  Installing Docker Compose v2 plugin (docker-compose-v2) for container deploys..."
        install_package docker-compose-v2 || log_warn "  Could not install docker-compose-v2 - container deploys (compose) will not work until it is present."
    fi
}

ensure_teamspeak_access() {
    # Opt-in only (--enable-teamspeak / --module server-management). Grants no sudo.
    # Detection of a running TeamSpeak works WITHOUT this (pidof + default query port 10011);
    # this only (a) lets the collector read ts3server.ini for a NON-default query port by joining
    # the 'teamspeak' group, and (b) scaffolds the agent-local credentials dir so the operator can
    # drop the serveradmin ServerQuery password the typed ServerQuery operation (S-TECH-87) needs.
    if [ "$ENABLE_TEAMSPEAK" -ne 1 ]; then
        return 0
    fi

    if getent group teamspeak >/dev/null 2>&1; then
        if id -nG "$AGENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx teamspeak; then
            log_info "  $AGENT_USER already in teamspeak group"
        else
            log_info "  Adding $AGENT_USER to teamspeak group (read ts3server.ini for query port)..."
            usermod -aG teamspeak "$AGENT_USER" || log_warn "  Failed to add to teamspeak group (non-fatal)"
        fi
    else
        log_info "  No 'teamspeak' group found - skipping group membership (default port 10011 still works)"
    fi

    # Scaffold the agent-local credentials dir (inside INSTALL_DIR, owned by the agent, 0700).
    _ts_dir="$INSTALL_DIR/teamspeak"
    _ts_cred="$_ts_dir/query-credentials"
    mkdir -p "$_ts_dir"
    chown "$AGENT_USER:$AGENT_GROUP" "$_ts_dir" 2>/dev/null || true
    chmod 700 "$_ts_dir" 2>/dev/null || true
    if [ -s "$_ts_cred" ]; then
        chown "$AGENT_USER:$AGENT_GROUP" "$_ts_cred" 2>/dev/null || true
        chmod 600 "$_ts_cred" 2>/dev/null || true
        log_info "  TeamSpeak query credential present."
    else
        log_warn "  TeamSpeak ServerQuery credential missing - drop the serveradmin password into:"
        log_warn "    $_ts_cred   (then: chown $AGENT_USER $_ts_cred; chmod 600 it)"
        log_warn "  Until then, TeamSpeak ServerQuery actions will report a missing-credential error."
    fi
}

# Minimal JSON string escaper (backslash + double-quote) for values written into
# appsettings.json. Defeats config-structure injection from operator-supplied
# URL/token/name containing " or \.
json_escape() {
    printf '%s' "$1" | sed -e 's/\\/\\\\/g' -e 's/"/\\"/g'
}

# Drives the "Last update" field shown on the agent detail page. Stamped on every fresh
# install AND every --upgrade so the UI always reflects the most recent binary swap.
# Format: ISO-8601 UTC ("2026-05-21T14:33:07Z"). Owned by $AGENT_USER (the agent reads it
# at startup), mode 644 so the marker is readable even if the agent is later moved to a
# different account. Safe to call when $WORK_DIR doesn't exist yet - we create it.
write_installed_at_marker() {
    _iat_path="$WORK_DIR/.installed-at"
    mkdir -p "$WORK_DIR"
    date -u +"%Y-%m-%dT%H:%M:%SZ" > "$_iat_path"
    if id "$AGENT_USER" >/dev/null 2>&1; then
        chown "$AGENT_USER:$AGENT_GROUP" "$_iat_path" 2>/dev/null || true
    fi
    chmod 644 "$_iat_path" 2>/dev/null || true
    log_info "  Recorded install timestamp: $(cat "$_iat_path")"
}

# Refresh the apt package index exactly once per run. A freshly-imaged box (the blank
# VPS-sim, or a cloud image whose /var/lib/apt/lists was never populated) ships no package
# index, so `apt-get install` would fail to locate git/sshpass/acl/dotnet. Best-effort: a
# failed refresh falls back to whatever cached index exists (a warning, not fatal).
# Set to 1 by grant_sudoers_read_acls when a hash-derived capability was requested but setfacl
# (the `acl` package) could not be made available, meaning the sudo grant IS installed but the
# non-root agent cannot read the drop-in to hash it, so the capability reports OFF in the UI.
# Surfaced loudly in print_posture_summary so a `-y` operator can't miss it (the exact symptom that
# made "everything enabled" silently yield package-manage = unavailable on the live box).
ACL_GRANT_DEGRADED=0

APT_UPDATED=0
ensure_apt_updated() {
    if [ "$APT_UPDATED" -eq 1 ]; then
        return 0
    fi
    APT_UPDATED=1
    if command -v apt-get >/dev/null 2>&1; then
        log_info "  Refreshing apt package index..."
        apt-get update >/dev/null 2>&1 || log_warn "  apt-get update failed (continuing with cached index)."
    fi
}

# Install a system package by name via the detected package manager.
# Args: $1 = package name. Returns 0 on success, 1 on failure.
install_package() {
    _ip_pkg="$1"
    if command -v apt-get >/dev/null 2>&1; then
        ensure_apt_updated
        apt-get install -y "$_ip_pkg" >/dev/null 2>&1 && log_info "  '$_ip_pkg' installed." && return 0
    elif command -v yum >/dev/null 2>&1; then
        yum install -y "$_ip_pkg" >/dev/null 2>&1 && log_info "  '$_ip_pkg' installed." && return 0
    fi
    log_warn "  Failed to install '$_ip_pkg'."
    return 1
}

# Ask user confirmation and install a package if missing.
# Args: $1 = command to check, $2 = package name, $3 = reason description.
# Respects ASSUME_YES. Returns 0 if command available after, 1 otherwise.
ask_install_package() {
    _aip_cmd="$1"; _aip_pkg="$2"; _aip_reason="$3"
    if command -v "$_aip_cmd" >/dev/null 2>&1; then
        return 0
    fi
    if [ "$ASSUME_YES" -eq 1 ]; then
        log_info "  Installing '$_aip_pkg' ($ASSUME_YES mode)..."
        install_package "$_aip_pkg"
        return $?
    fi
    printf "  '%s' is not installed (%s). Install it? [Y/n] " "$_aip_pkg" "$_aip_reason"
    read _aip_answer
    case "$_aip_answer" in
        [nN]*) log_info "  Skipping '$_aip_pkg'."; return 1 ;;
        *) install_package "$_aip_pkg"; return $? ;;
    esac
}

# Ask user confirmation and install a package if a shared library is missing.
# Args: $1 = soname to look up in the linker cache, $2 = apt package, $3 = yum package,
# $4 = reason description. Respects ASSUME_YES. Returns 0 if the library is present after.
ask_install_library() {
    _ail_soname="$1"; _ail_reason="$4"
    _ail_pkg="$2"
    if ! command -v apt-get >/dev/null 2>&1; then
        _ail_pkg="$3"
    fi
    if ldconfig -p 2>/dev/null | grep -Fq "$_ail_soname"; then
        return 0
    fi
    if [ "$ASSUME_YES" -eq 1 ]; then
        log_info "  Installing '$_ail_pkg' (assume-yes mode)..."
    else
        printf "  '%s' is not installed (%s). Install it? [Y/n] " "$_ail_pkg" "$_ail_reason"
        read _ail_answer
        case "$_ail_answer" in
            [nN]*) log_info "  Skipping '$_ail_pkg'."; return 1 ;;
        esac
    fi
    install_package "$_ail_pkg" || return 1
    ldconfig -p 2>/dev/null | grep -Fq "$_ail_soname"
}

# Install the host tools the pipeline-runner module needs. Language SDKs are resolved
# from the repository lock manifest and run inside immutable OCI images.
# No elevation is granted here - these are ordinary packages the agent invokes as
# its own unprivileged user when executing pipeline steps.
ensure_pipeline_runner_packages() {
    log_info "Pipeline-runner module: ensuring build/deploy toolchain..."
    ask_install_package "git" "git" "required to clone pipeline repositories" || true
    ask_install_package "sshpass" "sshpass" "used by SSH-based pipeline deployments" || true
    # S-FEAT-BLDX: buildx is the modern (non-legacy) docker build backend. Best-effort: on a box without
    # the plugin, docker build still works via the legacy builder, so a missing plugin never blocks install.
    ask_install_package "docker-buildx" "docker-buildx-plugin" "used for BuildKit / multi-platform container builds" || true
    # The .NET SDK the pipelines download runs on this host, and its wasm-tools workload links the
    # frontend with the Node.js bundled in the Emscripten pack. That Node.js needs libatomic.so.1, which
    # a minimal Ubuntu image does not ship: the frontend publish then dies with "error while loading
    # shared libraries: libatomic.so.1" (local mirror CI run 1272). An unprivileged pipeline cannot
    # install it, so the runner module does.
    ask_install_library "libatomic.so.1" "libatomic1" "libatomic" "needed by the Node.js of the .NET wasm-tools workload" || true
}

# The deployment module also owns explicit, verified database restores during a release rollback.
# Install client-only tools: no database server or daemon is added to the host. These commands run as
# the unprivileged agent and authenticate with task-scoped protected environment variables.
ensure_backup_restore_packages() {
    log_info "Deployment module: ensuring database backup/restore clients..."
    if command -v apt-get >/dev/null 2>&1; then
        ask_install_package "pg_restore" "postgresql-client" "required for verified PostgreSQL backup restore" || true
        ask_install_package "mysql" "default-mysql-client" "required for verified MySQL backup restore" || true
    elif command -v yum >/dev/null 2>&1; then
        ask_install_package "pg_restore" "postgresql" "required for verified PostgreSQL backup restore" || true
        ask_install_package "mysql" "mysql" "required for verified MySQL backup restore" || true
    fi
}

# Install optional dependencies before the main setup begins.
ensure_optional_packages() {
    if [ "$MODULE_PIPELINE_RUNNER" -eq 1 ]; then
        ensure_pipeline_runner_packages
    fi
    if [ "$ENABLE_DEPLOYMENT" -eq 1 ]; then
        ensure_backup_restore_packages
    fi
    _eop_need_acl=0
    # ACL is needed for the Apache/Certbot read trees AND for granting the agent read access to the
    # sudoers drop-ins it hashes each heartbeat to self-report its capabilities (package-manage,
    # deployment, …). So require it whenever ANY elevation that writes a sudoers drop-in is enabled -
    # otherwise the grant exists but the non-root agent can't read it and the capability stays OFF.
    if [ "$ENABLE_APACHE" -eq 1 ] || [ "$ENABLE_APACHE_MANAGE" -eq 1 ] || [ "$ENABLE_CERTBOT" -eq 1 ] \
        || [ "$ENABLE_SERVICE_CONTROL" -eq 1 ] || [ "$ENABLE_RKHUNTER_MANAGE" -eq 1 ] \
        || [ "$ENABLE_CRON_MANAGE" -eq 1 ] || [ "$ENABLE_PORTSENTRY_MANAGE" -eq 1 ] \
        || [ "$ENABLE_SERVICE_ENABLE" -eq 1 ] || [ "$ENABLE_PACKAGE_MANAGE" -eq 1 ] \
        || [ "$ENABLE_PATCH_MANAGE" -eq 1 ] || [ "$ENABLE_FIREWALL_MANAGE" -eq 1 ] \
        || [ "$ENABLE_MAIL_SETUP" -eq 1 ] || [ "$ENABLE_TEAMSPEAK_SETUP" -eq 1 ] || [ "$ENABLE_DEPLOYMENT" -eq 1 ]; then
        _eop_need_acl=1
    fi
    if [ "$_eop_need_acl" -eq 1 ]; then
        ask_install_package "setfacl" "acl" "required for file ACL management"
        # S-TECH-ACLB: you explicitly requested an elevation that writes a sudoers drop-in (package-manage,
        # deployment, apache/certbot, mail, teamspeak, service-control, ...). Without 'acl' the non-root
        # agent cannot read that drop-in to self-report the capability, so it would silently stay OFF. Fail
        # hard here instead of degrading in silence - the operator asked for the capability, so a missing
        # setfacl is a hard error, not a best-effort skip.
        if ! command -v setfacl >/dev/null 2>&1; then
            log_error "'acl' (setfacl) is required for the elevation you requested but could not be installed. Run 'apt-get install -y acl' then re-run, or drop the elevation flags."
            exit 1
        fi
    else
        # Not required by an elevation, but the read-only SSH configuration grant (aetheus-agent-read-grant)
        # is an ACL: without setfacl it is skipped and the SSH card shows those files as unknown.
        ask_install_package "setfacl" "acl" "lets the agent read the SSH configuration it monitors" \
            || log_warn "  setfacl unavailable: the agent will not be able to read the SSH files that need an ACL (the SSH card shows them as unknown)."
    fi
}

# Ensure the ICU runtime is present. The self-contained .NET agent does NOT bundle ICU, so a bare
# Ubuntu/Debian image (the blank VPS-sim, or a minimal cloud image) makes the runtime FailFast at
# startup ("Couldn't find a valid ICU package") - which the pre-start probe then misreports as a
# backend-connectivity failure. Mandatory regardless of capability modules: without ICU the agent
# never boots. Package installation remains best-effort; the runtime probe stays the hard gate.
ensure_icu() {
    if command -v ldconfig >/dev/null 2>&1 && ldconfig -p 2>/dev/null | grep -Eq 'libicuuc|libicui18n'; then
        log_info "  ICU runtime already present."
        return 0
    fi
    log_info "Ensuring ICU runtime (required by the .NET agent)..."
    if command -v apt-get >/dev/null 2>&1; then
        ensure_apt_updated
        # Debian/Ubuntu ship no unversioned 'libicu' runtime package - pick the highest libicuNN.
        _icu_pkg="$(apt-cache --names-only search '^libicu[0-9][0-9]*$' 2>/dev/null | awk '{print $1}' | sort -V | tail -n1)"
        if [ -n "$_icu_pkg" ] && apt-get install -y "$_icu_pkg" >/dev/null 2>&1; then
            log_info "  ICU runtime installed ($_icu_pkg)."
            return 0
        fi
    elif command -v yum >/dev/null 2>&1; then
        # RHEL/CentOS/Fedora provide the unversioned 'libicu'.
        if yum install -y libicu >/dev/null 2>&1; then
            log_info "  ICU runtime installed (libicu)."
            return 0
        fi
    fi
    log_warn "  Could not install ICU automatically - the agent may FailFast at startup."
    log_warn "  Install it manually (e.g. 'apt-get install libicu74') or publish with InvariantGlobalization."
    return 1
}

# Grant the agent READ-ONLY access to a config tree via POSIX ACL - no sudo, no
# group, no root execution. Falls back to a clear warning if setfacl is absent.
# Args: $1 = human label, $2.. = directories.
grant_read_acl() {
    _gra_label="$1"; shift
    if ! command -v setfacl >/dev/null 2>&1; then
        log_warn "  setfacl not available - skipping ACL for ${_gra_label}."
        return 0
    fi
    for _gra_dir in "$@"; do
        [ -d "$_gra_dir" ] || continue
        log_info "  Granting $AGENT_USER read access to $_gra_dir (${_gra_label})..."
        # rX = read + dir-traverse only; default ACL so new files inherit it.
        setfacl -R  -m "u:$AGENT_USER:rX" "$_gra_dir" 2>/dev/null \
            || log_warn "  setfacl failed on $_gra_dir (non-fatal)"
        setfacl -R -d -m "u:$AGENT_USER:rX" "$_gra_dir" 2>/dev/null || true
    done
}

apply_read_acls() {
    if [ "$ENABLE_APACHE" -eq 1 ]; then
        grant_read_acl "Apache introspection" /etc/apache2 /etc/httpd
    fi
    if [ "$ENABLE_CERTBOT" -eq 1 ]; then
        grant_read_acl "Certbot introspection" /etc/letsencrypt
    fi
    # The recursive read-only grant above (rX on all of /etc/apache2) would remove the directory
    # write ACL installed for Apache management. Re-assert directory rwx LAST, but never install a
    # default write ACL on new entries. Sticky directories prevent the agent from unlinking or
    # replacing root-owned/shared-host vhosts while still allowing it to manage files it owns.
    if [ "$ENABLE_APACHE_MANAGE" -eq 1 ] && command -v setfacl >/dev/null 2>&1; then
        for d in /etc/apache2/sites-available /etc/apache2/sites-enabled; do
            if [ -d "$d" ]; then
                setfacl -m "u:$AGENT_USER:rwx" "$d" 2>/dev/null || true
                setfacl -x "d:u:$AGENT_USER" "$d" 2>/dev/null || true
                chmod +t "$d"
            fi
        done
    fi
}

# Derive unchanged capabilities from the installed host posture so upgrades neither widen nor drop
# operator-selected privileges; explicit flags retain precedence for their own capability. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
preserve() {
    is_explicit "$1" && return 0
    eval "ENABLE_$1=1"
    [ -z "${2:-}" ] || log_info "  Preserving existing $2."
}
derive_existing_posture() {
    if [ -f "$SUDOERS_FILE" ]; then
        preserve SERVICE_CONTROL "service-control sudoers rule"
    fi
    if [ -f "$APACHE_MANAGE_SUDOERS_FILE" ]; then
        preserve APACHE_MANAGE "Apache-manage sudoers rule"
        preserve APACHE  # implies the read ACL too
    fi
    if [ -f "$RKHUNTER_MANAGE_SUDOERS_FILE" ]; then
        preserve RKHUNTER_MANAGE "RKHunter-manage sudoers rule"
    fi
    # certbot-manage was historically absent from this derivation, so every reinstall
    # silently revoked it via apply_sudoers and the production HTTPS issuance broke.
    if [ -f "$CERTBOT_MANAGE_SUDOERS_FILE" ]; then
        preserve CERTBOT_MANAGE "Certbot-manage sudoers rule"
        preserve CERTBOT        # implies the read ACL
        preserve APACHE_MANAGE  # certbot-manage drives apache2ctl through the helper
        preserve APACHE
    fi
    if [ -f "$CRON_MANAGE_SUDOERS_FILE" ]; then
        preserve CRON_MANAGE "cron-manage sudoers rule"
    fi
    if [ -f "$PORTSENTRY_MANAGE_SUDOERS_FILE" ]; then
        preserve PORTSENTRY_MANAGE "portsentry-manage sudoers rule"
    fi
    if [ -f "$SERVICE_ENABLE_SUDOERS_FILE" ]; then
        preserve SERVICE_ENABLE "service-enable sudoers rule"
    fi
    if [ -f "$PACKAGE_MANAGE_SUDOERS_FILE" ]; then
        preserve PACKAGE_MANAGE "package-manage sudoers rule"
    fi
    if [ -f "$PATCH_MANAGE_SUDOERS_FILE" ]; then
        preserve PATCH_MANAGE "patch-manage sudoers rule"
    fi
    if [ -f "$FIREWALL_MANAGE_SUDOERS_FILE" ]; then
        preserve FIREWALL_MANAGE "firewall-manage sudoers rule"
    fi
    if [ -f "$MAIL_MANAGE_SUDOERS_FILE" ]; then
        preserve MAIL_SETUP "mail-setup sudoers rule"
    fi
    if [ -f "$TEAMSPEAK_SETUP_SUDOERS_FILE" ]; then
        preserve TEAMSPEAK_SETUP "teamspeak-setup sudoers rule"
    fi
    if [ -f "$DEPLOY_MANAGE_SUDOERS_FILE" ]; then
        preserve DEPLOYMENT "deployment-module sudoers rule"
    fi
    if command -v getfacl >/dev/null 2>&1; then
        if getfacl -p /etc/apache2 2>/dev/null | grep -q "user:$AGENT_USER:"; then
            preserve APACHE "Apache read ACL"
        fi
        if getfacl -p /etc/letsencrypt 2>/dev/null | grep -q "user:$AGENT_USER:"; then
            preserve CERTBOT "Certbot read ACL"
        fi
    fi
    if id -nG "$AGENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx docker; then
        preserve DOCKER "docker-group membership"
    fi
    if id -nG "$AGENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx teamspeak || [ -d "$INSTALL_DIR/teamspeak" ]; then
        preserve TEAMSPEAK "TeamSpeak access provisioning"
    fi
}

# Clear only agent state during reinstallation and preserve the deployment tree so delivered
# applications retain their releases and secrets; keep the work directory for idempotent ownership
# setup. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
purge_agent_work_dir() {
    [ -d "$WORK_DIR" ] || return 0
    [ -n "$(ls -A "$WORK_DIR" 2>/dev/null)" ] || return 0
    log_info "  Purging agent state from the work dir ($WORK_DIR)..."
    for _entry in "${WORK_DIR:?}"/* "${WORK_DIR:?}"/.[!.]* "${WORK_DIR:?}"/..?*; do
        [ -e "$_entry" ] || [ -L "$_entry" ] || continue
        if [ "$_entry" = "$DEPLOY_BASE_DIR" ]; then
            log_info "  Keeping the deployment tree ($DEPLOY_BASE_DIR)."
            continue
        fi
        rm -rf "$_entry" 2>/dev/null || true
    done
}

write_sudoers() {
    # Only ever called when --enable-service-control is active. Service start/stop
    # genuinely needs privilege and cannot be done via file ACLs, so this is the
    # single remaining sudo grant. Hardened: exact commands (no wildcards - shell
    # meta-chars would bypass the restriction), runas root only (not ALL), env
    # reset + fixed secure_path, and Docker/Apache/Certbot are NEVER added here
    # (docker = group; apache2ctl/certbot are GTFOBins root-escalation wrappers;
    #  apache/certbot use read-only ACLs instead).
    # Note on the docker.service entries below: this rule grants ONLY the systemctl start/stop/restart/status
    # lifecycle of the docker DAEMON (argv-exact, no wildcard) - NOT the docker CLI, which is the real
    # container-escape surface (`docker run -v /:/host`) and stays behind the docker group. The whole rule is
    # opt-in (written only when service-control is enabled), so the daemon-lifecycle grant is an explicit
    # choice, scoped so the agent can bounce a wedged daemon without gaining a root shell.
    log_info "Configuring narrow service-control sudoers rule..."
    render_host_config agent/sudoers.d/aetheus-agent "$SUDOERS_FILE"
    chmod 440 "$SUDOERS_FILE"
    visudo -cf "$SUDOERS_FILE" || {
        log_error "Invalid sudoers syntax"
        rm -f "$SUDOERS_FILE"
        exit 1
    }
}

# Item #6 - controlled sudo escalation for Apache manage. Strictly follows the recipe in
# docs/contracts/security.md#controlled-sudo-escalation:
#   - exact argv (no wildcards) so apache2ctl -f <arbitrary> and friends are unreachable
#   - noexec for fixed systemctl commands; a root-owned no-argument helper for configtest,
#     because apache2ctl must exec apache2 (ADR-019 compensating boundary)
#   - timestamp_timeout=0 so a concurrent process cannot inherit the sudo cache
#   - env_reset + fixed secure_path to neutralise PATH-style injection vectors
#   - one justification comment per Cmnd_Alias entry - forces manual review on extension
write_apache_manage_sudoers() {
    log_info "Configuring Apache-manage sudoers (item #6, ultra-strict allow-list)..."
    mkdir -p "$(dirname "$APACHE_CONFIGTEST_HELPER_PATH")"
    render_host_config apache/aetheus-apache-configtest "$APACHE_CONFIGTEST_HELPER_PATH"
    chown root:root "$APACHE_CONFIGTEST_HELPER_PATH"
    chmod 755 "$APACHE_CONFIGTEST_HELPER_PATH"

    # The blue-green switch step invokes its reload helper as `sudo -n <path>` with argv exactly one
    # element, so the three-word `systemctl reload apache2.service` grant above is unreachable from
    # it. A shell step could pass three words; a typed step has no shell. Hence this second
    # no-argument helper, on the same fixed-purpose pattern as configtest above.
    render_host_config apache/aetheus-apache-reload "$APACHE_RELOAD_HELPER_PATH"
    chown root:root "$APACHE_RELOAD_HELPER_PATH"
    chmod 755 "$APACHE_RELOAD_HELPER_PATH"

    render_host_config apache/sudoers.d/aetheus-apache "$APACHE_MANAGE_SUDOERS_FILE"
    chmod 440 "$APACHE_MANAGE_SUDOERS_FILE"
    visudo -cf "$APACHE_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid Apache-manage sudoers syntax"
        rm -f "$APACHE_MANAGE_SUDOERS_FILE"
        rm -f "$APACHE_CONFIGTEST_HELPER_PATH"
        rm -f "$APACHE_RELOAD_HELPER_PATH"
        exit 1
    }

    # Directory write is needed to create vhosts and enable them with symlinks. Do NOT grant a
    # default ACL: it would make unrelated future vhosts agent-writable. Sticky-bit protection
    # prevents the agent from unlinking/replacing root-owned shared-host entries. On upgrade, take
    # ownership only of files carrying the explicit Aetheus marker so existing managed vhosts stay
    # editable without widening access to unrelated sites.
    if command -v setfacl >/dev/null 2>&1; then
        for d in /etc/apache2/sites-available /etc/apache2/sites-enabled; do
            if [ -d "$d" ]; then
                setfacl -m "u:$AGENT_USER:rwx" "$d" || true
                setfacl -x "d:u:$AGENT_USER" "$d" 2>/dev/null || true
                chmod +t "$d"
                log_info "  Sticky directory ACL on $d granted to $AGENT_USER."
            fi
        done
        for f in /etc/apache2/sites-available/*.conf; do
            [ -f "$f" ] || continue
            if head -n 1 "$f" | grep -Fq '# Managed by Aetheus'; then
                chown "$AGENT_USER:$AGENT_GROUP" "$f"
                chmod u+rw "$f"
                enabled="/etc/apache2/sites-enabled/$(basename "$f")"
                [ -L "$enabled" ] && chown -h "$AGENT_USER:$AGENT_GROUP" "$enabled"
            fi
        done
    else
        log_warn "  setfacl not available - cannot set write ACL on Apache sites dirs."
    fi

    # The apache-proxy step renders a reverse-proxy vhost (ProxyPass), so the proxy modules must be
    # loaded. a2enmod is idempotent; enable them at install time (configtest later confirms).
    if command -v a2enmod >/dev/null 2>&1; then
        a2enmod proxy proxy_http headers >/dev/null 2>&1 \
            && log_info "  Enabled Apache modules: proxy, proxy_http, headers." \
            || log_warn "  Could not enable Apache proxy modules (a2enmod) - reverse proxy may need them."
    fi
}

# Certbot management - root-owned issue helper + NOPASSWD sudoers. certbot's argv is domain-variable
# (can't be argv-exact granted), so the helper IS the security boundary: it re-validates each domain,
# performs real ACME issuance through a dedicated webroot in production. The explicit local mode
# creates a self-signed certificate for a disposable local simulator without contacting ACME.
# Domains/email arrive via env (env_keep), never on the argv list.
write_certbot_manage() {
    log_info "Configuring certbot management: root-owned production ACME/local TLS helper + sudoers..."
    ensure_helper_dir

    # Substitute only the host's ACME_WEBROOT and reject unresolved placeholders; require_web_root
    # bounds it to a direct child of /var/www. See
    # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
    render_host_config certbot/aetheus-certbot-issue "$CERTBOT_ISSUE_HELPER_PATH"
    chown root:root "$CERTBOT_ISSUE_HELPER_PATH"
    chmod 755 "$CERTBOT_ISSUE_HELPER_PATH"

    # Lifecycle helper: renew/delete/revoke a named lineage (or renew all). The cert name is the only
    # variable and is re-validated here against the SAME shape the backend/agent enforce, so a fixed
    # certbot verb runs argv-exact - certbot stays GTFOBins-forbidden as a free-form sudo target.
    render_host_config certbot/aetheus-certbot-manage "$CERTBOT_MANAGE_HELPER_PATH"
    chown root:root "$CERTBOT_MANAGE_HELPER_PATH"
    chmod 755 "$CERTBOT_MANAGE_HELPER_PATH"

    render_host_config certbot/sudoers.d/aetheus-certbot "$CERTBOT_MANAGE_SUDOERS_FILE"
    chmod 440 "$CERTBOT_MANAGE_SUDOERS_FILE"
    visudo -cf "$CERTBOT_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid certbot-manage sudoers syntax"
        rm -f "$CERTBOT_MANAGE_SUDOERS_FILE"
        exit 1
    }
    write_certbot_conventions
}

# Use the same ACME web root for automated and manual certificate renewal. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
write_certbot_conventions() {
    _cc_webroot="${ACME_WEBROOT_OVERRIDE:-/var/www/aetheus-acme}"
    mkdir -p "$(dirname "$CERTBOT_CLI_INI")" "$(dirname "$CERTBOT_APACHE_DEPLOY_HOOK")" "$CERTBOT_STATE_DIR"
    chmod 755 "$CERTBOT_STATE_DIR"

    # cli.ini: only the marked block is ours; anything the operator wrote outside it is kept.
    [ -f "$CERTBOT_CLI_INI" ] || : > "$CERTBOT_CLI_INI"
    sed -i '/^# BEGIN aetheus-managed$/,/^# END aetheus-managed$/d' "$CERTBOT_CLI_INI"
    if grep -Eq '^[[:space:]]*(authenticator|webroot-path|webroot_path|apache|standalone)[[:space:]]*(=|$)' "$CERTBOT_CLI_INI"; then
        log_warn "  $CERTBOT_CLI_INI sets its own authenticator or web root outside the Aetheus block - review it."
    fi
    printf '%s\n' \
        '# BEGIN aetheus-managed' \
        '# Written by the Aetheus agent installer: a certificate requested without options renews' \
        '# through the Aetheus ACME web root, never through the Apache plugin.' \
        'authenticator = webroot' \
        "webroot-path = $_cc_webroot" \
        '# END aetheus-managed' >> "$CERTBOT_CLI_INI"
    chmod 644 "$CERTBOT_CLI_INI"

    # Webroot renewals do not touch Apache, so nothing reloads it: without this hook a renewed
    # certificate is only served after the next restart.
    render_host_config certbot/renewal-hooks-deploy/aetheus-apache "$CERTBOT_APACHE_DEPLOY_HOOK"
    chmod 755 "$CERTBOT_APACHE_DEPLOY_HOOK"

    # Weekly renewal rehearsal: a broken renewal surfaces weeks before the certificate expires.
    render_host_config certbot/aetheus-certbot-renewal-check.service "/etc/systemd/system/$CERTBOT_RENEWAL_CHECK_UNIT.service"
    render_host_config certbot/aetheus-certbot-renewal-check.timer "/etc/systemd/system/$CERTBOT_RENEWAL_CHECK_UNIT.timer"
    systemctl daemon-reload
    systemctl enable --now "$CERTBOT_RENEWAL_CHECK_UNIT.timer" >/dev/null 2>&1 \
        || log_warn "  Could not enable $CERTBOT_RENEWAL_CHECK_UNIT.timer."

    # Bring lineages issued before the convention (Apache plugin, old web root) onto it now.
    if command -v certbot >/dev/null 2>&1; then
        if "$CERTBOT_MANAGE_HELPER_PATH" normalize; then
            log_info "  Certbot lineages follow the Aetheus web root convention."
        else
            log_warn "  Some certbot lineages could not be normalized - see the output above and the Certbot section."
        fi
    fi
}

remove_certbot_conventions() {
    systemctl disable --now "$CERTBOT_RENEWAL_CHECK_UNIT.timer" >/dev/null 2>&1 || true
    rm -f "/etc/systemd/system/$CERTBOT_RENEWAL_CHECK_UNIT.service" "/etc/systemd/system/$CERTBOT_RENEWAL_CHECK_UNIT.timer"
    systemctl daemon-reload 2>/dev/null || true
    # cli.ini and the deploy hook stay: they keep manual renewals working without the agent.
}

# Remove legacy prometheus sudoers and helper artifacts best-effort so duplicate aliases and
# orphaned root grants cannot conflict with the installed agent. See
# docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
cleanup_legacy_prometheus_artifacts() {
    # EXACT names only - never a blind `prometheus-*` glob: an unrelated third party (the Prometheus
    # monitoring stack) ships its OWN sudoers drop-ins (prometheus-node-exporter, prometheus-alertmanager,
    # ...) and must never be deleted here. This list mirrors the aetheus-* drop-ins this installer writes.
    for _legacy in agent apache certbot cron deploy mail package portsentry rkhunter service-enable teamspeak; do
        _f="/etc/sudoers.d/prometheus-${_legacy}"
        if [ -f "$_f" ]; then
            log_info "Removing legacy ${_f} (pre-rename migration)..."
            rm -f "$_f" 2>/dev/null || true
        fi
    done
    # Old root-owned helper dir of the pre-rename agent (its own path, not shared with any third party).
    if [ -d /usr/local/lib/prometheus ]; then
        log_info "Removing legacy /usr/local/lib/prometheus helper dir..."
        rm -rf /usr/local/lib/prometheus 2>/dev/null || true
    fi
}

# Apply the configured sudoers state. With service-control disabled there is NO
# /etc/sudoers.d/aetheus-agent at all (the agent runs with zero sudo).
apply_sudoers() {
    cleanup_legacy_prometheus_artifacts
    if [ "$ENABLE_SERVICE_CONTROL" -eq 1 ]; then
        write_sudoers
    else
        rm -f "$SUDOERS_FILE"
        log_info "No sudoers rule installed (service control disabled - fully sandboxed)."
    fi
    if [ "$ENABLE_APACHE_MANAGE" -eq 1 ]; then
        write_apache_manage_sudoers
    else
        rm -f "$APACHE_MANAGE_SUDOERS_FILE"
        rm -f "$APACHE_CONFIGTEST_HELPER_PATH"
    fi
    # Certbot management: root-owned webroot ACME helper + NOPASSWD sudoers.
    if [ "$ENABLE_CERTBOT_MANAGE" -eq 1 ]; then
        write_certbot_manage
    else
        rm -f "$CERTBOT_MANAGE_SUDOERS_FILE" "$CERTBOT_ISSUE_HELPER_PATH" "$CERTBOT_MANAGE_HELPER_PATH"
        remove_certbot_conventions
    fi
    if [ "$ENABLE_RKHUNTER_MANAGE" -eq 1 ]; then
        write_rkhunter_manage_sudoers
    else
        rm -f "$RKHUNTER_MANAGE_SUDOERS_FILE" "$RKHUNTER_HELPER_PATH"
    fi
    # Phase 3 - cron management: root-owned helper + NOPASSWD sudoers for that one binary.
    if [ "$ENABLE_CRON_MANAGE" -eq 1 ]; then
        write_cron_manage
    else
        rm -f "$CRON_MANAGE_SUDOERS_FILE" "$CRON_HELPER_PATH"
    fi
    # Phase 3 - portsentry management: root-owned unblock-ip + portsentry-setup helpers + NOPASSWD sudoers.
    if [ "$ENABLE_PORTSENTRY_MANAGE" -eq 1 ]; then
        write_portsentry_manage
    else
        rm -f "$PORTSENTRY_MANAGE_SUDOERS_FILE" "$UNBLOCK_HELPER_PATH" "$PORTSENTRY_SETUP_HELPER_PATH"
    fi
    # Phase 3 (option B) - fixed-unit systemctl enable --now allow-list (argv-exact, no helper).
    if [ "$ENABLE_SERVICE_ENABLE" -eq 1 ]; then
        write_service_enable_sudoers
    else
        rm -f "$SERVICE_ENABLE_SUDOERS_FILE"
    fi
    # S-FEAT-W8KN - fixed-package apt-get install/purge allow-list (argv-exact, no helper).
    if [ "$ENABLE_PACKAGE_MANAGE" -eq 1 ]; then
        write_package_manage_sudoers
    else
        rm -f "$PACKAGE_MANAGE_SUDOERS_FILE"
    fi
    # ADR-024 4.1 - fixed apt-get upgrade allow-list (argv-exact, no helper) for fleet OS patching.
    if [ "$ENABLE_PATCH_MANAGE" -eq 1 ]; then
        write_patch_manage_sudoers
    else
        rm -f "$PATCH_MANAGE_SUDOERS_FILE"
    fi
    # ADR-024 4.2 - root-owned ufw helper + path-only sudoers for firewall control.
    if [ "$ENABLE_FIREWALL_MANAGE" -eq 1 ]; then
        write_firewall_manage
    else
        rm -f "$FIREWALL_MANAGE_SUDOERS_FILE" "$FIREWALL_HELPER_PATH"
    fi
    # S-FEAT-W8KN - mail setup: root-owned helper + NOPASSWD sudoers for that one binary.
    if [ "$ENABLE_MAIL_SETUP" -eq 1 ]; then
        write_mail_setup
    else
        rm -f "$MAIL_MANAGE_SUDOERS_FILE" "$MAIL_SETUP_HELPER_PATH" "$MAIL_MANAGE_HELPER_PATH"
    fi
    # TeamSpeak setup: root-owned helper + NOPASSWD sudoers for that one binary.
    if [ "$ENABLE_TEAMSPEAK_SETUP" -eq 1 ]; then
        write_teamspeak_setup
    else
        rm -f "$TEAMSPEAK_SETUP_SUDOERS_FILE" "$TEAMSPEAK_SETUP_HELPER_PATH"
    fi
    # Deployment module - unit template + root-owned deploy-restart helper + NOPASSWD sudoers + base dir.
    if [ "$ENABLE_DEPLOYMENT" -eq 1 ]; then
        write_deploy_module
    else
        # Teardown leaves $DEPLOY_BASE_DIR in place (it holds deployed apps); only the privileged
        # bits are removed. daemon-reload only when the template actually went away.
        if [ -f "$DEPLOY_UNIT_TEMPLATE_PATH" ]; then
            rm -f "$DEPLOY_MANAGE_SUDOERS_FILE" "$DEPLOY_RESTART_HELPER_PATH" "$DEPLOY_UNIT_TEMPLATE_PATH"
            systemctl daemon-reload 2>/dev/null || true
        else
            rm -f "$DEPLOY_MANAGE_SUDOERS_FILE" "$DEPLOY_RESTART_HELPER_PATH"
        fi
    fi
    grant_sudoers_read_acls
}

# The agent hashes the sudoers drop-ins each heartbeat to self-report its elevation posture (the
# backend derives PackageManagementAvailable / DeploymentTargetAvailable from those hashes - and
# gates the install/uninstall UI on them). Drop-ins are root:root 0440, so the non-root agent cannot
# read them without an explicit grant - give it READ-ONLY POSIX ACL on each aetheus-* drop-in.
# Read only, never write: sudo still owns the files and visudo still accepts them (a read ACL adds no
# group/other WRITE bit, which is all sudo refuses). Without this the capability stays OFF even when
# the grant is present - the file exists but is unreadable to the agent, so no hash is reported.
grant_sudoers_read_acls() {
    # A hash-derived capability is dead-on-arrival without setfacl, so try harder than the best-effort
    # ensure_optional_packages pass (which can race a not-yet-refreshed apt index on a freshly-imaged
    # box): one last-chance install before we give up. install_package refreshes the index itself.
    if ! command -v setfacl >/dev/null 2>&1; then
        log_warn "  setfacl missing, attempting to install 'acl' (needed to self-report capabilities)..."
        install_package acl || true
    fi
    if ! command -v setfacl >/dev/null 2>&1; then
        # Which REQUESTED capabilities the backend derives from a drop-in hash; these are the ones the
        # operator will see as "unavailable" in the UI despite the sudo grant being correctly installed.
        _gsr_blocked=""
        [ "$ENABLE_PACKAGE_MANAGE" -eq 1 ] && _gsr_blocked="$_gsr_blocked package-manage"
        [ "$ENABLE_PATCH_MANAGE" -eq 1 ] && _gsr_blocked="$_gsr_blocked patch-manage"
        [ "$ENABLE_FIREWALL_MANAGE" -eq 1 ] && _gsr_blocked="$_gsr_blocked firewall-manage"
        [ "$ENABLE_DEPLOYMENT" -eq 1 ]     && _gsr_blocked="$_gsr_blocked deployment"
        if [ -n "$_gsr_blocked" ]; then
            ACL_GRANT_DEGRADED=1
            log_warn "  ============================================================"
            log_warn "  WARNING: 'acl' (setfacl) is unavailable and could not be installed."
            log_warn "  The sudo grant IS installed, but the non-root agent cannot read the"
            log_warn "  /etc/sudoers.d/aetheus-* drop-ins to hash them, so these REQUESTED"
            log_warn "  capabilities will report UNAVAILABLE in the UI:"
            log_warn "    ${_gsr_blocked# }"
            log_warn "  Fix: install acl, then re-run this installer (idempotent):"
            log_warn "    apt-get update && apt-get install -y acl"
            log_warn "  ============================================================"
        else
            log_warn "  setfacl not available; sudoers drop-ins won't be hashable. No hash-derived"
            log_warn "  capability was requested, so this is non-blocking."
        fi
        return 0
    fi
    _gsr_any=0
    for _gsr_file in /etc/sudoers.d/aetheus-*; do
        [ -f "$_gsr_file" ] || continue
        if setfacl -m "u:$AGENT_USER:r" "$_gsr_file" 2>/dev/null; then
            _gsr_any=1
        else
            log_warn "  setfacl read grant failed on $_gsr_file (non-fatal)"
        fi
    done
    if [ "$_gsr_any" -eq 1 ]; then
        log_info "  Granted $AGENT_USER read access to sudoers drop-ins (capability self-report)."
    fi
}

# Major version of the running systemd ("systemd 245 (245.4-4ubuntu3)" gives 245); 0 when it cannot be
# read, so a caller testing for a minimum treats an unknown version as too old.
systemd_major_version() {
    _sd_version="$(systemctl --version 2>/dev/null | awk 'NR == 1 { print $2; exit }')"
    case "$_sd_version" in
        ""|*[!0-9]*) echo 0 ;;
        *) echo "$_sd_version" ;;
    esac
}

# Create the root-owned helper directory (root:root, 0755 - agent can traverse + exec but NOT
# write, so it cannot tamper with the helpers it is allowed to sudo).
ensure_helper_dir() {
    mkdir -p "$AETHEUS_HELPER_DIR"
    chown root:root "$AETHEUS_HELPER_DIR" 2>/dev/null || true
    chmod 755 "$AETHEUS_HELPER_DIR" 2>/dev/null || true
}

# Phase 3 - cron management. Deploys the root-owned cron-apply helper (the security boundary: it
# re-validates every argument and can ONLY write /etc/cron.d/aetheus-<id>) and a NOPASSWD sudoers
# grant for exactly that binary. noexec is intentionally NOT set on this Cmnd_Alias: the helper is a
# vetted shell script that must exec coreutils (chmod/chown) - it is itself the boundary, not a
# general GTFOBins tool. See docs/contracts/security.md#controlled-sudo-escalation.
write_cron_manage() {
    log_info "Configuring cron-manage helper + sudoers (Phase 3, root-owned helper boundary)..."
    ensure_helper_dir
    render_host_config cron/cron-apply "$CRON_HELPER_PATH"
    chown root:root "$CRON_HELPER_PATH"
    chmod 755 "$CRON_HELPER_PATH"

    render_host_config cron/sudoers.d/aetheus-cron "$CRON_MANAGE_SUDOERS_FILE"
    chmod 440 "$CRON_MANAGE_SUDOERS_FILE"
    visudo -cf "$CRON_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid cron-manage sudoers syntax"
        rm -f "$CRON_MANAGE_SUDOERS_FILE"
        exit 1
    }
}

# Deployment module - makes this host a cross-agent deploy target. Three pieces, all posed at install
# time so nothing is ever generated from a pipeline YAML (anti-injection):
#   1. A systemd unit TEMPLATE (aetheus-app@.service). The instance name %i is the validated <app>;
#      ExecStart points at the agent-owned $DEPLOY_BASE_DIR/<app>/current/run script SHIPPED INSIDE THE
#      ARTIFACT - the "how to start" never travels in the task, only the app name does.
#   2. A root-owned deploy-restart helper (the security boundary: re-validates <app>, can ONLY restart
#      aetheus-app@<app>.service) + a NOPASSWD sudoers grant for exactly that binary. noexec is
#      omitted (the helper is a vetted script that must exec systemctl) - same recipe as cron-apply.
#   3. The agent-owned $DEPLOY_BASE_DIR tree (it writes releases/ and atomically flips current).
# See docs/contracts/security.md#controlled-sudo-escalation.
# S-FEAT-DPU2 (v2 isolation - IMPLEMENTED): the unit template below runs each deployed app under a
# transient per-app systemd identity (DynamicUser) with a StateDirectory for its writable state,
# instead of the shared agent user. The ownership split that makes this work: $DEPLOY_BASE_DIR/%i
# stays AGENT-managed (the agent writes releases/ and flips 'current' as $AGENT_USER) but its group is
# the dedicated read-only $DEPLOY_READ_GROUP (setgid base dir, mode 0750 on each release - GROUP-readable,
# never world: the payload carries cleartext substituted secrets). The unit grants the app that group via
# SupplementaryGroups, so the transient uid reads the payload without joining the agent's group. The app's
# writable HOME moves off the deploy tree onto /var/lib/aetheus-app-%i (systemd StateDirectory,
# auto-owned by the transient uid, persisted across redeploys).
# Residual (documented limitation): apps sharing $DEPLOY_READ_GROUP can read each other's release trees -
# closing that fully needs per-app groups (in tension with the anti-injection no-useradd-at-deploy model).
# NOTE: smoke-test on a real systemd box (DataProtection-key persistence + release-read under the
# transient uid) when first enabling the module on a host - the VPS-sim is the place to validate it.
write_deploy_module() {
    log_info "Configuring deployment module: unit template + deploy-restart helper + sudoers + base dir..."
    ensure_helper_dir

    render_host_config deploy/aetheus-app@.service "$DEPLOY_UNIT_TEMPLATE_PATH"
    chmod 644 "$DEPLOY_UNIT_TEMPLATE_PATH"

    render_host_config deploy/deploy-restart "$DEPLOY_RESTART_HELPER_PATH"
    chown root:root "$DEPLOY_RESTART_HELPER_PATH"
    chmod 755 "$DEPLOY_RESTART_HELPER_PATH"

    render_host_config deploy/sudoers.d/aetheus-deploy "$DEPLOY_MANAGE_SUDOERS_FILE"
    chmod 440 "$DEPLOY_MANAGE_SUDOERS_FILE"
    visudo -cf "$DEPLOY_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid deploy sudoers syntax"
        rm -f "$DEPLOY_MANAGE_SUDOERS_FILE"
        exit 1
    }

    # S-FEAT-DPU2: dedicated read-only group for deployed apps to read their payload via SupplementaryGroups.
    getent group "$DEPLOY_READ_GROUP" >/dev/null 2>&1 || groupadd --system "$DEPLOY_READ_GROUP" 2>/dev/null || true

    # Agent-owned base dir: the agent writes releases/ and flips the 'current' symlink here (no sudo).
    # Group = the deploy-read group with setgid (2750) so every release dir/file the agent extracts under
    # it inherits that group - the transient DynamicUser then reads the payload via its group membership,
    # owner-agent keeps write, and other host users get nothing (o---).
    mkdir -p "$DEPLOY_BASE_DIR"
    chown "$AGENT_USER:$DEPLOY_READ_GROUP" "$DEPLOY_BASE_DIR" 2>/dev/null || true
    chmod 2750 "$DEPLOY_BASE_DIR" 2>/dev/null || true

    # $WORK_DIR is also the agent's systemd StateDirectory. It stays private (no read/list access),
    # but the deployed app's DynamicUser must be able to traverse this one parent component before
    # reaching the group-readable deploy tree. A named ACL grants exactly execute to the dedicated
    # deploy group; StateDirectoryMode=0710 below preserves the ACL mask across agent restarts.
    # Never widen this to world execute: the parent also contains credentials, keys and backups.
    if ! command -v setfacl >/dev/null 2>&1; then
        log_error "Deployment module requires 'setfacl' to grant safe traverse-only access on $WORK_DIR."
        exit 1
    fi
    setfacl -m "g:$DEPLOY_READ_GROUP:--x" "$WORK_DIR"

    # Static web root: make it agent-writable via ACL so a pipeline can publish a static site
    # (e.g. the vitrine) there without sudo, while Apache (www-data) still reads it. Recursive +
    # default ACL so files created later inherit the grant. Requires 'acl' (setfacl); the installer
    # already depends on it for other hash-derived capabilities.
    if [ -n "$DEPLOY_WEB_ROOT" ]; then
        mkdir -p "$DEPLOY_WEB_ROOT"
        if command -v setfacl >/dev/null 2>&1; then
            setfacl -R  -m "u:$AGENT_USER:rwx" "$DEPLOY_WEB_ROOT" 2>/dev/null || true
            setfacl -R -d -m "u:$AGENT_USER:rwx" "$DEPLOY_WEB_ROOT" 2>/dev/null || true
            log_info "Web root $DEPLOY_WEB_ROOT is agent-writable (ACL) for static-site publishing."
        else
            log_warn "setfacl missing: cannot grant agent write on $DEPLOY_WEB_ROOT (install 'acl' and re-run)."
        fi
    fi

    systemctl daemon-reload 2>/dev/null || true
}

# Phase 3 - portsentry IP unblock. Deploys the root-owned unblock-ip helper (validates the IP, then
# removes it from iptables/ip6tables + /etc/hosts.deny) and a NOPASSWD sudoers grant for exactly that
# binary. Keeps /etc/hosts.deny (a 'spawn'-capable root-RCE file) out of any standing agent ACL.
write_portsentry_manage() {
    log_info "Configuring portsentry-unblock helper + sudoers (Phase 3, root-owned helper boundary)..."
    ensure_helper_dir
    render_host_config portsentry/unblock-ip "$UNBLOCK_HELPER_PATH"
    chown root:root "$UNBLOCK_HELPER_PATH"
    chmod 755 "$UNBLOCK_HELPER_PATH"

    # Root-owned portsentry-setup helper: apt-installs portsentry and writes the scan mode + TCP/UDP port
    # lists into the config, then enables the service. Re-validates every positional arg (the agent already
    # validated, but the helper is the boundary) and never evals input. Replaces the old back-side sed/apt
    # shell chain the agent CommandValidator rejected.
    render_host_config portsentry/portsentry-setup "$PORTSENTRY_SETUP_HELPER_PATH"
    chown root:root "$PORTSENTRY_SETUP_HELPER_PATH"
    chmod 755 "$PORTSENTRY_SETUP_HELPER_PATH"

    render_host_config portsentry/sudoers.d/aetheus-portsentry "$PORTSENTRY_MANAGE_SUDOERS_FILE"
    chmod 440 "$PORTSENTRY_MANAGE_SUDOERS_FILE"
    visudo -cf "$PORTSENTRY_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid portsentry-manage sudoers syntax"
        rm -f "$PORTSENTRY_MANAGE_SUDOERS_FILE"
        exit 1
    }
}

# Phase 3 (option B) - systemctl enable --now on a FIXED list of pre-existing units. `systemctl
# enable` is otherwise forbidden (mints new root services); permitted here ONLY argv-exact, no
# wildcards, against units that already exist. Mirrors the AETHEUS_SYSTEMCTL unit set.
# See docs/contracts/security.md#controlled-sudo-escalation.
write_service_enable_sudoers() {
    log_info "Configuring service-enable sudoers (Phase 3 option B, fixed-unit allow-list)..."
    render_host_config service-enable/sudoers.d/aetheus-service-enable "$SERVICE_ENABLE_SUDOERS_FILE"
    chmod 440 "$SERVICE_ENABLE_SUDOERS_FILE"
    visudo -cf "$SERVICE_ENABLE_SUDOERS_FILE" || {
        log_error "Invalid service-enable sudoers syntax"
        rm -f "$SERVICE_ENABLE_SUDOERS_FILE"
        exit 1
    }
}

# S-FEAT-W8KN - install/uninstall a managed OS package via apt-get. Each entry is an EXACT argv for a
# package in the closed allow-list (kept in lockstep with Aetheus.Shared.Constants.ManageablePackages
# by SudoersAuditTests). Names are the real apt package names (docker.io / mysql-server
# / mariadb-server / mongodb-org), NOT the service keys. `apt-get update` is allow-listed so a fresh box
# with an empty index can refresh before install. INSTALL covers the full list; PURGE is restricted to
# the Removable subset (protected packages - databases, ufw/fail2ban, docker - cannot be uninstalled). No
# wildcards: anything outside the list is refused by sudo at the OS level. noexec is intentionally NOT set
# - apt-get must exec dpkg + maintainer scripts; the argv-exact allow-list (no -o / config-file flags) is
# the boundary. See docs/contracts/security.md#controlled-sudo-escalation.
write_package_manage_sudoers() {
    log_info "Configuring package-manage sudoers (S-FEAT-W8KN, fixed-package allow-list)..."
    render_host_config package/sudoers.d/aetheus-package "$PACKAGE_MANAGE_SUDOERS_FILE"
    chmod 440 "$PACKAGE_MANAGE_SUDOERS_FILE"
    visudo -cf "$PACKAGE_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid package-manage sudoers syntax"
        rm -f "$PACKAGE_MANAGE_SUDOERS_FILE"
        exit 1
    }
}

write_patch_manage_sudoers() {
    log_info "Configuring patch-manage sudoers (ADR-024 4.1, apt-get upgrade, argv-exact)..."
    render_host_config patch/sudoers.d/aetheus-patch "$PATCH_MANAGE_SUDOERS_FILE"
    chmod 440 "$PATCH_MANAGE_SUDOERS_FILE"
    visudo -cf "$PATCH_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid patch-manage sudoers syntax"
        rm -f "$PATCH_MANAGE_SUDOERS_FILE"
        exit 1
    }
}

write_firewall_manage() {
    log_info "Configuring firewall-manage helper + sudoers (ADR-024 4.2, ufw)..."
    ensure_helper_dir
    # Root-owned helper = the security boundary: it re-validates every argument, re-enforces anti-lockout
    # with the REAL SSH port, and never evals. The template declares no placeholder => nothing is expanded inside.
    render_host_config firewall/aetheus-firewall "$FIREWALL_HELPER_PATH"
    chown root:root "$FIREWALL_HELPER_PATH"
    chmod 755 "$FIREWALL_HELPER_PATH"

    render_host_config firewall/sudoers.d/aetheus-firewall "$FIREWALL_MANAGE_SUDOERS_FILE"
    chmod 440 "$FIREWALL_MANAGE_SUDOERS_FILE"
    visudo -cf "$FIREWALL_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid firewall-manage sudoers syntax"
        rm -f "$FIREWALL_MANAGE_SUDOERS_FILE"
        exit 1
    }
}

# S-FEAT-W8KN - mail setup. Deploys the root-owned mail-setup helper (the security boundary: it
# re-validates every argument, reads the admin password from stdin, and performs the full
# postfix/dovecot/opendkim install+configure for ONE domain) and a NOPASSWD sudoers grant for exactly
# that binary. The old MailService.SetupAsync enqueued a free-form apt/postconf/systemctl shell pipeline
# that always failed for the non-root agent; this is the typed OperationKind.MailSetup path. noexec is
# intentionally NOT set on this Cmnd_Alias: the helper is a vetted script that must exec apt-get /
# postconf / systemctl / doveadm - it is itself the boundary. See
# docs/contracts/security.md#controlled-sudo-escalation.
write_mail_setup() {
    log_info "Configuring mail-setup helper + sudoers (S-FEAT-W8KN, root-owned helper boundary)..."
    ensure_helper_dir
    render_host_config mail/mail-setup "$MAIL_SETUP_HELPER_PATH"
    chown root:root "$MAIL_SETUP_HELPER_PATH"
    chmod 755 "$MAIL_SETUP_HELPER_PATH"

    # Keep mail operations behind the root-owned helper with argument validation and passwords on
    # stdin; share the certbot ACME web root so mail certificates use the same renewal path. See
    # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
    render_host_config mail/mail-manage "$MAIL_MANAGE_HELPER_PATH"
    chown root:root "$MAIL_MANAGE_HELPER_PATH"
    chmod 755 "$MAIL_MANAGE_HELPER_PATH"

    render_host_config mail/sudoers.d/aetheus-mail "$MAIL_MANAGE_SUDOERS_FILE"
    chmod 440 "$MAIL_MANAGE_SUDOERS_FILE"
    visudo -cf "$MAIL_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid mail-setup sudoers syntax"
        rm -f "$MAIL_MANAGE_SUDOERS_FILE"
        exit 1
    }
}

# TeamSpeak setup. Deploys the root-owned teamspeak-setup helper (the security boundary: it re-validates
# every argument and performs the full TS3 server install - system user, tarball download/extract,
# license, systemd unit, ports, and serveradmin-credential extraction for the unprivileged agent) and a
# NOPASSWD sudoers grant for exactly that binary. The old TeamspeakService.SetupAsync enqueued a free-form
# useradd/cat>/etc/systemd/systemctl shell pipeline that always failed for the non-root agent; this is the
# typed OperationKind.TeamspeakSetup path. noexec is intentionally NOT set: the helper must exec apt-get /
# curl / tar / systemctl / useradd - it is itself the boundary. See
# docs/contracts/security.md#controlled-sudo-escalation.
write_teamspeak_setup() {
    log_info "Configuring teamspeak-setup helper + sudoers (root-owned helper boundary)..."
    ensure_helper_dir
    render_host_config teamspeak/teamspeak-setup "$TEAMSPEAK_SETUP_HELPER_PATH"
    chown root:root "$TEAMSPEAK_SETUP_HELPER_PATH"
    chmod 755 "$TEAMSPEAK_SETUP_HELPER_PATH"

    render_host_config teamspeak/sudoers.d/aetheus-teamspeak "$TEAMSPEAK_SETUP_SUDOERS_FILE"
    chmod 440 "$TEAMSPEAK_SETUP_SUDOERS_FILE"
    visudo -cf "$TEAMSPEAK_SETUP_SUDOERS_FILE" || {
        log_error "Invalid teamspeak-setup sudoers syntax"
        rm -f "$TEAMSPEAK_SETUP_SUDOERS_FILE"
        exit 1
    }
}

# Items #10.2/#10.3 - root-owned helper (ADR-019) + argv-exact allow-list + Defaults
# timestamp_timeout=0 / env_reset / secure_path. Three commands strictly: check, update,
# propupd. No rkhunter flag reaches sudo; the helper runs the fixed rkhunter argv. noexec is
# omitted because rkhunter, a shell script, must exec grep/tr/cut (recette R3-36).
write_rkhunter_manage_sudoers() {
    log_info "Configuring RKHunter-manage helper + sudoers (items #10.2/#10.3, ultra-strict allow-list)..."
    ensure_helper_dir
    render_host_config rkhunter/aetheus-rkhunter "$RKHUNTER_HELPER_PATH"
    chown root:root "$RKHUNTER_HELPER_PATH"
    chmod 755 "$RKHUNTER_HELPER_PATH"
    # rkhunter writes its log 0600 root; the helper re-grants read after each run, this covers the log
    # already on disk (left by the package's own daily run) so the collector reads it from now on.
    if [ -f /var/log/rkhunter.log ]; then
        setfacl -m "u:$AGENT_USER:r" /var/log/rkhunter.log 2>/dev/null \
            || log_warn "  Could not grant $AGENT_USER read access to /var/log/rkhunter.log"
    fi
    render_host_config rkhunter/sudoers.d/aetheus-rkhunter "$RKHUNTER_MANAGE_SUDOERS_FILE"
    chmod 440 "$RKHUNTER_MANAGE_SUDOERS_FILE"
    visudo -cf "$RKHUNTER_MANAGE_SUDOERS_FILE" || {
        log_error "Invalid RKHunter-manage sudoers syntax"
        rm -f "$RKHUNTER_MANAGE_SUDOERS_FILE"
        exit 1
    }
}

print_posture_summary() {
    log_info ""
    [ "$MODULE_PIPELINE_RUNNER" -eq 1 ] \
        && log_info "  Module pipeline-runner: ON (git/sshpass installed; SDKs use locked OCI images)" \
        || log_info "  Module pipeline-runner: off"
    log_info "  Elevation posture (server-management; all OFF by default - opt in with --module server-management or --enable-*):"
    if [ "$ENABLE_SERVICE_CONTROL" -eq 1 ]; then
        log_info "    service-control : ON  (sudoers rule installed; NoNewPrivileges=false)"
    elif sudo_grant_enabled; then
        log_info "    service-control : off (no service-control sudoers; another grant keeps NoNewPrivileges=false)"
    else
        log_info "    service-control : off (no sudoers; NoNewPrivileges=true, full sandbox)"
    fi
    [ "$ENABLE_APACHE"  -eq 1 ] && log_info "    apache read ACL : ON" || log_info "    apache read ACL : off"
    if [ "$ENABLE_APACHE_MANAGE" -eq 1 ]; then
        log_info "    apache manage   : ON  (fixed configtest helper + argv-exact systemctl; ACL +rw on sites-available)"
    else
        log_info "    apache manage   : off (no Apache write/reload privileges)"
    fi
    if [ "$ENABLE_RKHUNTER_MANAGE" -eq 1 ]; then
        log_info "    rkhunter manage : ON  (sudoers ${RKHUNTER_MANAGE_SUDOERS_FILE}; root helper ${RKHUNTER_HELPER_PATH}, argv-exact)"
    else
        log_info "    rkhunter manage : off (cannot run rkhunter scan/update/propupd)"
    fi
    [ "$ENABLE_CERTBOT" -eq 1 ] && log_info "    certbot read ACL: ON" || log_info "    certbot read ACL: off"
    if [ "$ENABLE_DOCKER" -eq 1 ]; then
        log_info "    docker group    : ON"
    elif id -nG "$AGENT_USER" 2>/dev/null | tr ' ' '\n' | grep -qx docker; then
        # Said as it is: off was asked, but the account is still a member (not the dedicated account,
        # or gpasswd failed), so the agent still has root-equivalent access to the Docker socket.
        log_warn "    docker group    : off requested, but $AGENT_USER is STILL a member of the docker group"
    else
        log_info "    docker group    : off"
    fi
    [ "$ENABLE_TEAMSPEAK" -eq 1 ] && log_info "    teamspeak access: ON" || log_info "    teamspeak access: off"
    if [ "$ENABLE_CRON_MANAGE" -eq 1 ]; then
        log_info "    cron manage     : ON  (root helper ${CRON_HELPER_PATH}; sudoers ${CRON_MANAGE_SUDOERS_FILE})"
    else
        log_info "    cron manage     : off (cannot write cron jobs)"
    fi
    if [ "$ENABLE_PORTSENTRY_MANAGE" -eq 1 ]; then
        log_info "    portsentry mgmt : ON  (root helpers ${UNBLOCK_HELPER_PATH} + ${PORTSENTRY_SETUP_HELPER_PATH}; sudoers ${PORTSENTRY_MANAGE_SUDOERS_FILE})"
    else
        log_info "    portsentry mgmt : off (cannot unblock IPs or install/configure portsentry)"
    fi
    if [ "$ENABLE_SERVICE_ENABLE" -eq 1 ]; then
        log_info "    service enable  : ON  (sudoers ${SERVICE_ENABLE_SUDOERS_FILE}; fixed-unit, argv-exact, noexec)"
    else
        log_info "    service enable  : off (config-deploy cannot enable units on boot)"
    fi
    if [ "$ENABLE_PACKAGE_MANAGE" -eq 1 ]; then
        log_info "    package manage  : ON  (sudoers ${PACKAGE_MANAGE_SUDOERS_FILE}; fixed-package, argv-exact)"
    else
        log_info "    package manage  : off (cannot install/uninstall managed packages)"
    fi
    if [ "$ENABLE_PATCH_MANAGE" -eq 1 ]; then
        log_info "    patch manage    : ON  (sudoers ${PATCH_MANAGE_SUDOERS_FILE}; apt-get upgrade, argv-exact)"
    else
        log_info "    patch manage    : off (can see pending updates; cannot apply them)"
    fi
    if [ "$ENABLE_FIREWALL_MANAGE" -eq 1 ]; then
        log_info "    firewall manage : ON  (helper ${FIREWALL_HELPER_PATH}; sudoers ${FIREWALL_MANAGE_SUDOERS_FILE})"
    else
        log_info "    firewall manage : off (cannot view/control ufw rules)"
    fi
    if [ "$ENABLE_MAIL_SETUP" -eq 1 ]; then
        log_info "    mail setup      : ON  (root helper ${MAIL_SETUP_HELPER_PATH}; sudoers ${MAIL_MANAGE_SUDOERS_FILE})"
    else
        log_info "    mail setup      : off (Mail section 'Setup' cannot run)"
    fi
    if [ "$ENABLE_TEAMSPEAK_SETUP" -eq 1 ]; then
        log_info "    teamspeak setup : ON  (root helper ${TEAMSPEAK_SETUP_HELPER_PATH}; sudoers ${TEAMSPEAK_SETUP_SUDOERS_FILE})"
    else
        log_info "    teamspeak setup : off (TeamSpeak 'Install' cannot run)"
    fi
    if [ "$ENABLE_DEPLOYMENT" -eq 1 ]; then
        log_info "    deployment      : ON  (unit template ${DEPLOY_UNIT_TEMPLATE_PATH}; root helper ${DEPLOY_RESTART_HELPER_PATH}; sudoers ${DEPLOY_MANAGE_SUDOERS_FILE})"
    else
        log_info "    deployment      : off (cannot be a cross-agent deploy target)"
    fi

    # Loud, unmissable re-statement of the acl/setfacl degradation: the per-capability lines above
    # print "ON" (the grant is genuinely installed) but without setfacl the agent can't hash the
    # drop-in, so the UI will still report them OFF. Repeat it here at the very end of the run so a
    # `-y` operator scrolling to the success banner sees it.
    if [ "$ACL_GRANT_DEGRADED" -eq 1 ]; then
        log_warn ""
        log_warn "  NOTE: capabilities above marked ON depend on the 'acl' package, which is missing."
        log_warn "        Until you 'apt-get install -y acl' and re-run this installer, the agent"
        log_warn "        cannot self-report them and they stay UNAVAILABLE in the dashboard."
    fi

    # Tip: hint the user they can grant more capabilities later
    if [ "$ENABLE_SERVICE_CONTROL" -eq 0 ] && [ "$ENABLE_APACHE" -eq 0 ] && \
       [ "$ENABLE_APACHE_MANAGE" -eq 0 ] && [ "$ENABLE_RKHUNTER_MANAGE" -eq 0 ] && \
       [ "$ENABLE_CERTBOT" -eq 0 ] && [ "$ENABLE_DOCKER" -eq 0 ] && [ "$ENABLE_TEAMSPEAK" -eq 0 ] && \
       [ "$ENABLE_CRON_MANAGE" -eq 0 ] && [ "$ENABLE_PORTSENTRY_MANAGE" -eq 0 ] && \
       [ "$ENABLE_SERVICE_ENABLE" -eq 0 ] && [ "$ENABLE_PACKAGE_MANAGE" -eq 0 ] && [ "$ENABLE_PATCH_MANAGE" -eq 0 ] && [ "$ENABLE_FIREWALL_MANAGE" -eq 0 ] && \
       [ "$ENABLE_MAIL_SETUP" -eq 0 ] && [ "$ENABLE_TEAMSPEAK_SETUP" -eq 0 ] && [ "$ENABLE_DEPLOYMENT" -eq 0 ]; then
        log_info ""
        log_info "  Tip: re-run with --upgrade --enable-<flag> to grant more capabilities."
        log_info "       Use $0 -h for the full flag list."
    fi
}

# Whether any capability that installs a sudoers grant (apply_sudoers) is on. The sandbox of the agent
# unit is derived from it, so a grant can never be installed and then blocked by NoNewPrivileges.
sudo_grant_enabled() {
    for _grant in "$ENABLE_SERVICE_CONTROL" "$ENABLE_APACHE_MANAGE" "$ENABLE_CERTBOT_MANAGE" \
        "$ENABLE_RKHUNTER_MANAGE" "$ENABLE_CRON_MANAGE" "$ENABLE_PORTSENTRY_MANAGE" \
        "$ENABLE_SERVICE_ENABLE" "$ENABLE_PACKAGE_MANAGE" "$ENABLE_PATCH_MANAGE" \
        "$ENABLE_FIREWALL_MANAGE" "$ENABLE_MAIL_SETUP" "$ENABLE_TEAMSPEAK_SETUP" "$ENABLE_DEPLOYMENT"; do
        [ "$_grant" -eq 1 ] && return 0
    done
    return 1
}

write_systemd_unit() {
    log_info "Writing systemd unit: $SYSTEMD_UNIT_PATH"
    EXEC_START="$INSTALL_DIR/Aetheus.Agent.Linux"
    if [ ! -f "$EXEC_START" ]; then
        EXEC_START="/usr/bin/dotnet $INSTALL_DIR/Aetheus.Agent.Linux.dll"
    fi

    # Enable NoNewPrivileges only when no sudo grant is active because it blocks all sudo and setuid
    # escalation; retain the remaining sandbox when an explicit grant requires elevation. See
    # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
    if sudo_grant_enabled; then
        NNP_BLOCK="# RELAXED: a sudo grant (any --enable-* capability that installs a sudoers drop-in, or
# --module deployment) requires NoNewPrivileges=false (sudo needs setgid(0) for 'systemctl reload
# apache2', the certbot helper, etc.). This is a deliberate trade-off (sudo escalation surface) -
# disable those capabilities to restore NoNewPrivileges=true.
NoNewPrivileges=false"
        # A sudo grant is dead on arrival if the elevated (root) process is left with no
        # capabilities: the setuid sudo binary execs as uid 0 but, with an empty bounding set, can't
        # even setgid(0) ('sudo: unable to change to root gid') let alone let apt/systemctl do their
        # work. So when a grant is in play, leave CapabilityBoundingSet at its default (full set) and
        # don't block setuid execution. The remaining sandbox (syscall filter, kernel protections,
        # and ProtectSystem where it still applies) stays; the argv-exact sudoers allow-list is the
        # real boundary.
        SANDBOX_PRIV_BLOCK="RestrictSUIDSGID=false
# CapabilityBoundingSet intentionally left at the default (full set) so the sudo grant can elevate."
    else
        # Set NoNewPrivileges in the sandboxed branch so the rendered service unit retains its
        # zero-elevation boundary. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
        NNP_BLOCK="# Fully sandboxed: no sudo grant, so privilege escalation is blocked outright.
NoNewPrivileges=true"
        # Fully sandboxed collector: no sudo grant, so strip every capability and block setuid exec.
        SANDBOX_PRIV_BLOCK="RestrictSUIDSGID=true
# CapabilityBoundingSet emptied - a non-root collector needs no capabilities.
CapabilityBoundingSet="
    fi

    # Allow broad writes only for helpers that install or manage system software; keep ProtectSystem
    # strict with narrow writable paths for Apache, certbot, cron and rkhunter. See
    # docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
    READ_WRITE_PATHS_BLOCK=""
    if [ "$ENABLE_PACKAGE_MANAGE" -eq 1 ] || [ "$ENABLE_PATCH_MANAGE" -eq 1 ] \
       || [ "$ENABLE_FIREWALL_MANAGE" -eq 1 ] || [ "$ENABLE_MAIL_SETUP" -eq 1 ] \
       || [ "$ENABLE_DEPLOYMENT" -eq 1 ] || [ "$ENABLE_PORTSENTRY_MANAGE" -eq 1 ] \
       || [ "$ENABLE_TEAMSPEAK_SETUP" -eq 1 ]; then
        PROTECT_SYSTEM_BLOCK="# RELAXED: package/mail/deploy/portsentry/TeamSpeak grants write broad system paths, so the read-only system mount is off.
ProtectSystem=false"
    else
        PROTECT_SYSTEM_BLOCK="ProtectSystem=strict"
        if [ "$ENABLE_APACHE_MANAGE" -eq 1 ]; then
            mkdir -p /etc/apache2
            READ_WRITE_PATHS_BLOCK="$READ_WRITE_PATHS_BLOCK /etc/apache2"
        fi
        if [ "$ENABLE_CRON_MANAGE" -eq 1 ]; then
            # cron-apply writes /etc/cron.d/aetheus-<id> from inside this unit's mount namespace.
            mkdir -p /etc/cron.d
            READ_WRITE_PATHS_BLOCK="$READ_WRITE_PATHS_BLOCK /etc/cron.d"
        fi
        if [ "$ENABLE_RKHUNTER_MANAGE" -eq 1 ]; then
            # rkhunter --update/--propupd write its database, and every run rewrites its log. The '-'
            # lets the unit start before rkhunter is installed.
            READ_WRITE_PATHS_BLOCK="$READ_WRITE_PATHS_BLOCK -/var/lib/rkhunter -/var/log"
        fi
        if [ "$ENABLE_CERTBOT_MANAGE" -eq 1 ]; then
            # Allow the unit's helpers to write challenge files and renewal-check evidence inside
            # its mount namespace. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
            _rw_acme_webroot="${ACME_WEBROOT_OVERRIDE:-/var/www/aetheus-acme}"
            mkdir -p /etc/letsencrypt /var/lib/letsencrypt /var/log/letsencrypt "$_rw_acme_webroot" "$CERTBOT_STATE_DIR"
            READ_WRITE_PATHS_BLOCK="$READ_WRITE_PATHS_BLOCK /etc/letsencrypt /var/lib/letsencrypt /var/log/letsencrypt $_rw_acme_webroot $CERTBOT_STATE_DIR"
        fi
    fi

    # Home directories stay out of reach. The certbot helpers are the one exception: snap-packaged
    # certbot refuses to start unless it can create /root/snap/certbot/<revision> ("cannot create user
    # data directory ... Permission denied"), so with --enable-certbot-manage the homes are an empty
    # tmpfs and only /root/snap is bound in. Without it, every new or renewed certificate failed under
    # this unit (aetheus-nightly 2503, qa.app and qa.api, 2026-10-02).
    #
    # An agent self-update runs this script from aetheus-agent-upgrade.service, which hides the homes
    # itself (ProtectHome=true): /root is read-only there, and a plain mkdir aborted the upgrade, which
    # then rolled back to the old agent (1.2.398 -> 1.2.402, 2026-10-03). The directory is created when
    # the homes are reachable; the leading '-' lets the unit start without it, certbot then failing as
    # it did before rather than the whole agent.
    PROTECT_HOME_BLOCK="ProtectHome=true"
    #
    # ProtectHome=tmpfs exists from systemd 242 only (see the SSH binds below): an older systemd, or one
    # whose version cannot be read, ignores the value and leaves the homes readable. There the unit
    # keeps ProtectHome=true and snap certbot stays unable to create its user data directory.
    if [ "$ENABLE_CERTBOT_MANAGE" -eq 1 ]; then
        if [ "$(systemd_major_version)" -ge 242 ]; then
            if ! mkdir -p /root/snap 2>/dev/null; then
                echo "WARNING: /root/snap cannot be created here (homes hidden); snap certbot needs it, run the installer outside the upgrade worker to create it." >&2
            fi
            PROTECT_HOME_BLOCK="ProtectHome=tmpfs
BindPaths=-/root/snap"
        else
            log_warn "systemd older than 242 (or its version is unreadable): the homes stay hidden with ProtectHome=true and snap certbot cannot create its user data directory under /root/snap."
        fi
    fi

    # Read-only host observation (docs/contracts/security.md, "Read-only host observation rights"):
    # fresh install and --upgrade both end here, so both end with the same rights.
    #  1. The root-owned helper, run by its own oneshot unit before every start of the agent: read ACLs
    #     on the SSH configuration.
    #  2. The unit joins systemd-journal, whose members may read the journal and nothing else: the kernel
    #     messages that name an OOM victim come from it, ProtectKernelLogs=true staying on.
    #  3. The .ssh directory of each login user is bound read-only into the otherwise empty homes. The
    #     list comes from /etc/passwd, which stays readable when this script runs from the upgrade
    #     worker (ProtectHome=true): a '-' skips a directory that does not exist.
    ensure_helper_dir
    render_host_config agent/aetheus-agent-read-grant "$AGENT_READ_GRANT_HELPER_PATH"
    chown root:root "$AGENT_READ_GRANT_HELPER_PATH"
    chmod 755 "$AGENT_READ_GRANT_HELPER_PATH"
    render_host_config agent/aetheus-agent-read-grant.service "$AGENT_READ_GRANT_SERVICE_PATH"
    chown root:root "$AGENT_READ_GRANT_SERVICE_PATH"
    chmod 644 "$AGENT_READ_GRANT_SERVICE_PATH"

    JOURNAL_GROUP_BLOCK="# systemd-journal does not exist on this host: the kernel log cannot be read, OOM kills are counted without a process name."
    if getent group systemd-journal >/dev/null 2>&1; then
        JOURNAL_GROUP_BLOCK="# Read-only access to the systemd journal (kernel messages: the process an OOM kill took). A group, not
# capability: ProtectKernelLogs=true stays on and the agent still cannot write the kernel log.
SupplementaryGroups=systemd-journal"
    fi

    #     ProtectHome=tmpfs exists from systemd 242 only: an older systemd (Debian 10, CentOS 7/8, Ubuntu
    #     18.04) does not know the value, ignores the line and leaves the homes readable. Below 242 the
    #     unit keeps ProtectHome=true and binds nothing; the SSH key files then stay "unknown".
    _ssh_bind=""
    if [ "$(systemd_major_version)" -ge 242 ]; then
        for _ssh_dir in $("$AGENT_READ_GRANT_HELPER_PATH" --list-ssh-dirs); do
            _ssh_bind="$_ssh_bind -$_ssh_dir"
        done
    else
        log_warn "systemd older than 242 (or its version is unreadable): the homes stay hidden with ProtectHome=true and the SSH key files cannot be shown to the agent (they stay unknown)."
    fi
    if [ -n "$_ssh_bind" ]; then
        # Opening any path below the homes needs them replaced by an empty tmpfs; only the listed
        # directories are bound in, read-only.
        if [ "$PROTECT_HOME_BLOCK" = "ProtectHome=true" ]; then
            PROTECT_HOME_BLOCK="ProtectHome=tmpfs"
        fi
        PROTECT_HOME_BLOCK="$PROTECT_HOME_BLOCK
BindReadOnlyPaths=${_ssh_bind# }"
    fi

    # A deployment target needs an ACL mask with execute on the private state directory so the
    # named aetheus-deploy ACL can traverse to $DEPLOY_BASE_DIR. Without deployment, keep 0700.
    STATE_DIRECTORY_MODE="0700"
    if [ "$ENABLE_DEPLOYMENT" -eq 1 ]; then
        STATE_DIRECTORY_MODE="0710"
    fi

    ADDRESS_FAMILIES="AF_UNIX AF_INET AF_INET6"
    if [ "$ENABLE_MAIL_SETUP" -eq 1 ] || [ "$ENABLE_PORTSENTRY_MANAGE" -eq 1 ]; then
        # Postfix postqueue uses getifaddrs(), which needs a netlink socket under this unit's sandbox,
        # and so does iptables-nft, through which unblock-ip removes a DROP rule.
        ADDRESS_FAMILIES="$ADDRESS_FAMILIES AF_NETLINK"
    fi

    render_host_config agent/aetheus-agent.service "$SYSTEMD_UNIT_PATH"
}

# Faithful "can the agent itself reach the backend?" probe - runs the agent
# binary in --probe-backend mode (one-shot HTTP GET /health/live using the
# same .NET HTTP stack the running agent will use) so TLS/DNS/proxy issues
# invisible to curl surface NOW instead of inside a systemd start timeout.
# Args: $1 = server URL (logged for context only - the probe reads appsettings).
# Returns 0 on reachable, 1 otherwise. Hard-fails by default (the caller may
# choose to demote to a warning via PROBE_BACKEND_SOFT=1).
run_backend_probe() {
    _pb_url="${1:-}"
    _pb_dll="$INSTALL_DIR/Aetheus.Agent.Linux.dll"
    _pb_bin="$INSTALL_DIR/Aetheus.Agent.Linux"

    log_info "Probing backend with the agent runtime (TLS/DNS faithfulness check)..."

    # Pick the deployment shape: single-file binary if present, else dotnet + dll.
    if [ -f "$_pb_bin" ]; then
        _pb_cmd="$_pb_bin --probe-backend"
    elif [ -f "$_pb_dll" ] && command -v dotnet >/dev/null 2>&1; then
        _pb_cmd="dotnet $_pb_dll --probe-backend"
    else
        log_warn "  No agent binary found at $INSTALL_DIR - skipping runtime probe."
        return 0
    fi

    # Run as the agent user with the install dir as CWD so the probe loads the
    # same appsettings.json the running service will read.
    if id "$AGENT_USER" >/dev/null 2>&1; then
        _pb_output="$(cd "$INSTALL_DIR" && runuser -u "$AGENT_USER" -- sh -c "$_pb_cmd" 2>&1)" || _pb_rc=$?
    else
        _pb_output="$(cd "$INSTALL_DIR" && sh -c "$_pb_cmd" 2>&1)" || _pb_rc=$?
    fi
    _pb_rc="${_pb_rc:-0}"

    # Print the probe output verbatim - it already says what happened.
    [ -n "$_pb_output" ] && printf '  %s\n' "$_pb_output"

    if [ "$_pb_rc" -eq 0 ]; then
        return 0
    fi

    # Distinguish a missing .NET runtime from an actual network failure. The .NET
    # apphost returns exit 150 ("framework not found") and prints "No frameworks
    # were found" / "must install or update .NET" - that is NOT a connectivity
    # problem, so reporting it as one (and pointing at TLS/DNS/proxy) sends the
    # operator down the wrong path. This agent build is framework-dependent; the
    # box needs the .NET runtime, or a self-contained agent build.
    if [ "$_pb_rc" -eq 150 ] || printf '%s' "$_pb_output" | grep -qE 'No frameworks were found|must install or update \.NET'; then
        log_error ".NET runtime missing - the agent could not start (exit $_pb_rc)."
        log_error "  This build is framework-dependent and the .NET 10 runtime is not installed here."
        log_error "  This is NOT a backend connectivity problem (the URL above is irrelevant for now)."
        log_error "  Fix: install the .NET 10 runtime, then re-run this installer:"
        log_error "    curl -fsSL ${DOTNET_INSTALLER_URL} -o /tmp/dotnet-install.sh \\"
        log_error "      && echo '${DOTNET_INSTALLER_SHA256}  /tmp/dotnet-install.sh' | sha256sum -c - \\"
        log_error "      && sudo bash /tmp/dotnet-install.sh --version ${DOTNET_RUNTIME_VERSION} --runtime dotnet --install-dir /usr/share/dotnet \\"
        log_error "      && sudo ln -sf /usr/share/dotnet/dotnet /usr/local/bin/dotnet"
        log_error "  Or use a self-contained agent build (no runtime needed on the target)."
        return 1
    fi

    log_error "Agent runtime cannot reach the backend (exit $_pb_rc)."
    if [ -n "$_pb_url" ]; then
        log_error "  Configured ServerUrl: $_pb_url"
    fi
    log_error "  This is the failure that would have eaten the systemd start timeout."
    log_error "  Common causes: TLS cert chain missing for .NET runtime, HTTPS proxy required,"
    log_error "  IPv6-only DNS that the .NET resolver picks differently from curl."
    return 1
}

# Print the most useful diagnostics when something goes wrong.
diagnose_failure() {
    _df_url="${1:-}"
    printf "\n"
    log_info "=== Diagnostics ==="
    log_info "Last 40 log lines:"
    journalctl -u "$SERVICE_NAME" -n 40 --no-pager 2>/dev/null || true
    printf "\n"
    log_info "Service status:"
    systemctl status "$SERVICE_NAME" --no-pager -l 2>/dev/null || true
    printf "\n"
    log_info "Next steps:"
    if [ -n "$_df_url" ]; then
        log_info "  - Verify API reachable: curl -s -o /dev/null -w '%{http_code}\\n' ${_df_url}/health/live"
    fi
    log_info "  - Tail logs: journalctl -u $SERVICE_NAME -f"
    log_info "  - If the registration token is stale or already consumed, generate a"
    log_info "    fresh one in the web UI and re-run: sudo sh $0"
    log_info "  - Config: $INSTALL_DIR/appsettings.json"
}

# Wait for service to become active, then scan journal for enrollment outcome.
# Args: $1 = server URL (for diagnostic hint), $2 = since timestamp (ISO, optional).
# Returns 0 on healthy, 1 otherwise.
run_health_check() {
    _hc_url="${1:-}"
    _hc_since="${2:-}"
    printf "\n"
    log_info "Running health check..."

    # Phase 1 - wait for "active" state. Type=notify means the host signals ready
    # as soon as ExecuteAsync starts, typically within 2-5s. Give it up to 20s.
    _hc_waited=0
    _hc_max=20
    while [ "$_hc_waited" -lt "$_hc_max" ]; do
        if systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
            break
        fi
        sleep 1
        _hc_waited=$((_hc_waited + 1))
    done

    if ! systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
        log_error "Service did not reach 'active' within ${_hc_max}s."
        diagnose_failure "$_hc_url"
        return 1
    fi
    log_info "  Service active after ${_hc_waited}s."

    # Phase 2 - enrollment alone is not readiness: protocol/capabilities are authoritative only
    # after the initial heartbeat. Do not return success while the UI can still observe P? and an
    # empty capability set.
    log_info "  Waiting up to 45s for enrollment and initial compatibility heartbeat..."
    _hc_waited=0
    _hc_max=45
    _hc_enrolled=0
    _hc_contract_ready=0
    _hc_journal=""
    while [ "$_hc_waited" -lt "$_hc_max" ]; do
        if [ -n "$_hc_since" ]; then
            _hc_journal="$(journalctl -u "$SERVICE_NAME" --since "$_hc_since" --no-pager 2>/dev/null || echo '')"
        else
            _hc_journal="$(journalctl -u "$SERVICE_NAME" -n 200 --no-pager 2>/dev/null || echo '')"
        fi
        printf '%s\n' "$_hc_journal" | grep -qE 'Enrolled successfully|Agent already enrolled|Agent is enrolled and ready' \
            && _hc_enrolled=1
        printf '%s\n' "$_hc_journal" | grep -Fq 'Initial heartbeat sent; agent contract is ready' \
            && _hc_contract_ready=1
        if [ "$_hc_enrolled" -eq 1 ] && [ "$_hc_contract_ready" -eq 1 ]; then
            break
        fi
        if printf '%s\n' "$_hc_journal" | grep -qE 'Enrollment failed|Enrollment HTTP call.*failed|No registration token configured|credentials is missing or unreadable|Enrollment failed after [0-9]+ attempts'; then
            break
        fi
        if ! systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
            break
        fi
        sleep 1
        _hc_waited=$((_hc_waited + 1))
    done

    # Negative signals (most recent match wins).
    _hc_err="$(printf '%s\n' "$_hc_journal" \
        | grep -E 'Enrollment failed|Enrollment HTTP call.*failed|No registration token configured|credentials is missing or unreadable|Enrollment failed after [0-9]+ attempts' \
        | tail -1 || true)"

    if [ -n "$_hc_err" ]; then
        log_error "Enrollment problem detected in logs:"
        log_error "  $_hc_err"
        diagnose_failure "$_hc_url"
        return 1
    fi

    # Positive signals.
    if [ "$_hc_enrolled" -eq 1 ] && [ "$_hc_contract_ready" -eq 1 ]; then
        _hc_server_id="$(printf '%s\n' "$_hc_journal" \
            | grep -oE 'Server ID [a-zA-Z0-9-]+' | tail -1 || true)"
        if [ -n "$_hc_server_id" ]; then
            log_info "  Enrollment OK (${_hc_server_id})."
        else
            log_info "  Enrollment OK."
        fi
        if ! systemctl is-active --quiet "$SERVICE_NAME" 2>/dev/null; then
            log_warn "  ...but service is no longer active. Investigating."
            diagnose_failure "$_hc_url"
            return 1
        fi
        return 0
    fi

    if [ "$_hc_enrolled" -ne 1 ]; then
        log_error "  No successful enrollment outcome was observed; installation fails closed."
    else
        log_error "  Enrollment succeeded but no initial compatibility heartbeat was observed; installation fails closed."
    fi
    diagnose_failure "$_hc_url"
    return 1
}

# Check all required host templates before changing the host because every mode except purge renders
# them. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
if [ "$MODE" != "purge" ]; then
    require_host_config
fi

# =============================================================================
# Mode: BOOTSTRAP UPDATE SUPERVISOR
# =============================================================================
if [ "$MODE" = "bootstrap-update-supervisor" ]; then
    log_info "BOOTSTRAP mode: installing only the root-owned agent update supervisor."
    if [ ! -f "$INSTALL_DIR/appsettings.json" ]; then
        log_error "No existing config at $INSTALL_DIR/appsettings.json."
        exit 1
    fi
    if ! systemctl list-unit-files 2>/dev/null | grep -q "^${SERVICE_NAME}\.service"; then
        log_error "Service ${SERVICE_NAME} is not installed."
        exit 1
    fi
    EXISTING_URL="$(grep -oE '"ServerUrl"[[:space:]]*:[[:space:]]*"[^"]+"' "$INSTALL_DIR/appsettings.json" \
        | sed -E 's/.*"ServerUrl"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/' | head -1 || true)"
    case "$EXISTING_URL" in https://*) ;; *) log_error "Existing agent ServerUrl must use HTTPS."; exit 1 ;; esac
    install_agent_update_supervisor "$EXISTING_URL"
    for _supervisor_file in \
        "$AGENT_POSTURE_VERSION_FILE" "$AGENT_UPDATE_URL_FILE" \
        "$AGENT_UPDATE_WORKER_PATH" "$AGENT_UPDATE_SERVICE_PATH" "$AGENT_UPDATE_PATH_PATH"; do
        [ -f "$_supervisor_file" ] || { log_error "Supervisor file missing: $_supervisor_file"; exit 1; }
        [ "$(stat -c %u "$_supervisor_file")" = 0 ] || { log_error "Supervisor file is not root-owned: $_supervisor_file"; exit 1; }
        [ "$(stat -c %g "$_supervisor_file")" = 0 ] || { log_error "Supervisor file group is not root: $_supervisor_file"; exit 1; }
    done
    [ "$(cat "$AGENT_POSTURE_VERSION_FILE")" = "$AGENT_POSTURE_VERSION" ] || exit 1
    [ "$(stat -c %a "$AGENT_POSTURE_VERSION_FILE")" = 644 ] || exit 1
    [ "$(stat -c %a "$AGENT_UPDATE_URL_FILE")" = 600 ] || exit 1
    [ "$(stat -c %a "$AGENT_UPDATE_WORKER_PATH")" = 755 ] || exit 1
    [ "$(stat -c %a "$AGENT_UPDATE_SERVICE_PATH")" = 644 ] || exit 1
    [ "$(stat -c %a "$AGENT_UPDATE_PATH_PATH")" = 644 ] || exit 1
    systemctl is-enabled --quiet aetheus-agent-upgrade.path
    systemctl is-active --quiet aetheus-agent-upgrade.path
    log_info "Agent update supervisor bootstrap complete; the running agent binary was not replaced."
    exit 0
fi

# =============================================================================
# Mode: PURGE
# =============================================================================
if [ "$MODE" = "purge" ]; then
    log_warn "PURGE mode: this will remove the service, $INSTALL_DIR, $WORK_DIR, and the $AGENT_USER user."
    if [ "$ASSUME_YES" -ne 1 ]; then
        printf "Type 'PURGE' to confirm: "
        read _confirm
        if [ "$_confirm" != "PURGE" ]; then
            log_info "Aborted."
            exit 0
        fi
    fi

    log_info "Stopping and disabling service..."
    stop_and_disable_service
    systemctl disable --now aetheus-agent-upgrade.path 2>/dev/null || true
    rm -f "$AGENT_UPDATE_SERVICE_PATH" "$AGENT_UPDATE_PATH_PATH" \
        "$AGENT_UPDATE_WORKER_PATH" "$AGENT_UPDATE_URL_FILE" \
        "$AGENT_POSTURE_VERSION_FILE" "$AGENT_UPDATE_REQUEST_FILE"
    remove_systemd_unit

    # Take the agent account's read ACLs back (SSH configuration) before the account and the helper go.
    if [ -x "$AGENT_READ_GRANT_HELPER_PATH" ]; then
        "$AGENT_READ_GRANT_HELPER_PATH" --revoke || true
    fi
    rm -f "$AGENT_READ_GRANT_HELPER_PATH" "$AGENT_READ_GRANT_SERVICE_PATH"
    systemctl daemon-reload 2>/dev/null || true

    if [ -f "$SUDOERS_FILE" ]; then
        log_info "Removing sudoers file..."
        rm -f "$SUDOERS_FILE"
    fi
    if [ -f "$APACHE_MANAGE_SUDOERS_FILE" ]; then
        log_info "Removing Apache-manage sudoers file..."
        rm -f "$APACHE_MANAGE_SUDOERS_FILE"
    fi
    rm -f "$APACHE_CONFIGTEST_HELPER_PATH"
    if [ -f "$RKHUNTER_MANAGE_SUDOERS_FILE" ]; then
        log_info "Removing RKHunter-manage sudoers file..."
        rm -f "$RKHUNTER_MANAGE_SUDOERS_FILE" "$RKHUNTER_HELPER_PATH"
    fi
    if [ -f "$CRON_MANAGE_SUDOERS_FILE" ] || [ -f "$PORTSENTRY_MANAGE_SUDOERS_FILE" ] || \
       [ -f "$SERVICE_ENABLE_SUDOERS_FILE" ] || [ -f "$PACKAGE_MANAGE_SUDOERS_FILE" ] || \
       [ -f "$PATCH_MANAGE_SUDOERS_FILE" ] || [ -f "$FIREWALL_MANAGE_SUDOERS_FILE" ] || \
       [ -f "$MAIL_MANAGE_SUDOERS_FILE" ] || [ -f "$DEPLOY_MANAGE_SUDOERS_FILE" ] || \
       [ -f "$CERTBOT_MANAGE_SUDOERS_FILE" ] || [ -f "$TEAMSPEAK_SETUP_SUDOERS_FILE" ] || \
       [ -d "$AETHEUS_HELPER_DIR" ]; then
        log_info "Removing privileged-capability sudoers files + helpers..."
        rm -f "$CRON_MANAGE_SUDOERS_FILE" "$PORTSENTRY_MANAGE_SUDOERS_FILE" "$SERVICE_ENABLE_SUDOERS_FILE"
        rm -f "$PACKAGE_MANAGE_SUDOERS_FILE" "$PATCH_MANAGE_SUDOERS_FILE" "$FIREWALL_MANAGE_SUDOERS_FILE" "$MAIL_MANAGE_SUDOERS_FILE" "$DEPLOY_MANAGE_SUDOERS_FILE"
        rm -f "$CERTBOT_MANAGE_SUDOERS_FILE" "$TEAMSPEAK_SETUP_SUDOERS_FILE"
        rm -f "$CRON_HELPER_PATH" "$UNBLOCK_HELPER_PATH" "$PORTSENTRY_SETUP_HELPER_PATH" "$MAIL_SETUP_HELPER_PATH" "$MAIL_MANAGE_HELPER_PATH" "$DEPLOY_RESTART_HELPER_PATH" "$FIREWALL_HELPER_PATH"
        rm -f "$CERTBOT_ISSUE_HELPER_PATH" "$CERTBOT_MANAGE_HELPER_PATH" "$TEAMSPEAK_SETUP_HELPER_PATH"
        remove_certbot_conventions
        rmdir "$AETHEUS_HELPER_DIR" 2>/dev/null || true
    fi
    # Deployment unit template lives under /etc/systemd/system (not $AETHEUS_HELPER_DIR); remove it too.
    # Deployed app instances under $WORK_DIR/deploy go away with the $WORK_DIR rm -rf below.
    if [ -f "$DEPLOY_UNIT_TEMPLATE_PATH" ]; then
        log_info "Removing deploy unit template..."
        rm -f "$DEPLOY_UNIT_TEMPLATE_PATH"
        systemctl daemon-reload 2>/dev/null || true
    fi

    # cd out of INSTALL_DIR in case the script is running from inside it;
    # rm -rf is fine for open file descriptors but some shells get upset
    # if their CWD disappears.
    cd /

    if [ -d "$INSTALL_DIR" ]; then
        log_info "Removing $INSTALL_DIR..."
        rm -rf "$INSTALL_DIR"
    fi

    if [ -d "$WORK_DIR" ]; then
        log_info "Removing $WORK_DIR..."
        rm -rf "$WORK_DIR"
    fi

    if id "$AGENT_USER" >/dev/null 2>&1; then
        # Only a membership this installer recorded is withdrawn (decision D12); one it did not add is
        # not its to remove.
        if docker_group_added_by_installer; then
            gpasswd -d "$AGENT_USER" docker >/dev/null 2>&1 || true
        fi
        gpasswd -d "$AGENT_USER" teamspeak >/dev/null 2>&1 || true
        log_info "Deleting user $AGENT_USER..."
        userdel "$AGENT_USER" 2>/dev/null || log_warn "userdel reported issues (non-fatal)"
        # Group is auto-removed by userdel on distros using USERGROUPS_ENAB yes;
        # clean up explicitly just in case.
        if getent group "$AGENT_GROUP" >/dev/null 2>&1; then
            groupdel "$AGENT_GROUP" 2>/dev/null || true
        fi
    fi

    rm -f "$DOCKER_GROUP_MARKER_FILE"

    log_info ""
    log_info "Purge complete. The agent is fully removed from this system."
    exit 0
fi

# Restores the executable bit on the native binaries the agent runs, after the files land in
# $INSTALL_DIR. A tarball packed on Windows (.NET TarFile) loses the Unix +x bit, so this is never
# left to the archive. Two kinds of binary:
#   - Aetheus.Agent.Linux: the self-contained apphost that systemd ExecStart and the pre-start probe
#     run directly (absent in a framework-dependent build, hence the existence check).
#   - aetheus-fn/<rid>/aetheus-fn: the pipeline-functions executable (PLAN-002 lot 1.0g), one per
#     Linux C library. It is bind-mounted read-only into a step container that runs as a non-root
#     user, so it must be world-readable and world-executable (755), not only owner-executable.
restore_executable_modes() {
    if [ -f "$INSTALL_DIR/Aetheus.Agent.Linux" ]; then
        chmod +x "$INSTALL_DIR/Aetheus.Agent.Linux"
    fi
    for _fn_bin in "$INSTALL_DIR"/aetheus-fn/*/aetheus-fn; do
        [ -f "$_fn_bin" ] || continue
        chmod 755 "$_fn_bin"
    done
}

# =============================================================================
# Mode: UPGRADE
# =============================================================================
if [ "$MODE" = "upgrade" ]; then
    log_info "UPGRADE mode: replacing binaries + service, preserving config and data."

    # Preconditions: existing install must be present.
    if [ ! -f "$INSTALL_DIR/appsettings.json" ]; then
        log_error "No existing config at $INSTALL_DIR/appsettings.json."
        log_error "Upgrade requires a prior installation. Use the default mode to install fresh."
        exit 1
    fi
    if ! systemctl list-unit-files 2>/dev/null | grep -q "^${SERVICE_NAME}\.service"; then
        log_error "Service ${SERVICE_NAME} not installed. Use the default mode to install fresh."
        exit 1
    fi

    SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"

    # Refuse to run from INSIDE $INSTALL_DIR. If the user extracted the new
    # tarball there, the shipped default appsettings.json has already clobbered
    # the configured one - we can't recover it from this script.
    if [ "$SCRIPT_DIR" = "$INSTALL_DIR" ]; then
        log_error "Upgrade must be run from outside $INSTALL_DIR."
        log_error "Extract the new tarball to a temp dir first, then re-run from there:"
        log_error "  mkdir -p /tmp/aetheus-agent-upgrade && cd /tmp/aetheus-agent-upgrade"
        log_error "  tar -xzf /path/to/aetheus-agent-linux-x64-v*.tar.gz"
        log_error "  sudo sh ./install-agent-linux.sh --upgrade"
        exit 1
    fi

    if [ ! -f "$SCRIPT_DIR/Aetheus.Agent.Linux.dll" ] && [ ! -f "$SCRIPT_DIR/Aetheus.Agent.Linux" ]; then
        log_error "New agent binaries not found in $SCRIPT_DIR."
        exit 1
    fi

    # Capture the server URL from the existing config for the health-check hint.
    EXISTING_URL="$(grep -oE '"ServerUrl"[[:space:]]*:[[:space:]]*"[^"]+"' "$INSTALL_DIR/appsettings.json" \
        | sed -E 's/.*"ServerUrl"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/' | head -1 || true)"

    log_info "Stopping service before swapping binaries..."
    stop_and_disable_service

    # Copy everything from the new tree EXCEPT appsettings.json (preserve config).
    # Using find ... -exec cp -r is portable (no rsync dependency).
    log_info "Copying new binaries to $INSTALL_DIR (preserving appsettings.json)..."
    find "$SCRIPT_DIR" -maxdepth 1 -mindepth 1 \
        ! -name 'appsettings.json' \
        -exec cp -r {} "$INSTALL_DIR/" \;

    # Re-chown in case new files came in as root.
    if id "$AGENT_USER" >/dev/null 2>&1; then
        chown -R "$AGENT_USER:$AGENT_GROUP" "$INSTALL_DIR"
        chown "$AGENT_USER:$AGENT_GROUP" "$INSTALL_DIR/appsettings.json"
        chmod 600 "$INSTALL_DIR/appsettings.json"
    else
        log_warn "User $AGENT_USER missing - upgrade continued but service will fail to start. Run default install mode."
    fi

    # The swapped-in native binaries may have lost their +x bit (tarball packed on Windows).
    restore_executable_modes

    # Preserve the operator's existing elevation posture across the upgrade
    # (never silently widen or drop it); explicit --enable-* flags override.
    derive_existing_posture
    ensure_optional_packages
    ensure_icu || true
    apply_sudoers
    write_systemd_unit
    install_agent_update_supervisor "$EXISTING_URL"
    if id "$AGENT_USER" >/dev/null 2>&1; then
        apply_read_acls
        ensure_docker_group
        ensure_teamspeak_access
    fi

    # Pre-start runtime probe: an upgraded binary can ship with a newer .NET
    # runtime that handles TLS differently, so re-verify the agent itself can
    # reach the backend BEFORE we ask systemd to start it.
    if ! run_backend_probe "$EXISTING_URL"; then
        exit 1
    fi

    write_installed_at_marker

    _service_start_ts="$(date -u +"%Y-%m-%d %H:%M:%S UTC")"
    log_info "Reloading systemd and starting service..."
    systemctl daemon-reload
    systemctl enable "$SERVICE_NAME"
    systemctl start "$SERVICE_NAME"

    if ! run_health_check "$EXISTING_URL" "$_service_start_ts"; then
        exit 1
    fi

    log_info ""
    log_info "Upgrade complete."
    log_info "  Config preserved: $INSTALL_DIR/appsettings.json"
    log_info "  Data preserved:   $WORK_DIR"
    log_info "  Logs:             journalctl -u $SERVICE_NAME -f"
    print_posture_summary
    exit 0
fi

# =============================================================================
# Mode: INSTALL (default)
# =============================================================================

# --- Interactive prompts ---
if [ -z "$SERVER_URL" ]; then
    if [ -n "$URL_FROM_MARKER" ] && [ "$URL_FROM_MARKER" != "https://aetheus.example.com" ]; then
        SERVER_URL="$URL_FROM_MARKER"
        log_info "Using server URL from marker: $SERVER_URL"
    else
        printf "Aetheus server URL [%s]: " "$DEFAULT_SERVER_URL"
        read SERVER_URL
        if [ -z "$SERVER_URL" ]; then
            SERVER_URL="$DEFAULT_SERVER_URL"
        fi
    fi
fi

if [ -z "$REG_TOKEN" ]; then
    printf "Registration token (input hidden): "
    stty -echo 2>/dev/null || true
    read REG_TOKEN
    stty echo 2>/dev/null || true
    printf "\n"
fi

if [ -z "$REG_TOKEN" ]; then
    log_error "Registration token cannot be empty. Generate one in the Aetheus web UI."
    exit 1
fi

TOKEN_LEN=${#REG_TOKEN}
if [ "$TOKEN_LEN" -gt 10 ]; then
    TOKEN_FIRST="$(printf '%s' "$REG_TOKEN" | cut -c1-5)"
    TOKEN_LAST="$(printf '%s' "$REG_TOKEN" | cut -c$((TOKEN_LEN - 4))-)"
    printf "  Token received: %s...%s (%d chars)\n" "$TOKEN_FIRST" "$TOKEN_LAST" "$TOKEN_LEN"
else
    printf "  Token received: %d chars\n" "$TOKEN_LEN"
fi

# --- Prefer HTTPS for a remote backend ---
# The agent publishes its own ServerUrl as the package base URL, and NuGet refuses to
# push over plain HTTP ("NuGet requires HTTPS sources"), which broke the observability
# package publisher in production. Upgrade a remote http:// endpoint to https:// only
# when HTTPS actually answers, so a local or self-hosted dev backend keeps working.
case "$SERVER_URL" in
    http://*)
        SERVER_URL_HOST="${SERVER_URL#http://}"
        SERVER_URL_HOST="${SERVER_URL_HOST%%/*}"
        SERVER_URL_HOST="${SERVER_URL_HOST%%:*}"
        case "$SERVER_URL_HOST" in
            localhost|127.*|::1|host.docker.internal|10.*|192.168.*|172.1[6-9].*|172.2[0-9].*|172.3[01].*)
                log_info "Keeping http:// for local backend $SERVER_URL_HOST."
                ;;
            *)
                SERVER_URL_HTTPS="https://${SERVER_URL#http://}"
                if command -v curl >/dev/null 2>&1; then
                    HTTPS_PROBE="$(curl -s -o /dev/null -w "%{http_code}" --max-time 10 "${SERVER_URL_HTTPS}/health/live" 2>/dev/null || echo "000")"
                    if [ "$HTTPS_PROBE" = "200" ]; then
                        log_warn "Server URL was http://; HTTPS answers, switching to $SERVER_URL_HTTPS."
                        log_warn "  Plain HTTP breaks NuGet package publishing and sends the enrolment token in clear."
                        SERVER_URL="$SERVER_URL_HTTPS"
                    else
                        log_warn "Server URL uses http:// and HTTPS did not answer (got $HTTPS_PROBE) - keeping http://."
                        log_warn "  NuGet package publishing will refuse this source."
                    fi
                fi
                ;;
        esac
        ;;
esac

# --- Validate that ServerUrl points to the backend API, not the frontend ---
# The backend exposes GET /health/live (anonymous liveness probe); the frontend
# (Blazor WASM) does not. This catches the classic mistake of using
# https://aetheus.example.com (frontend) instead of
# https://aetheus-api.example.com (backend API).
log_info "Validating server URL reachability: ${SERVER_URL}/health/live"
if command -v curl >/dev/null 2>&1; then
    # Honour --allow-insecure-certs here too: a self-signed backend (dev / localhost)
    # would otherwise make this pre-flight curl fail TLS verification (code 000) even
    # though the agent itself is configured to bypass it.
    CURL_K=""
    [ "$ALLOW_INSECURE_CERTS" -eq 1 ] && CURL_K="-k"
    HEALTH_HTTP_CODE="$(curl -s $CURL_K -o /dev/null -w '%{http_code}' --max-time 10 "${SERVER_URL}/health/live" || echo "000")"
    if [ "$HEALTH_HTTP_CODE" != "200" ]; then
        log_error "Server URL '${SERVER_URL}' did not respond 200 on /health/live (got ${HEALTH_HTTP_CODE})."
        log_error "Make sure you pointed at the backend API, not the frontend."
        log_error "Example: https://aetheus-api.example.com (API) - NOT https://aetheus.example.com (front)."
        exit 1
    fi
    log_info "Server URL is reachable (HTTP 200 on /health/live)."
else
    log_warn "curl not available - skipping server URL validation."
fi

if [ -z "$AGENT_NAME" ]; then
    AGENT_NAME="$(hostname)"
    log_info "Using hostname as agent name: $AGENT_NAME (changeable from the web UI)"
fi

# --- Resolve script location ---
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
if [ ! -f "$SCRIPT_DIR/Aetheus.Agent.Linux.dll" ] && [ ! -f "$SCRIPT_DIR/Aetheus.Agent.Linux" ]; then
    log_error "Agent binaries not found in $SCRIPT_DIR. Extract the agent archive first."
    exit 1
fi

# --- Preserve the elevation posture of a pre-existing install ---
# An agent is disposable: re-running the enrolment command from the UI must be
# idempotent, never a downgrade. apply_sudoers revokes (rm -f) every capability whose
# flag is 0, so without this derivation a plain reinstall silently disarmed the agent
# (certbot, Apache, docker, deployment...) and the production pipelines broke. This runs
# BEFORE the cleanup below, because the cleanup deletes the very sudoers files that act
# as the on-disk witnesses of the posture. Explicit --enable-*/--disable-* flags still win.
derive_existing_posture

# --- Clean any previous installation ---
# Stop + remove service, sudoers, stale config, and wipe work dir so the reinstall
# starts from a known state. We never rm -rf "$INSTALL_DIR" because this script
# typically lives inside it - removing it would kill the running shell.
preserve_production_environment
log_info "Cleaning previous installation (if any)..."
stop_and_disable_service
remove_systemd_unit

if [ -f "$SUDOERS_FILE" ]; then
    log_info "  Removing old sudoers rules..."
    rm -f "$SUDOERS_FILE"
fi
if [ -f "$APACHE_MANAGE_SUDOERS_FILE" ]; then
    log_info "  Removing old Apache-manage sudoers rules..."
    rm -f "$APACHE_MANAGE_SUDOERS_FILE"
fi
rm -f "$APACHE_CONFIGTEST_HELPER_PATH"
if [ -f "$RKHUNTER_MANAGE_SUDOERS_FILE" ]; then
    log_info "  Removing old RKHunter-manage sudoers rules..."
    rm -f "$RKHUNTER_MANAGE_SUDOERS_FILE" "$RKHUNTER_HELPER_PATH"
fi

if [ -f "$INSTALL_DIR/appsettings.json" ]; then
    log_info "  Removing old appsettings.json (stale token/enrollment)..."
    rm -f "$INSTALL_DIR/appsettings.json"
fi

purge_agent_work_dir

# --- Move files to INSTALL_DIR if needed ---
if [ "$SCRIPT_DIR" != "$INSTALL_DIR" ]; then
    log_info "Copying agent files to $INSTALL_DIR..."
    mkdir -p "$INSTALL_DIR"
    cp -r "$SCRIPT_DIR/"* "$INSTALL_DIR/"
fi

# --- Create system user ---
log_info "Creating system user: $AGENT_USER"
if id "$AGENT_USER" >/dev/null 2>&1; then
    # Re-install is normal - keep the existing system user instead of yelling.
    log_info "User $AGENT_USER already exists - reusing"
else
    useradd -r -s /bin/false -d "$WORK_DIR" "$AGENT_USER"
fi
if [ -d "$PRODUCTION_STATE_DIR" ]; then
    chown -R "$AGENT_USER:$AGENT_GROUP" "$PRODUCTION_STATE_DIR"
    chmod 700 "$PRODUCTION_STATE_DIR"
fi
# --- Add agent user to docker group (required for container/image collection) ---
ensure_docker_group

# --- Provision TeamSpeak access (opt-in: --enable-teamspeak / --module server-management) ---
ensure_teamspeak_access

# --- Create directories ---
log_info "Creating directories..."
mkdir -p "$WORK_DIR"
chown "$AGENT_USER:$AGENT_GROUP" "$WORK_DIR"
chown -R "$AGENT_USER:$AGENT_GROUP" "$INSTALL_DIR"

# Native binaries (agent apphost, aetheus-fn per C library) get their modes back here.
restore_executable_modes

# --- Write configuration ---
# JSON-escape operator-supplied values so a " or \ in the URL/token/name cannot
# break out of its string and inject config structure.
log_info "Writing configuration to $INSTALL_DIR/appsettings.json..."

# Auto-enable insecure certs for localhost (self-signed dev cert) - parity with
# the Windows installer. Explicit opt-in elsewhere via --allow-insecure-certs.
case "$SERVER_URL" in
    *://localhost*|*://127.0.0.1*|*://\[::1\]*) ALLOW_INSECURE_CERTS=1 ;;
esac
ALLOW_INSECURE_JSON="false"
if [ "$ALLOW_INSECURE_CERTS" -eq 1 ]; then
    ALLOW_INSECURE_JSON="true"
    log_warn "AllowInsecureCerts enabled (DEV mode): TLS validation bypassed for backend API and pipeline git clones"
fi

SERVER_URL_J="$(json_escape "$SERVER_URL")"
REG_TOKEN_J="$(json_escape "$REG_TOKEN")"
AGENT_NAME_J="$(json_escape "$AGENT_NAME")"
WORK_DIR_J="$(json_escape "$WORK_DIR")"
DOCKER_STORAGE_DEPLOYMENT_ONLY=false
# A deployment-capable pipeline runner is still a general runner. Restrict the task gate only
# when the host was deliberately installed without the pipeline-runner module.
if [ "$MODULE_DEPLOYMENT" -eq 1 ] && [ "$MODULE_PIPELINE_RUNNER" -eq 0 ]; then
    DOCKER_STORAGE_DEPLOYMENT_ONLY=true
fi
render_host_config agent/appsettings.json "$INSTALL_DIR/appsettings.json"
chown "$AGENT_USER:$AGENT_GROUP" "$INSTALL_DIR/appsettings.json"
chmod 600 "$INSTALL_DIR/appsettings.json"

ensure_optional_packages
ensure_icu || true
apply_sudoers
write_systemd_unit
install_agent_update_supervisor "$SERVER_URL"
apply_read_acls

# --- Faithful pre-start probe ---
# Fails fast (well before the 120s systemd TimeoutStartSec) if the .NET HTTP
# stack can't reach the backend even though curl could - typically a TLS cert
# chain or proxy issue that only the runtime sees.
if ! run_backend_probe "$SERVER_URL"; then
    exit 1
fi

write_installed_at_marker

# --- Enable and start ---
_service_start_ts="$(date -u +"%Y-%m-%d %H:%M:%S UTC")"
log_info "Enabling and starting service..."
systemctl daemon-reload
systemctl enable "$SERVICE_NAME"
systemctl start "$SERVICE_NAME"

# --- Health check (only inspect logs since this start) ---
if ! run_health_check "$SERVER_URL" "$_service_start_ts"; then
    exit 1
fi

log_info ""
log_info "Aetheus Agent is running!"
log_info ""
log_info "  Server URL: $SERVER_URL"
log_info "  Agent name: $AGENT_NAME"
log_info ""
log_info "  Status:  systemctl status $SERVICE_NAME"
log_info "  Logs:    journalctl -u $SERVICE_NAME -f"
log_info "  Config:  $INSTALL_DIR/appsettings.json"
log_info "  Install: $INSTALL_DIR"
log_info "  Data:    $WORK_DIR"
print_posture_summary
