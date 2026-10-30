#!/usr/bin/env bash
# =============================================================================
# vpssim-agent-bootstrap.sh - optional agent auto-install for the VPS-sim
# =============================================================================
# Opt-in (AGENT_AUTOINSTALL=1): mints a registration token from the backend API
# at boot, then runs the REAL install-agent-linux.sh so the agent installs and
# self-registers - the VPS-sim then appears in the dashboard with all its
# services reported. When disabled, the box stays a blank VPS (no agent).
#
# Token sourcing:
#   - if AGENT_TOKEN is set, use it verbatim (host-minted path);
#   - else log in as ADMIN_EMAIL/ADMIN_PASSWORD and POST /api/auth/registration-tokens.
#
# Real operations only - no faked success. LOCAL TEST ONLY.
# =============================================================================
set -eu

log() { echo "[vpssim-agent] $*"; }

# Replacing the outer simulator container is equivalent to rebooting/reprovisioning the local VPS.
# The real installer intentionally purges its work directory on a fresh enrollment, but that same
# directory also contains application-owned deployment state (`aetheus-*`: generated secrets,
# live colour, source commit and backups). Preserve only those application directories across the
# simulator's mandatory fresh enrollment; agent credentials and caches must be recreated normally.
PRESERVED_DEPLOY_STATE="/run/vpssim-preserved-deploy-state"
preserve_deploy_state() {
    rm -rf "$PRESERVED_DEPLOY_STATE"
    mkdir -p "$PRESERVED_DEPLOY_STATE"
    for state_dir in /var/lib/aetheus-agent/aetheus-*; do
        [ -d "$state_dir" ] || continue
        cp -a "$state_dir" "$PRESERVED_DEPLOY_STATE/"
    done
}

restore_deploy_state() {
    [ -d "$PRESERVED_DEPLOY_STATE" ] || return 0
    for state_dir in "$PRESERVED_DEPLOY_STATE"/aetheus-*; do
        [ -d "$state_dir" ] || continue
        cp -a "$state_dir" /var/lib/aetheus-agent/
    done
    if id aetheus-agent >/dev/null 2>&1; then
        chown -R aetheus-agent:aetheus-agent /var/lib/aetheus-agent/aetheus-* 2>/dev/null || true
    fi
    rm -rf "$PRESERVED_DEPLOY_STATE"
}

# systemd starts services with a clean environment - the container env vars
# (compose `environment:`) live on PID 1 only. Import the ones we need from it.
if [ -r /proc/1/environ ]; then
    while IFS= read -r -d '' _kv; do
        case "$_kv" in
            AGENT_AUTOINSTALL=*|BACKEND_URL=*|ADMIN_EMAIL=*|ADMIN_PASSWORD=*|AGENT_NAME=*|AGENT_TOKEN=*)
                export "${_kv?}" ;;
        esac
    done < /proc/1/environ
fi

[ "${AGENT_AUTOINSTALL:-0}" = "1" ] || { log "auto-install disabled (AGENT_AUTOINSTALL!=1) - box stays blank."; exit 0; }

BACKEND_URL="${BACKEND_URL:-https://host.docker.internal:5301}"
ADMIN_EMAIL="${ADMIN_EMAIL:-admin@aetheus.local}"
ADMIN_PASSWORD="${ADMIN_PASSWORD:-}"
AGENT_NAME="${AGENT_NAME:-vpssim}"
TOKEN="${AGENT_TOKEN:-}"

# This bootstrap deliberately trusts the self-signed development certificate, so
# it must never be repointed at a remote authority. Validate the complete URL
# before any credential or registration token can leave the simulator.
if [[ ! "$BACKEND_URL" =~ ^https://(host\.docker\.internal|localhost|127\.0\.0\.1)(:[0-9]{1,5})?(/.*)?$ ]]; then
    log "ERROR: BACKEND_URL must be an HTTPS loopback/host.docker.internal URL for this local-only bootstrap."
    exit 1
fi

# The development control plane serves a self-signed certificate. Node ignores the system CA store
# unless NODE_EXTRA_CA_CERTS points at it (a drop-in in the image does that), and the store must also
# actually CONTAIN that root, or `npm publish` to the control plane registry fails with
# DEPTH_ZERO_SELF_SIGNED_CERT - which is what stopped publish-observability-packages on the mirror.
# The certificate is generated on the developer machine, so it cannot be baked into the image, and it
# is renewed there independently of this box; reading it at boot from the endpoint the guard above
# just proved to be a loopback one keeps the two in step. Done BEFORE the already-installed exit, so
# an existing box refreshes the trust on restart instead of keeping a certificate that has rotated.
#
# This is the opposite of relaxing TLS: the chain stays verified, the simulated host is handed the
# root to verify it against, exactly as the baked demo certificate does for the smoke probe.
# A failure here is a warning, never an abort: the box is still a usable VPS without it.
trust_control_plane_certificate() {
    hostport="${BACKEND_URL#https://}"
    hostport="${hostport%%/*}"
    case "$hostport" in *:*) : ;; *) hostport="${hostport}:443" ;; esac
    attempt=1
    while [ "$attempt" -le 20 ]; do
        pem="$(openssl s_client -connect "$hostport" -servername "${hostport%%:*}" </dev/null 2>/dev/null | openssl x509 -outform pem 2>/dev/null)"
        [ -n "$pem" ] && break
        attempt=$((attempt + 1))
        sleep 3
    done
    if [ -z "$pem" ]; then
        log "WARN: no certificate readable at $hostport; npm publish to that registry will fail"
        log "WARN: with DEPTH_ZERO_SELF_SIGNED_CERT until the control plane is up and the box reboots."
        return 0
    fi
    echo "$pem" > /usr/local/share/ca-certificates/aetheus-dev.crt
    chmod 644 /usr/local/share/ca-certificates/aetheus-dev.crt
    update-ca-certificates >/dev/null 2>&1 || log "WARN: update-ca-certificates reported an error."
    log "Development control plane certificate installed in the system CA store."
}
trust_control_plane_certificate

