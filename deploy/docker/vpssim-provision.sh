#!/usr/bin/env bash
# =============================================================================
# vpssim-provision.sh - first-boot provisioning for the Aetheus VPS-sim
# =============================================================================
# Runs ONCE at first boot (systemd oneshot), after dockerd is up. Handles the
# provisioning steps that need a live daemon or a representative runtime - the
# static config (apache vhost, certs, postfix, portsentry) is baked at image
# build time. Real operations only: no faked success. LOCAL TEST ONLY.
# =============================================================================
set -eu

MARKER=/var/lib/vpssim-provisioned
[ -e "$MARKER" ] && exit 0

log() { echo "[vpssim-provision] $*"; }

# --- DockerCollector: a real running container + image so `docker ps`/`images`
#     return data. nginx is tiny and always-on.
log "Starting a test nginx container for DockerCollector..."
if docker run -d --name vpssim-web --restart unless-stopped -p 8081:80 nginx:alpine >/dev/null 2>&1; then
    log "  nginx container up."
else
    log "  WARN: could not start nginx container (DockerCollector will show no containers)."
fi

# --- RkhunterCollector: a real scan generates /var/log/rkhunter.log, which the
#     collector parses for the last-scan summary. Skip key/weak-file checks so it
#     completes quickly inside a container.
log "Running an initial rkhunter scan to populate the log..."
rkhunter --check --sk --nocolors --report-warnings-only \
    --skip-keypress >/dev/null 2>&1 || true
log "  rkhunter scan done (log at /var/log/rkhunter.log)."

# --- Demo TLS: keep the trust anchor equal to the certificate actually served.
#     Dockerfile.vpssim generates the self-signed demo pair into /etc/letsencrypt, which is a NAMED
#     VOLUME. A fresh volume receives the image copy, but an existing one masks it: after an image
#     rebuild the box trusts the anchor baked in the new image while Apache still serves the older
#     certificate from the volume, and the deployment probes fail with "The SSL connection could not
#     be established" (nightly run 1220: anchor D5:C7..., served 7B:6F...). Anchoring the certificate
#     that is really on disk removes the drift in both directions.
#     This is not a TLS relaxation: the probes still require a valid chain, the simulated host simply
#     provides the root for the certificate it serves, which a real Let's Encrypt host gets for free.
DEMO_CERT=/etc/letsencrypt/live/demo.app.aetheus.sonytumen.com/fullchain.pem
DEMO_ANCHOR=/usr/local/share/ca-certificates/aetheus-local-simulation-demo.crt
if [ -f "$DEMO_CERT" ]; then
    if ! cmp -s "$DEMO_CERT" "$DEMO_ANCHOR"; then
        log "Re-anchoring the demo certificate actually present in /etc/letsencrypt..."
        install -m 644 "$DEMO_CERT" "$DEMO_ANCHOR"
        update-ca-certificates >/dev/null 2>&1 || log "  WARN: update-ca-certificates reported an error."
    fi
    log "  demo certificate and trust anchor are the same certificate."
else
    log "  WARN: no demo certificate at $DEMO_CERT; the deployment probes will refuse the chain."
fi

touch "$MARKER"
log "First-boot provisioning complete."
