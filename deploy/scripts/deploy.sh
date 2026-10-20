#!/bin/sh
# SPDX-License-Identifier: EUPL-1.2
# SPDX-FileCopyrightText: 2026 Aetheus contributors
set -e

# =============================================================================
# deploy.sh - Aetheus Remote Deployment
# =============================================================================
APPNAME="aetheus"
# Require repository and host values explicitly so an unset variable cannot silently select another
# installation. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
GITREMOTE="${GITREMOTE:-}"
SERVERNAME="${SERVERNAME:-}"
require_env() {
    eval "value=\${$1}"
    if [ -z "$value" ]; then
        echo "$1 is required: this script does not assume a host. Set it and re-run." >&2
        exit 1
    fi
}
BASE_PORT_FRONT=10021
BASE_PORT_BACK=10022
DEPLOY_BASE="$(pwd)"

ENV="prod"
GITBRANCH="main"
REBUILD=0
DOWN=0
DIAG_ONLY=0
PULL_ONLY=0
ROTATE_JWT=0
FORCE=0
WIPE=0
AGENT_BUILD=0

show_help() {
    cat <<HELP
Usage: sh deploy.sh [OPTIONS]

Options:
  -e accept|prod   Target environment (default: prod)
  -b BRANCH        Git branch to deploy (default: main)
  -r               Rebuild Docker images without cache (DB/data preserved;
                   -w is the ONLY flag that destroys data)
  -d               Stop containers (DB/data preserved)
  -w               WIPE every Docker artifact for this env (containers, volumes,
                   networks, locally-built images). Targets only resources
                   labeled for the "$APPNAME-$ENV" compose project - does NOT
                   touch other workloads on this host. Requires -f to skip
                   the confirmation prompt.
  -x               Run runtime diagnostics only (no deploy)
  -p               Git pull only (no Docker build/restart)
  -k               Regenerate JWT key
  -f               Force deploy even if no git changes (also skips -w confirmation)
  -a               Agent build: bump only the 4th version part (1.2.3 -> 1.2.3.1)
                   instead of the patch. Use for deploys that only change agent
                   code. A subsequent normal deploy resets the 4th part.
  -h               Show this help message
HELP
}

while getopts "e:b:rdwxpkfah" opt; do
    case $opt in
        e) ENV="$OPTARG" ;;
        b) GITBRANCH="$OPTARG" ;;
        r) REBUILD=1 ;;
        d) DOWN=1 ;;
        w) WIPE=1 ;;
        x) DIAG_ONLY=1 ;;
        p) PULL_ONLY=1 ;;
        k) ROTATE_JWT=1 ;;
        f) FORCE=1 ;;
        a) AGENT_BUILD=1 ;;
        h) show_help; exit 0 ;;
        *) show_help; exit 1 ;;
    esac
done

increment_patch() {
    _ver="$1"
    _major=$(echo "$_ver" | cut -d. -f1)
    _minor=$(echo "$_ver" | cut -d. -f2)
    _patch=$(echo "$_ver" | cut -d. -f3)
    _patch=$((_patch + 1))
    # Emits only major.minor.patch - any 4th (agent) part present in the input
    # is dropped, so a normal deploy after an -a deploy resets the agent part.
    echo "${_major}.${_minor}.${_patch}"
}

# Bumps the 4th (agent-build) part for -a deploys: 1.2.3 -> 1.2.3.1,
# 1.2.3.1 -> 1.2.3.2. Leaves major.minor.patch untouched.
increment_agent_part() {
    _ver="$1"
    _major=$(echo "$_ver" | cut -d. -f1)
    _minor=$(echo "$_ver" | cut -d. -f2)
    _patch=$(echo "$_ver" | cut -d. -f3)
    _agent=$(echo "$_ver" | cut -d. -f4)
    if [ -z "$_agent" ]; then
        _agent=1
    else
        _agent=$((_agent + 1))
    fi
    echo "${_major}.${_minor}.${_patch}.${_agent}"
}