if [ -e /var/lib/vpssim-agent-installed ] && [ -x /opt/aetheus-agent/Aetheus.Agent.Linux ]; then
    log "agent already installed."
    exit 0
fi
# The state volume survives a simulator container replacement while /opt does not. A marker
# without the installed binary is stale and must never suppress enrollment of the rebuilt agent.
rm -f /var/lib/vpssim-agent-installed

if [ -z "$TOKEN" ] && [ -z "$ADMIN_PASSWORD" ]; then
    log "ERROR: set AGENT_TOKEN or VPSSIM_ADMIN_PASSWORD when VPSSIM_AGENT=1; no default password is embedded."
    exit 1
fi

# Backend may come up after the container - wait for health (max ~3 min).
log "Waiting for backend at $BACKEND_URL ..."
for _ in $(seq 1 60); do
    if curl -sk --max-time 4 "$BACKEND_URL/health/live" -o /dev/null; then break; fi
    sleep 3
done
if ! curl -sk --max-time 4 "$BACKEND_URL/health/live" -o /dev/null; then
    log "ERROR: backend not reachable at $BACKEND_URL - aborting (box still usable without agent)."
    exit 1
fi

if [ -z "$TOKEN" ]; then
    log "Logging in as $ADMIN_EMAIL to mint a registration token..."
    LOGIN_PAYLOAD="$(jq -n --arg email "$ADMIN_EMAIL" --arg password "$ADMIN_PASSWORD" \
        '{email: $email, password: $password}')"
    JWT="$(curl -sk --max-time 10 -X POST "$BACKEND_URL/api/auth/login" \
        -H 'Content-Type: application/json' \
        -d "$LOGIN_PAYLOAD" | jq -r '.token // empty')"
    if [ -z "$JWT" ] || [ "$JWT" = "null" ]; then
        log "ERROR: login failed (no JWT). Check ADMIN_EMAIL/ADMIN_PASSWORD."
        exit 1
    fi
    log "Minting registration token via /api/auth/registration-tokens..."
    TOKEN="$(curl -sk --max-time 10 -X POST "$BACKEND_URL/api/auth/registration-tokens" \
        -H "Authorization: Bearer $JWT" -H 'Content-Type: application/json' \
        -d '{"expirationHours":24}' | jq -r '.token // empty')"
    if [ -z "$TOKEN" ] || [ "$TOKEN" = "null" ]; then
        log "ERROR: token mint failed."
        exit 1
    fi
    log "Registration token obtained."
fi

cd /opt/agent-pkg || { log "ERROR: /opt/agent-pkg missing."; exit 1; }
chmod +x install-agent-linux.sh Aetheus.Agent.Linux 2>/dev/null || true

log "Running install-agent-linux.sh (pipeline-runner + server-management + deployment, docker + certbot + teamspeak)..."
preserve_deploy_state
trap restore_deploy_state EXIT
if ./install-agent-linux.sh \
        --server-url "$BACKEND_URL" \
        --token "$TOKEN" \
        --name "$AGENT_NAME" \
        --module pipeline-runner \
        --module server-management \
        --module deployment \
        --enable-docker \
        --enable-certbot-manage \
        --enable-teamspeak \
        --allow-insecure-certs \
        --yes; then
    RC=0
else
    RC=$?
fi
restore_deploy_state
trap - EXIT

if [ $RC -eq 0 ]; then
    touch /var/lib/vpssim-agent-installed
    log "Agent installed and enrolled. The VPS-sim should now appear in the dashboard."
else
    log "ERROR: install-agent-linux.sh exited $RC."
fi
exit $RC