# =========================================
# Helper: print compose diagnostics
# =========================================
print_runtime_diagnostics() {
    echo ""
    echo ">>> Runtime diagnostics (docker compose ps):"
    docker compose \
        --env-file "$ENV_FILE" \
        -f deploy/compose/remote.compose.yml \
        -p "$PROJECT_NAME" \
        ps 2>&1 || true

    echo ""
    echo ">>> Backend logs (last 120 lines):"
    docker compose \
        --env-file "$ENV_FILE" \
        -f deploy/compose/remote.compose.yml \
        -p "$PROJECT_NAME" \
        logs --tail 120 back 2>&1 || true

    echo ""
    echo ">>> Database logs (last 120 lines):"
    docker compose \
        --env-file "$ENV_FILE" \
        -f deploy/compose/remote.compose.yml \
        -p "$PROJECT_NAME" \
        logs --tail 120 database 2>&1 || true

    echo ""
    echo ">>> Front logs (last 120 lines):"
    docker compose \
        --env-file "$ENV_FILE" \
        -f deploy/compose/remote.compose.yml \
        -p "$PROJECT_NAME" \
        logs --tail 120 front 2>&1 || true
}

# =========================================
# Helper: print config sanity table
# =========================================
# Printed for every mode (down / wipe / diag / deploy) right after the env file
# is loaded or created. A blank ENCRYPTION_KEY/SALT or JWT_KEY crash-loops the
# backend (Program.cs guard); showing it up-front turns a silent 2-hour restart
# loop into an obvious one-line diagnostic. Secrets are never echoed - only
# presence is reported.
print_config_sanity() {
    echo ""
    echo ">>> Config sanity ($ENV_FILE):"
    _cfg_row() {
        # $1 = label, $2 = value-or-empty, $3 = hint shown only when missing
        if [ -n "$2" ]; then
            printf '  %-16s: loaded\n' "$1"
        else
            printf '  %-16s: *** MISSING ***%s\n' "$1" "$3"
        fi
    }
    _cfg_row "DB password"     "${DB_PASSWORD:-}"     " (DB auth will fail)"
    _cfg_row "JWT key"         "${JWT_KEY:-}"         " (Auth__JwtKey blank -> backend won't start)"
    _cfg_row "Encryption key"  "${ENCRYPTION_KEY:-}"  " (Auth__EncryptionKey blank -> backend crash-loop)"
    _cfg_row "Encryption salt" "${ENCRYPTION_SALT:-}" " (Auth__EncryptionSalt blank -> backend crash-loop)"
    _cfg_row "Admin password"  "${ADMIN_PASSWORD:-}"  " (first-run admin seed will fail)"
    printf '  %-16s: %s\n' "Version" "${APP_VERSION:-unknown}"
}

# =========================================
# Helper: ensure a secret exists in the env file
# =========================================
# Generates a secret and persists it to $ENV_FILE when the named variable is
# missing OR empty. A valid, non-empty value is left untouched, so existing
# AES-encrypted data and live JWTs are never invalidated by an unwanted
# rotation. Any pre-existing (e.g. empty) line for the variable is stripped
# before the generated line is appended, so the file never ends up with a
# duplicate entry that would shadow the generated value.
#   $1 = variable name (e.g. JWT_KEY)
#   $2 = shell command that prints a fresh secret on stdout
#   $3 = label for the log line (e.g. "JWT key")
ensure_env_secret() {
    _name="$1"
    eval "_cur=\${$_name:-}"
    if [ -n "$_cur" ]; then
        return 0
    fi
    _val=$(eval "$2")
    eval "$_name=\$_val"
    sed -i "/^${_name}=/d" "$ENV_FILE"
    env_line "$_name" "$_val" >> "$ENV_FILE"
    printf '  %-16s: GENERATED (persisted to %s)\n' "$3" "$ENV_FILE"
}

# Write and read environment values as literal data so metacharacters cannot execute or change
# stored bytes; escape single-quote cases for Compose and export the exact values for interpolation.
# See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
env_line() {
    case "$2" in
        *"'"*) printf '%s="%s"\n' "$1" "$(printf '%s' "$2" | sed 's/[\\"$`]/\\&/g')" ;;
        *)     printf "%s='%s'\n" "$1" "$2" ;;
    esac
}

# env_get NAME prints the value of the last NAME= line of $ENV_FILE. Exit 1 when there is none, 2
# when the line cannot be read as data: an unescaped $ or ` in "..." was expanded by the former
# sourcing, so the value in use may differ from the text, and guessing it would change a credential.
# "..." follows the shell's rules otherwise (\\ \" \$ \` are escapes, any other \ is kept), so a line
# an older deploy.sh wrote reads back as the value it had.
env_get() {
    awk -v key="$1" -v sq="'" '
        { sub(/\r$/, "") }
        /^[ \t]*(#|$)/ { next }
        index($0, "=") == 0 { next }
        substr($0, 1, index($0, "=") - 1) != key { next }
        {
            raw = substr($0, index($0, "=") + 1)
            found = 1; ok = 1; out = ""
            q = substr(raw, 1, 1)
            if (q == sq) {
                inner = substr(raw, 2, length(raw) - 2)
                if (length(raw) < 2 || substr(raw, length(raw), 1) != sq || index(inner, sq) > 0) ok = 0
                out = inner
            } else if (q == "\"") {
                inner = substr(raw, 2, length(raw) - 2)
                if (length(raw) < 2 || substr(raw, length(raw), 1) != "\"") ok = 0
                for (i = 1; ok && i <= length(inner); i++) {
                    c = substr(inner, i, 1)
                    if (c == "\\") {
                        n = substr(inner, i + 1, 1)
                        if (n == "") ok = 0
                        else if (n == "\\" || n == "\"" || n == "$" || n == "`") { out = out n; i++ }
                        else out = out c
                    } else if (c == "$" || c == "`" || c == "\"") ok = 0
                    else out = out c
                }
            } else {
                if (raw !~ /^[A-Za-z0-9._+\/=:@%,-]*$/) ok = 0
                out = raw
            }
            value = out; valid = ok
        }
        END {
            if (!found) exit 1
            if (!valid) exit 2
            printf "%s", value
        }
    ' "$ENV_FILE"
}

# Reads the values this script uses from $ENV_FILE; any other line is left to docker compose.
load_env_file() {
    for _key in DB_PASSWORD ADMIN_PASSWORD JWT_KEY ENCRYPTION_KEY ENCRYPTION_SALT APP_VERSION; do
        if _val="$(env_get "$_key")"; then
            eval "$_key=\$_val"
        elif [ "$?" -ne 1 ]; then
            echo "ERROR: $_key in $ENV_FILE cannot be read as a value: an unescaped \$ or \` was expanded" >&2
            echo "       when the file was sourced, or the quoting is incomplete. Rewrite the line as" >&2
            echo "       $_key='<value>' with the exact value in use, then re-run. Nothing was changed." >&2
            exit 1
        fi
    done
}

DB_USER="db${APPNAME}"
REPO_DIR="${DEPLOY_BASE}/${APPNAME}"
PROJECT_NAME="${APPNAME}-${ENV}"
ENV_FILE="${DEPLOY_BASE}/.env-${ENV}"

# Both required here rather than at the top: -h and the diagnostic-only paths must stay usable on a
# machine that has neither, and refusing them for a value they never read would be noise.
require_env SERVERNAME
require_env GITREMOTE

FRONT_URL_ACCEPT="https://${APPNAME}-accept.${SERVERNAME}"
FRONT_URL_PROD="https://${APPNAME}.${SERVERNAME}"

if [ "$ENV" = "prod" ]; then
    FRONT_URL="$FRONT_URL_PROD"
    API_BASE_URL="https://${APPNAME}-api.${SERVERNAME}"
    PORT_FRONT=$((BASE_PORT_FRONT + 4))
    PORT_BACK=$((BASE_PORT_BACK + 4))
else
    ENV="accept"
    FRONT_URL="$FRONT_URL_ACCEPT"
    API_BASE_URL="https://${APPNAME}-accept-api.${SERVERNAME}"
    PORT_FRONT=$BASE_PORT_FRONT
    PORT_BACK=$BASE_PORT_BACK
fi

echo ""
echo "============================================="
echo "  Aetheus - Deployment ($ENV)"
echo "============================================="
echo "  App        : $APPNAME"
echo "  Env        : $ENV"
echo "  Branch     : $GITBRANCH"
echo "  Front URL  : $FRONT_URL"
echo "  API URL    : $API_BASE_URL"
echo "  Port Front : $PORT_FRONT"
echo "  Port Back  : $PORT_BACK"
echo ""

# Export base compose variables early so all modes (down/diag/deploy) resolve interpolation.
export APPNAME ENV PORT_FRONT PORT_BACK DB_USER
export FRONT_URL API_BASE_URL FRONT_URL_ACCEPT FRONT_URL_PROD


if [ "$PULL_ONLY" = "1" ]; then
    echo ">>> Pull-only mode (-p): syncing git repository only"
    if [ -d "$REPO_DIR/.git" ]; then
        cd "$REPO_DIR"
        git fetch origin
        if git show-ref --verify --quiet "refs/heads/$GITBRANCH"; then
            git checkout "$GITBRANCH"
        else
            git checkout -B "$GITBRANCH" "origin/$GITBRANCH"
        fi
        git pull origin "$GITBRANCH"
        echo ">>> Pull complete ($(git rev-parse --short HEAD))."
    else
        git clone -b "$GITBRANCH" "$GITREMOTE" "$REPO_DIR"
        echo ">>> Repository cloned in pull-only mode ($(cd "$REPO_DIR" && git rev-parse --short HEAD))."
    fi
    exit 0
fi

if [ -f "$ENV_FILE" ]; then
    echo ">>> Loading config from $ENV_FILE"
    load_env_file
    if [ "$DIAG_ONLY" = "1" ]; then
        # Read-only: no mutation. Config presence is reported by
        # print_config_sanity below (runs for every mode).
        :
    else
        # -k: explicit JWT rotation - force a fresh key even if a valid one
        # exists (invalidates live sessions by design). Delete + append so it
        # works whether or not a JWT_KEY line is already present.
        if [ "$ROTATE_JWT" = "1" ]; then
            JWT_KEY=$(head -c 48 /dev/urandom | base64 | tr -d '/+=')
            sed -i '/^JWT_KEY=/d' "$ENV_FILE"
            env_line JWT_KEY "$JWT_KEY" >> "$ENV_FILE"
            printf '  %-16s: ROTATED (-k)\n' "JWT key"
        fi
        # Self-heal: generate any secret that is missing OR empty and persist
        # it, so a stale / hand-edited .env can never crash-loop the backend
        # (blank Auth__JwtKey / Auth__EncryptionKey / Auth__EncryptionSalt all
        # abort startup). A valid value is left untouched (no unwanted rotation).
        ensure_env_secret JWT_KEY         'head -c 48 /dev/urandom | base64 | tr -d "/+="' "JWT key"
        ensure_env_secret ENCRYPTION_KEY  'head -c 48 /dev/urandom | base64 | tr -d "/+="' "Encryption key"
        ensure_env_secret ENCRYPTION_SALT 'head -c 24 /dev/urandom | base64'               "Encryption salt"
        if [ -n "$APP_VERSION" ]; then
            if [ "$AGENT_BUILD" = "1" ]; then
                NEW_VERSION=$(increment_agent_part "$APP_VERSION")
                _bump_note="agent build, -a"
            else
                NEW_VERSION=$(increment_patch "$APP_VERSION")
                _bump_note="auto-incremented"
            fi
            sed -i "s|^APP_VERSION=.*|APP_VERSION=\"${NEW_VERSION}\"|" "$ENV_FILE"
            APP_VERSION="$NEW_VERSION"
            echo "  Version     : $APP_VERSION ($_bump_note)"
        fi
    fi
else
    if [ "$DIAG_ONLY" = "1" ]; then
        echo "ERROR: Diagnostics mode (-x) requires an existing env file: $ENV_FILE"
        echo "Run a deployment first to create it."
        exit 1
    fi
    echo ">>> Creating $ENV_FILE"
    printf "DB password for %s: " "$DB_USER"
    # read -r keeps every byte the operator typed: a backslash is part of the password.
    stty -echo; IFS= read -r DB_PASSWORD; stty echo; printf "\n"
    [ -z "$DB_PASSWORD" ] && echo "ERROR: empty password" && exit 1

    printf "Admin account password (admin): "
    stty -echo; IFS= read -r ADMIN_PASSWORD; stty echo; printf "\n"
    [ -z "$ADMIN_PASSWORD" ] && echo "ERROR: empty admin password" && exit 1

    printf "Initial version (e.g. 1.0.0): "
    IFS= read -r APP_VERSION
    [ -z "$APP_VERSION" ] && echo "ERROR: empty version" && exit 1

    JWT_KEY=$(head -c 48 /dev/urandom | base64 | tr -d '/+=')
    ENCRYPTION_KEY=$(head -c 48 /dev/urandom | base64 | tr -d '/+=')
    ENCRYPTION_SALT=$(head -c 24 /dev/urandom | base64)

    # Created 0600 before the first secret is written into it.
    (umask 077 && {
        env_line DB_PASSWORD "$DB_PASSWORD"
        env_line ADMIN_PASSWORD "$ADMIN_PASSWORD"
        env_line JWT_KEY "$JWT_KEY"
        env_line ENCRYPTION_KEY "$ENCRYPTION_KEY"
        env_line ENCRYPTION_SALT "$ENCRYPTION_SALT"
        env_line APP_VERSION "$APP_VERSION"
    } > "$ENV_FILE")
    chmod 600 "$ENV_FILE"
    echo "  Config saved to $ENV_FILE"
fi

# Export secret/runtime variables after loading/creating environment file.
export DB_PASSWORD JWT_KEY ENCRYPTION_KEY ENCRYPTION_SALT ADMIN_PASSWORD APP_VERSION

# Consolidated config sanity for every mode (down / wipe / diag / deploy).
print_config_sanity

if [ "$DOWN" = "1" ]; then
    echo ">>> Stopping containers..."
    cd "$REPO_DIR"
    docker compose \
        --env-file "$ENV_FILE" \
        -f deploy/compose/remote.compose.yml \
        -p "$PROJECT_NAME" \
        down
    echo "Containers stopped."
    exit 0
fi

# --- Wipe mode: remove every Docker artifact for this compose project ---
# Scope is strictly limited to resources carrying the compose label
# "com.docker.compose.project=$PROJECT_NAME". Other projects on the same
# Docker host are never touched - label filtering is a hard guarantee.
if [ "$WIPE" = "1" ]; then
    echo ""
    echo ">>> WIPE mode for project '$PROJECT_NAME'"
    echo "    Will remove:"
    echo "      - containers  (label: com.docker.compose.project=$PROJECT_NAME)"
    echo "      - volumes     (label + name fallback '${APPNAME}-${ENV}-*')"
    echo "      - networks    (label: com.docker.compose.project=$PROJECT_NAME)"
    echo "      - locally-built images for this project (not pulled images)"
    echo "    Will NOT touch: other compose projects, standalone containers,"
    echo "                    registry-pulled base images (postgres:*, etc.)."
    echo ""

    if [ "$FORCE" = "0" ]; then
        printf "Type 'WIPE' to confirm: "
        read _confirm
        if [ "$_confirm" != "WIPE" ]; then
            echo "Aborted."
            exit 0
        fi
    fi

    # 1. docker compose down does the heavy lifting when the repo + compose file are present.
    #    -v removes named volumes declared in the compose file.
    #    --rmi local removes images that were built by compose (not pulled).
    #    --remove-orphans cleans containers that were part of a previous compose version.
    if [ -d "$REPO_DIR" ] && [ -f "$REPO_DIR/deploy/compose/remote.compose.yml" ]; then
        echo ">>> docker compose down -v --rmi local --remove-orphans..."
        cd "$REPO_DIR"
        docker compose \
            --env-file "$ENV_FILE" \
            -f deploy/compose/remote.compose.yml \
            -p "$PROJECT_NAME" \
            down -v --rmi local --remove-orphans 2>&1 || true
        cd "$DEPLOY_BASE"
    else
        echo ">>> Compose file unavailable - skipping compose down, using label filters only."
    fi

    # 2. Belt-and-suspenders: any container still labeled for the project.
    _containers=$(docker ps -aq --filter "label=com.docker.compose.project=${PROJECT_NAME}" 2>/dev/null || true)
    if [ -n "$_containers" ]; then
        echo ">>> Removing $(echo "$_containers" | wc -l) leftover container(s)..."
        # shellcheck disable=SC2086
        docker rm -f $_containers 2>/dev/null || true
    fi

    # 3. Volumes: by label first, then by name pattern for volumes that predate labels.
    _volumes=$(docker volume ls -q --filter "label=com.docker.compose.project=${PROJECT_NAME}" 2>/dev/null || true)
    if [ -n "$_volumes" ]; then
        echo ">>> Removing $(echo "$_volumes" | wc -l) labeled volume(s)..."
        # shellcheck disable=SC2086
        docker volume rm $_volumes 2>/dev/null || true
    fi
    # Name-pattern fallback. Matches "$APPNAME-$ENV-anything" to avoid blasting
    # volumes that just happen to start with "aetheus".
    _named_volumes=$(docker volume ls -q 2>/dev/null | grep -E "^${APPNAME}-${ENV}-" || true)
    if [ -n "$_named_volumes" ]; then
        echo ">>> Removing $(echo "$_named_volumes" | wc -l) volume(s) matching '${APPNAME}-${ENV}-*'..."
        # shellcheck disable=SC2086
        docker volume rm $_named_volumes 2>/dev/null || true
    fi

    # 4. Networks created by compose for the project.
    _networks=$(docker network ls -q --filter "label=com.docker.compose.project=${PROJECT_NAME}" 2>/dev/null || true)
    if [ -n "$_networks" ]; then
        echo ">>> Removing $(echo "$_networks" | wc -l) labeled network(s)..."
        # shellcheck disable=SC2086
        docker network rm $_networks 2>/dev/null || true
    fi

    # 5. Images: locally-built images for this project. --rmi local in step 1
    #    normally handles this, but if compose down fell through above we catch
    #    them by tag pattern. Label filter on images is unreliable across
    #    compose versions, so we use the tag shape compose uses by default:
    #    "<project>-<service>[:tag]" → "aetheus-prod-back", etc.
    _images=$(docker images --format '{{.Repository}}:{{.Tag}}' 2>/dev/null \
              | grep -E "^${PROJECT_NAME}-" || true)
    if [ -n "$_images" ]; then
        echo ">>> Removing $(echo "$_images" | wc -l) image(s) matching '${PROJECT_NAME}-*'..."
        # shellcheck disable=SC2086
        docker rmi $_images 2>/dev/null || true
    fi

    echo ""
    echo "============================================="
    echo "  WIPE complete for env '$ENV' (project '$PROJECT_NAME')"
    echo "  Other Docker workloads on this host are intact."
    echo "============================================="
    exit 0
fi

# --- Diagnostics only mode ---
if [ "$DIAG_ONLY" = "1" ]; then
    if [ ! -d "$REPO_DIR" ]; then
        echo "ERROR: Repository directory not found: $REPO_DIR"
        echo "Run a deployment first, then use -x for diagnostics."
        exit 1
    fi
    echo ">>> Diagnostics-only mode (-x)"
    cd "$REPO_DIR"
    print_runtime_diagnostics

    # Require a reachable Docker daemon and a nonempty healthy stack so diagnostic exit codes are
    # reliable for monitoring. See docs/retrospectives/RET-001-erreurs-pipelines-aetheus.md.
    echo ""
    if ! _states=$(docker ps -a \
        --filter "label=com.docker.compose.project=${PROJECT_NAME}" \
        --format '{{.Names}}	{{.Status}}' 2>&1); then
        echo ">>> HEALTH: UNKNOWN - docker ps failed, the stack state could not be read:"
        printf '%s\n' "$_states" | sed 's/^/    /'
        exit 2
    fi
    _absent=""
    for _service in database back front; do
        printf '%s\n' "$_states" | grep -q "^${APPNAME}-${ENV}-${_service}	" || _absent="${_absent} ${APPNAME}-${ENV}-${_service}"
    done
    if [ -n "$_absent" ]; then
        echo ">>> HEALTH: FAIL - container(s) of ${PROJECT_NAME} not found:${_absent}"
        exit 1
    fi
    _unhealthy=$(printf '%s\n' "$_states" | grep -Ei 'restarting|unhealthy|exited|dead' || true)
    if [ -n "$_unhealthy" ]; then
        echo ">>> HEALTH: FAIL - unhealthy container(s):"
        echo "$_unhealthy" | sed 's/^/    /'
        exit 1
    fi
    echo ">>> HEALTH: OK - no Restarting/unhealthy/Exited containers."
    exit 0
fi

# --- Deploy mode: hard gate on non-self-healing secrets ---
# Reached only on a real deploy (down / wipe / diag / pull-only have all
# exited above). Unlike JWT/encryption, DB_PASSWORD and ADMIN_PASSWORD have
# no safe auto-generated default - DB_PASSWORD must match the existing
# Postgres data volume, ADMIN_PASSWORD is an operator choice - so they cannot
# self-heal. print_config_sanity already flagged them; abort here, before the
# git pull and multi-minute image build, instead of building an image that
# can only crash-loop.
_missing=""
if [ -z "${DB_PASSWORD:-}" ]; then _missing="${_missing} DB_PASSWORD"; fi
if [ -z "${ADMIN_PASSWORD:-}" ]; then _missing="${_missing} ADMIN_PASSWORD"; fi
if [ -n "$_missing" ]; then
    echo ""
    echo "FATAL: required secret(s) missing/blank in $ENV_FILE:${_missing}" >&2
    echo "       These have no safe default and are never auto-generated." >&2
    echo "       Restore them in $ENV_FILE (DB_PASSWORD MUST match the" >&2
    echo "       existing '${APPNAME}-${ENV}-db-data' volume's password)," >&2
    echo "       then re-run. Deleting $ENV_FILE re-prompts interactively" >&2
    echo "       but only works against a fresh database volume." >&2
    exit 1
fi

if [ -d "$REPO_DIR/.git" ]; then
    cd "$REPO_DIR"
    git fetch origin
    CURRENT=$(git rev-parse HEAD)
    REMOTE=$(git rev-parse "origin/$GITBRANCH")
    if [ "$CURRENT" = "$REMOTE" ] && [ "$FORCE" = "0" ] && [ "$REBUILD" = "0" ]; then
        echo ">>> No changes detected. Use -f to force."
        exit 0
    fi
    git checkout "$GITBRANCH"
    git pull origin "$GITBRANCH"
else
    git clone -b "$GITBRANCH" "$GITREMOTE" "$REPO_DIR"
    cd "$REPO_DIR"
fi
SOURCE_COMMIT=$(git rev-parse HEAD)
export SOURCE_COMMIT

echo ">>> Building and starting containers..."

# Enable BuildKit so Dockerfile cache mounts (--mount=type=cache) actually work.
# Without these, NuGet packages are re-downloaded on every build.
export DOCKER_BUILDKIT=1
export COMPOSE_DOCKER_CLI_BUILD=1

COMPOSE_CMD="docker compose --env-file $ENV_FILE -f deploy/compose/remote.compose.yml -p $PROJECT_NAME"

# =========================================
# Pre-migration database backup (pg_dump)
# =========================================
# The backend runs MigrateAsync() on startup, which applies pending EF
# migrations. A pg_dump snapshot BEFORE bringing up the new image ensures
# a rollback path if a migration fails or corrupts data. The dump is only
# attempted when the database container is already running (i.e. not a
# first-ever deploy). Backups are stored in deploy/backups/ with a
# timestamped filename; the 10 most recent are kept, older ones pruned.
DB_CONTAINER="${APPNAME}-${ENV}-database"
BACKUP_DIR="${DEPLOY_BASE}/backups"
if docker ps --format '{{.Names}}' 2>/dev/null | grep -qF "$DB_CONTAINER"; then
    echo ">>> Pre-migration pg_dump (container: $DB_CONTAINER)..."
    mkdir -p "$BACKUP_DIR"
    BACKUP_FILE="${BACKUP_DIR}/${APPNAME}-${ENV}-$(date +%Y%m%d-%H%M%S).sql.gz"
    BACKUP_TMP="${BACKUP_FILE%.sql.gz}.sql.tmp"
    # A piped `pg_dump | gzip` only reports gzip's exit status: a failed dump
    # would still print "Backup OK" over an empty archive. Dump to a temp file
    # first (testing pg_dump's OWN exit code and that the dump is non-empty),
    # then compress.
    if docker exec "$DB_CONTAINER" \
        pg_dump -U "${DB_USER}" -d "${APPNAME}" --no-owner --no-acl \
        > "$BACKUP_TMP" 2>/dev/null \
        && [ -s "$BACKUP_TMP" ] \
        && gzip -c "$BACKUP_TMP" > "$BACKUP_FILE"; then
        rm -f "$BACKUP_TMP"
        BACKUP_SIZE=$(du -h "$BACKUP_FILE" 2>/dev/null | cut -f1)
        echo "  Backup OK: $BACKUP_FILE ($BACKUP_SIZE)"
        # Prune old backups, keep 10 most recent
        ls -1t "$BACKUP_DIR"/${APPNAME}-${ENV}-*.sql.gz 2>/dev/null \
            | tail -n +11 \
            | xargs rm -f 2>/dev/null || true
    else
        echo "  FATAL: pg_dump FAILED - refusing to deploy without a verified pre-migration backup" >&2
        rm -f "$BACKUP_TMP" "$BACKUP_FILE" 2>/dev/null || true
        exit 1
    fi
else
    echo ">>> Skipping pre-migration backup (database container not running - first deploy?)"
fi

if [ "$REBUILD" = "1" ]; then
    # -r rebuilds images from scratch. It MUST NOT destroy data: `down` (no -v)
    # so every named volume - including ${APPNAME}-${ENV}-db-data - survives.
    # A destructive database reset is performed ONLY by -w (WIPE), which is
    # explicit and confirmation-gated. Never add `down -v` or `docker volume rm`
    # to this path.
    echo ">>> Stopping containers (volumes/data preserved), rebuilding images (no cache)..."
    $COMPOSE_CMD down 2>/dev/null || true
    $COMPOSE_CMD build --no-cache --parallel
fi

# Build front + back in parallel before bringing the stack up. This roughly
# halves wall-clock build time since the two images share no layers.
echo ">>> Building images (parallel)..."
$COMPOSE_CMD build --parallel

$COMPOSE_CMD up -d

echo ""
echo ">>> Waiting for health check..."
HEALTH_URL="http://127.0.0.1:${PORT_BACK}/health/ready"
HEALTHY=0
# Budget must exceed the compose healthcheck's own tolerance (start_period 120s + retries*interval,
# ~210s in remote.compose.yml) so a slow first-migration / JIT cold start is not falsely reported as
# a failed deploy while compose still considers the container inside its start_period.
HEALTH_BUDGET_SECONDS="${HEALTH_BUDGET_SECONDS:-240}"
for i in $(seq 1 "$HEALTH_BUDGET_SECONDS"); do
    if curl -sf --max-time 2 "$HEALTH_URL" > /dev/null 2>&1; then
        echo "  Health OK after ${i}s"
        HEALTHY=1
        break
    fi
    sleep 1
done

if [ "$HEALTHY" = "0" ]; then
    echo "  ERROR: Health check timeout (${HEALTH_BUDGET_SECONDS}s) - backend never became healthy"
    echo ""
    echo "============================================="
    echo "  DEPLOYED BUT UNHEALTHY"
    echo "  Containers are up but $HEALTH_URL never answered."
    echo "  Inspect: $COMPOSE_CMD logs back"
    echo "============================================="
    exit 1
fi

echo ""
echo "============================================="
echo "  Deployment complete!"
echo "  Front: $FRONT_URL"
echo "  API:   $API_BASE_URL"
echo "  Version: $APP_VERSION"
echo "============================================="
