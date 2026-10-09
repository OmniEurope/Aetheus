#!/usr/bin/env bash
# launch-linux.sh - Build and launch Aetheus backend + frontend + agent (Linux)
# Pendant Linux de launch-windows.ps1

set -u
set -o pipefail

# ============================================================
#  Colors
# ============================================================
if [[ -t 1 ]]; then
    C_RESET=$'\033[0m'
    C_CYAN=$'\033[36m'
    C_YELLOW=$'\033[33m'
    C_GREEN=$'\033[32m'
    C_RED=$'\033[31m'
    C_MAGENTA=$'\033[35m'
    C_WHITE=$'\033[37m'
    C_DGRAY=$'\033[90m'
    C_DCYAN=$'\033[36m'
    C_DGREEN=$'\033[32m'
    C_DYELLOW=$'\033[33m'
else
    C_RESET=""; C_CYAN=""; C_YELLOW=""; C_GREEN=""; C_RED="";
    C_MAGENTA=""; C_WHITE=""; C_DGRAY=""; C_DCYAN=""; C_DGREEN=""; C_DYELLOW="";
fi

write_step() {
    echo ""
    echo "${C_CYAN}========================================${C_RESET}"
    echo "${C_CYAN}  $1${C_RESET}"
    echo "${C_CYAN}========================================${C_RESET}"
}

# ============================================================
#  Flags (defaults)
# ============================================================
BUILD=0
COVERAGE=0
DEPLOY_AGENT=0
DEPLOY_AGENT_UPGRADE=0
GIT_PUSH=0
HELP=0
HELP_LONG=0
HOT_RELOAD=0
MODE_AGENT=0
MODE_FRONT=0
REMOTE_AGENT=""
RESET=0
SILENT=0
TEST_ALL=0
TEST_UNIT=0
TEST_BACK=0
TEST_AGENT=0
TEST_ANALYZERS=0
TEST_E2E=0
TEST_E2E_FILTER=""
TEST_E2E_FILTER_SET=0
TEST_FRONT=0
TEST_INTEGRATION=0

show_options() {
    echo ""
    echo "${C_CYAN}launch-linux.sh - Build and launch Aetheus (Linux)${C_RESET}"
    echo ""
    echo "${C_YELLOW}USAGE:${C_RESET}"
    echo "  ./launch-linux.sh [OPTIONS]"
    echo ""
    echo "${C_YELLOW}OPTIONS:${C_RESET}"
    echo "  -b,    --build                  Build all projects and exit"
    echo "  -c,    --coverage               Release coverage for Back + Front + Agent.Core, gated at 80.1%"
    echo "  -da,   --deploy-agent [ver]     Build agent + fresh install"
    echo "                                  If .remoteagent has a target: SCP tarball + SSH install."
    echo "                                  Otherwise (or empty Enter at prompt): install on THIS machine."
    echo "                                  Version resolution (in order): CLI arg, \$APP_VERSION,"
    echo "                                  APP_VERSION in .env-prod (shared with deploy.sh), 1.0.0."
    echo "                                  Override env path: DEPLOY_ENV_FILE=/path/.env-prod"
    echo "  -dau,  --deploy-agent-upgrade [ver]"
    echo "                                  Same as -da but runs install-agent-linux.sh --upgrade"
    echo "                                  (preserves appsettings.json + work dir)."
    echo "                                  Auto-increments patch in .env-prod unless ver is given."
    echo "  -gpush, --git-push              Stage all + commit .gitmessage + git push"
    echo "  -h,    --help                   Show available options"
    echo "  -hl,   --help-long              Show detailed help with examples"
    echo "  -hr,   --hot-reload             Start servers with dotnet watch (hot reload)"
    echo "  -ma,   --mode-agent             Start Agent Worker Service (Linux)"
    echo "  -mf,   --mode-front             Start Backend + Frontend (default)"
    echo "  -r,    --reset                  Drop & recreate local Postgres docker volume (fresh seed)"
    echo "  -ra,   --remote-agent [target]  SSH tunnel for remote agent (fwd port 5300)"
    echo "                                  Target: user@host or user@host:sshport"
    echo "                                  Saved in .remoteagent; prompted if absent"
    echo "  -s,    --silent                 Start servers without opening browser"
    echo "  -t,    --test-unit              Run ALL unit tests (back + front + agent-core + analyzers)"
    echo "  -ta,   --test-all               Run EVERYTHING: unit + integration + E2E"
    echo "  -tb,   --test-back              Run backend unit tests"
    echo "  -te,   --test-e2e               Run ALL E2E tests"
    echo "  -tec <filter>                   Run specific E2E categories (implies -te)"
    echo "    -tec ?                        Show interactive category picker"
    echo "    -tec 1                        Category by number"
    echo "    -tec 1,2,3                    Categories by number"
    echo "    -tec Auth                     Category by name"
    echo "    -tec Auth,Acts,3              Mix of names and numbers"
    echo "  -tf,   --test-front             Run frontend unit tests"
    echo "  -ti,   --test-integration       Run integration tests (Aetheus.Back.IntegrationTests, Testcontainers)"
    echo ""
}

show_help_long() {
    show_options
    echo "${C_YELLOW}EXAMPLES:${C_RESET}"
    echo "  ./launch-linux.sh                    Build + start servers + open browser"
    echo "  ./launch-linux.sh -b                 Build all projects + exit"
    echo "  ./launch-linux.sh -c                 Build + back + front tests + coverage report + exit"
    echo "  ./launch-linux.sh -da                Build agent + install (remote if .remoteagent set, else local)"
    echo "  ./launch-linux.sh -da 1.0.51         Build agent v1.0.51 + install (remote or local auto)"
    echo "  ./launch-linux.sh -dau               Build agent + upgrade install (remote or local auto)"
    echo "  ./launch-linux.sh -dau 1.0.51        Build agent v1.0.51 + upgrade install (remote or local auto)"
    echo "  ./launch-linux.sh -gpush             Stage all + commit .gitmessage + push"
    echo "  ./launch-linux.sh -hr                Start servers with hot reload (dotnet watch)"
    echo "  ./launch-linux.sh -ma                Build + start Agent only (Linux)"
    echo "  ./launch-linux.sh -mf -ma            Build + start Backend + Frontend + Agent"
    echo "  ./launch-linux.sh -r                 Reset DB + build + start servers"
    echo "  ./launch-linux.sh -ra                Start servers + SSH tunnel (reads .remoteagent)"
    echo "  ./launch-linux.sh -ra user@srv       Start servers + SSH tunnel (saves target)"
    echo "  ./launch-linux.sh -s                 Build + start servers (no browser)"
    echo "  ./launch-linux.sh -t                 Build + ALL unit tests (back/front/agent-core/analyzers) + exit"
    echo "  ./launch-linux.sh -ta                Build + EVERYTHING (unit + integration + E2E) + exit"
    echo "  ./launch-linux.sh -tb                Build + backend tests only + exit"
    echo "  ./launch-linux.sh -tb -tf            Build + back + front tests + exit"
    echo "  ./launch-linux.sh -te                Build + ALL E2E tests on an isolated dedicated DB (dev DB untouched) + exit"
    echo "  ./launch-linux.sh -tec ?             Build + E2E category picker (interactive)"
    echo "  ./launch-linux.sh -tec 1             Build + E2E category 1 only"
    echo "  ./launch-linux.sh -tec 1,2,3         Build + categories 1 + 2 + 3"
    echo "  ./launch-linux.sh -tec Auth          Build + Auth category only"
    echo "  ./launch-linux.sh -tf                Build + frontend tests only + exit"
    echo "  ./launch-linux.sh -ti                Build + integration tests only + exit"
    echo ""
    echo "${C_YELLOW}WORKFLOW:${C_RESET}"
    echo "  1. Kills existing Aetheus processes (ports 5301/5401)"
    echo "  2. Builds the solution (skipped with -hr, or done alone with -b)"
    echo "  3. Runs requested tests (if any test flag is set)"
    echo "  4. Starts services based on mode flags (-mf: Back+Front, -ma: Agent)"
    echo "  5. Opens browser to https://localhost:5401 (when -mf)"
    echo "  6. Streams server output until Ctrl+C"
    echo ""
}

# ============================================================
#  Parse args (short + long, allow bundled/combined)
# ============================================================
AGENT_VERSION=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        -b|--build)             BUILD=1 ;;
        -c|--coverage)          COVERAGE=1 ;;
        -da|--deploy-agent)
            DEPLOY_AGENT=1
            # Optional version: -da 1.0.51 or just -da
            if [[ -n "${2:-}" && "$2" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
                AGENT_VERSION="$2"
                shift
            fi
            ;;
        -dau|--deploy-agent-upgrade)
            DEPLOY_AGENT_UPGRADE=1
            if [[ -n "${2:-}" && "$2" =~ ^[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
                AGENT_VERSION="$2"
                shift
            fi
            ;;
        -gpush|--git-push)      GIT_PUSH=1 ;;
        -h|--help)              HELP=1 ;;
        -hl|--help-long)        HELP_LONG=1 ;;
        -hr|--hot-reload)       HOT_RELOAD=1 ;;
        -ma|--mode-agent)       MODE_AGENT=1 ;;
        -mf|--mode-front)       MODE_FRONT=1 ;;
        -r|--reset)             RESET=1 ;;
        -ra|--remote-agent)
            # Optional value: -ra user@host or just -ra
            if [[ -n "${2:-}" && ! "$2" =~ ^- ]]; then
                REMOTE_AGENT="$2"
                shift
            else
                REMOTE_AGENT="__from_file__"
            fi
            ;;
        -s|--silent)            SILENT=1 ;;
        -t|--test-unit)         TEST_UNIT=1 ;;
        -ta|--test-all)         TEST_ALL=1 ;;
        -tb|--test-back)        TEST_BACK=1 ;;
        -te|--test-e2e)         TEST_E2E=1 ;;
        -tec|--test-e2e-filter)
            TEST_E2E_FILTER="${2:-}"
            TEST_E2E_FILTER_SET=1
            shift
            ;;
        -tf|--test-front)       TEST_FRONT=1 ;;
        -ti|--test-integration) TEST_INTEGRATION=1 ;;
        *)
            echo "${C_RED}Unknown option: $1${C_RESET}" >&2
            show_options
            exit 1
            ;;
    esac
    shift
done

# ---------- implications ----------
if [[ $TEST_E2E_FILTER_SET -eq 1 ]]; then TEST_E2E=1; fi
# -ta runs everything; -t runs every unit suite. Unit (no DB), integration (Testcontainers, own
# containers) and E2E (isolated :15433) all bring their own databases, so NO test flag ever resets
# the shared dev DB (:15432); only an explicit -r does.
if [[ $TEST_ALL -eq 1 ]]; then TEST_UNIT=1; TEST_INTEGRATION=1; TEST_E2E=1; fi
if [[ $TEST_UNIT -eq 1 ]]; then TEST_BACK=1; TEST_FRONT=1; TEST_AGENT=1; TEST_ANALYZERS=1; fi
if [[ $COVERAGE -eq 1 ]]; then TEST_BACK=1; TEST_FRONT=1; fi
ANY_TEST=0
if [[ $TEST_BACK -eq 1 || $TEST_FRONT -eq 1 || $TEST_AGENT -eq 1 || $TEST_ANALYZERS -eq 1 || $TEST_INTEGRATION -eq 1 || $TEST_E2E -eq 1 ]]; then ANY_TEST=1; fi
E2E_ONLY=0
if [[ $TEST_E2E -eq 1 && $TEST_BACK -eq 0 && $TEST_FRONT -eq 0 && $TEST_AGENT -eq 0 && $TEST_ANALYZERS -eq 0 && $TEST_INTEGRATION -eq 0 && $COVERAGE -eq 0 ]]; then E2E_ONLY=1; fi
if [[ -n "$REMOTE_AGENT" ]]; then MODE_FRONT=1; fi
if [[ $MODE_FRONT -eq 0 && $MODE_AGENT -eq 0 ]]; then MODE_FRONT=1; fi

if [[ $HELP -eq 1 ]]; then show_options; exit 0; fi
if [[ $HELP_LONG -eq 1 ]]; then show_help_long; exit 0; fi

# ---------- deploy agent (remote) ----------

increment_patch() {
    local ver="$1"
    local major minor patch
    major="$(echo "$ver" | cut -d. -f1)"
    minor="$(echo "$ver" | cut -d. -f2)"
    patch="$(echo "$ver" | cut -d. -f3)"
    [[ -z "$patch" ]] && patch=0
    echo "${major}.${minor}.$((patch + 1))"
}

# Read APP_VERSION="..." (quoted or not) from a .env-prod-style file.
# Emits the bare version string on stdout; empty if not present.
read_env_version() {
    local file="$1"
    [[ -f "$file" ]] || return 0
    # shellcheck disable=SC2016
    grep -E '^[[:space:]]*APP_VERSION=' "$file" 2>/dev/null \
        | tail -n 1 \
        | sed -E 's/^[[:space:]]*APP_VERSION=//; s/^"(.*)"$/\1/; s/^'"'"'(.*)'"'"'$/\1/'
}

# Write APP_VERSION="..." to a .env-prod-style file, preserving other lines.
# Creates the file with just APP_VERSION if it does not exist.
write_env_version() {
    local file="$1"
    local new_version="$2"
    if [[ -f "$file" ]] && grep -qE '^[[:space:]]*APP_VERSION=' "$file"; then
        # In-place edit preserving every other line (secrets, etc.)
        local tmp
        tmp="$(mktemp)"
        awk -v v="$new_version" '
            BEGIN { replaced = 0 }
            /^[[:space:]]*APP_VERSION=/ { print "APP_VERSION=\"" v "\""; replaced = 1; next }
            { print }
            END { if (!replaced) print "APP_VERSION=\"" v "\"" }
        ' "$file" > "$tmp" && mv "$tmp" "$file"
    else
        printf 'APP_VERSION="%s"\n' "$new_version" >> "$file"
    fi
}

# Resolve the agent version AND persist it in the shared .env-prod file
# (same file deploy.sh reads/writes, so both scripts stay in sync).
#   $1 = auto_increment (0 = no, 1 = bump patch when reading from file)
#   $2 = env file path (.env-prod)
# Precedence:
#   1. explicit CLI arg (AGENT_VERSION) - overrides, saved to env file
#   2. $APP_VERSION env var - saved to env file
#   3. APP_VERSION in env file - reused (or bumped if auto_increment=1)
#   4. .agent-version legacy file - migrated into env file, then used
#   5. fallback 1.0.0 - saved to env file
resolve_agent_version() {
    local auto_increment="$1"
    local env_file="$2"
    local root_dir
    root_dir="$(dirname "$env_file")"
    local legacy_file="$root_dir/.agent-version"
    local version current

    if [[ -n "${AGENT_VERSION:-}" ]]; then
        version="$AGENT_VERSION"
    elif [[ -n "${APP_VERSION:-}" ]]; then
        version="$APP_VERSION"
    else
        current="$(read_env_version "$env_file")"
        if [[ -z "$current" && -f "$legacy_file" ]]; then
            # Migrate .agent-version -> .env-prod once, then delete legacy file
            current="$(tr -d '[:space:]' < "$legacy_file")"
            rm -f "$legacy_file"
        fi
        if [[ -n "$current" ]]; then
            if [[ "$auto_increment" -eq 1 ]]; then
                version="$(increment_patch "$current")"
            else
                version="$current"
            fi
        else
            version="1.0.0"
        fi
    fi

    write_env_version "$env_file" "$version"
    echo "$version"
}

deploy_agent_remote() {
    # args: <0=install | 1=upgrade>
    local upgrade_mode="$1"

    local ROOT_DIR
    ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
    local INSTALL_SCRIPT="$ROOT_DIR/deploy/scripts/install-agent-linux.sh"
    local PUBLISH_DIR="$ROOT_DIR/publish/agent"
    # Shared with deploy.sh. Override with DEPLOY_ENV_FILE=/path/to/.env-prod
    local ENV_FILE="${DEPLOY_ENV_FILE:-$ROOT_DIR/.env-prod}"

    if [[ ! -f "$INSTALL_SCRIPT" ]]; then
        echo "${C_RED}  Install script not found: $INSTALL_SCRIPT${C_RESET}"
        echo "${C_YELLOW}  Your repo at $ROOT_DIR seems incomplete or out of date.${C_RESET}"
        echo "${C_DGRAY}    cd $ROOT_DIR && git pull${C_RESET}"
        echo "${C_DGRAY}  If the repo is not there yet, clone it first.${C_RESET}"
        exit 1
    fi
    if [[ ! -d "$ROOT_DIR/src/Aetheus.Agent.Linux" ]]; then
        echo "${C_RED}  Agent project not found: $ROOT_DIR/src/Aetheus.Agent.Linux${C_RESET}"
        echo "${C_YELLOW}  launch-linux.sh must be run from the Aetheus repo root.${C_RESET}"
        exit 1
    fi

    local previous="" bumped=0
    previous="$(read_env_version "$ENV_FILE")"

    # Only -dau (upgrade) auto-increments; -da uses current version as-is.
    local version
    version="$(resolve_agent_version "$upgrade_mode" "$ENV_FILE")"
    if [[ "$upgrade_mode" -eq 1 && -z "${AGENT_VERSION:-}" && -n "$previous" && "$version" != "$previous" ]]; then
        bumped=1
    fi

    if [[ $bumped -eq 1 ]]; then
        echo "${C_DGRAY}  Version: $previous → $version (auto-incremented, saved in $(basename "$ENV_FILE"))${C_RESET}"
    else
        echo "${C_DGRAY}  Version: $version ($(basename "$ENV_FILE"))${C_RESET}"
    fi

    local upgrade_flag=""
    [[ "$upgrade_mode" -eq 1 ]] && upgrade_flag="--upgrade"

    # =========================================================
    # Auto-detect: remote (.remoteagent has target) vs local
    # =========================================================
    local ra_file="$ROOT_DIR/.remoteagent"
    local ra_target=""
    if [[ -f "$ra_file" ]]; then
        ra_target="$(cat "$ra_file" | tr -d '[:space:]')"
    fi
    if [[ -z "$ra_target" ]]; then
        read -r -p "  Enter SSH target (user@host or user@host:port), or press Enter to install locally: " ra_target
        ra_target="$(echo -n "$ra_target" | tr -d '[:space:]')"
        if [[ -n "$ra_target" ]]; then
            printf '%s' "$ra_target" > "$ra_file"
        fi
    fi

    # =========================================================
    # LOCAL mode: no SSH target → build + install on THIS machine
    # =========================================================
    if [[ -z "$ra_target" ]]; then
        write_step "Publishing agent v${version} locally (linux-x64, self-contained)..."
        rm -rf "$PUBLISH_DIR"
        if ! dotnet publish "$ROOT_DIR/src/Aetheus.Agent.Linux" -c Release \
            -p:Version="$version" -r linux-x64 --self-contained true \
            -o "$PUBLISH_DIR"; then
            echo "${C_RED}  dotnet publish failed. Install .NET SDK or run from a dev machine.${C_RESET}"
            exit 1
        fi

        cp "$INSTALL_SCRIPT" "$PUBLISH_DIR/"
        chmod +x "$PUBLISH_DIR/Aetheus.Agent.Linux" "$PUBLISH_DIR/install-agent-linux.sh"

        write_step "Running local install ($([[ $upgrade_mode -eq 1 ]] && echo upgrade || echo fresh))..."
        if ! (cd "$PUBLISH_DIR" && sudo sh ./install-agent-linux.sh $upgrade_flag); then
            echo "${C_RED}  Local install failed.${C_RESET}"
            exit 1
        fi

        echo ""
        echo "${C_GREEN}  Agent v${version} installed locally.${C_RESET}"
        echo "${C_DGRAY}  Watch logs: sudo journalctl -u aetheus-agent -f${C_RESET}"
        return 0
    fi

    # =========================================================
    # REMOTE mode: build + tarball + SCP + SSH to $ra_target
    # =========================================================

    local ssh_host="$ra_target"
    local ssh_port=""
    if [[ "$ra_target" =~ ^(.+):([0-9]+)$ ]]; then
        ssh_host="${BASH_REMATCH[1]}"
        ssh_port="${BASH_REMATCH[2]}"
    fi

    write_step "Publishing agent v${version} (linux-x64, self-contained)..."
    rm -rf "$PUBLISH_DIR"
    if ! dotnet publish "$ROOT_DIR/src/Aetheus.Agent.Linux" -c Release \
        -p:Version="$version" -r linux-x64 --self-contained true \
        -o "$PUBLISH_DIR"; then
        echo "${C_RED}  dotnet publish failed.${C_RESET}"
        exit 1
    fi

    cp "$INSTALL_SCRIPT" "$PUBLISH_DIR/"

    local tarball="/tmp/aetheus-agent-linux-${version}.tar.gz"
    write_step "Packaging $tarball..."
    if ! tar -czf "$tarball" -C "$PUBLISH_DIR" .; then
        echo "${C_RED}  tar failed.${C_RESET}"
        exit 1
    fi
    echo "${C_GREEN}  Tarball: $(ls -lh "$tarball" | awk '{print $5}')${C_RESET}"

    local remote_tarball="/tmp/aetheus-agent.tar.gz"
    local scp_args=()
    [[ -n "$ssh_port" ]] && scp_args+=(-P "$ssh_port")
    scp_args+=("$tarball" "${ssh_host}:${remote_tarball}")

    write_step "SCP to ${ssh_host}${ssh_port:+:$ssh_port}..."
    if ! scp "${scp_args[@]}"; then
        echo "${C_RED}  scp failed.${C_RESET}"
        exit 1
    fi

    local remote_dir remote_cmd
    if [[ "$upgrade_mode" -eq 1 ]]; then
        remote_dir="/tmp/aetheus-agent-upgrade"
    else
        remote_dir="/tmp/aetheus-agent-install"
    fi

    remote_cmd="set -e; \
        rm -rf '$remote_dir'; \
        mkdir -p '$remote_dir'; \
        cd '$remote_dir'; \
        tar -xzf '$remote_tarball'; \
        chmod +x Aetheus.Agent.Linux install-agent-linux.sh; \
        sudo sh ./install-agent-linux.sh $upgrade_flag"

    local ssh_args=()
    [[ -n "$ssh_port" ]] && ssh_args+=(-p "$ssh_port")
    ssh_args+=(-t "$ssh_host" "$remote_cmd")

    write_step "Running remote install ($([[ $upgrade_mode -eq 1 ]] && echo upgrade || echo fresh))..."
    if ! ssh "${ssh_args[@]}"; then
        echo "${C_RED}  Remote install failed.${C_RESET}"
        exit 1
    fi

    echo ""
    echo "${C_GREEN}  Agent v${version} deployed to ${ssh_host}.${C_RESET}"
    echo "${C_DGRAY}  Watch logs: ssh ${ssh_host}${ssh_port:+ -p $ssh_port} 'sudo journalctl -u aetheus-agent -f'${C_RESET}"
}

if [[ $DEPLOY_AGENT -eq 1 ]]; then
    deploy_agent_remote 0
    exit 0
fi
if [[ $DEPLOY_AGENT_UPGRADE -eq 1 ]]; then
    deploy_agent_remote 1
    exit 0
fi

# ---------- git push ----------

if [[ $GIT_PUSH -eq 1 ]]; then
    GM_FILE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/.gitmessage"
    if [[ ! -f "$GM_FILE" ]]; then
        echo "${C_RED}  .gitmessage not found at $GM_FILE${C_RESET}"
        exit 1
    fi
    if [[ ! -s "$GM_FILE" ]]; then
        echo "${C_RED}  .gitmessage is empty${C_RESET}"
        exit 1
    fi
    write_step "Git: stage + commit + push"

    echo "${C_DGRAY}  Staging all changes...${C_RESET}"
    git add -A || { echo "${C_RED}  git add failed.${C_RESET}"; exit 1; }

    echo "${C_DGRAY}  Committing with .gitmessage...${C_RESET}"
    git commit -F "$GM_FILE" || { echo "${C_RED}  git commit failed.${C_RESET}"; exit 1; }

    echo "${C_DGRAY}  Pushing...${C_RESET}"
    git push || { echo "${C_RED}  git push failed.${C_RESET}"; exit 1; }

    echo "${C_GREEN}  Done.${C_RESET}"
    exit 0
fi

# ============================================================
#  Paths
# ============================================================
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$ROOT/Aetheus.slnx"
BACK_DIR="$ROOT/src/Aetheus.Back"
FRONT_DIR="$ROOT/src/Aetheus.Front"
AGENT_DIR="$ROOT/src/Aetheus.Agent.Linux"
FRONT_URL="https://localhost:5401"
BACK_URL="https://localhost:5301/health/live"
COVERAGE_DIR="$ROOT/TestResults/Coverage"
REPORT_DIR="$ROOT/TestResults/CoverageReport"
DEV_DB_COMPOSE="$ROOT/deploy/compose/dev-db.compose.yml"
E2E_DB_COMPOSE="$ROOT/deploy/compose/e2e-db.compose.yml"
# E2E runs against its OWN PostgreSQL (:15433), NEVER the shared dev DB (:15432). The E2E backend's
# ConnectionStrings__Default override points here and the guarded reset-db endpoint recreates only
# this database while preserving the dedicated Docker volume. Mirrors launch-windows.ps1 / launch-core.ps1.
E2E_CONN_STRING="Host=localhost;Port=15433;Database=aetheus_e2e;Username=postgres;Password=postgres"
E2E_BACKEND=0   # flipped to 1 around the E2E server launch so start_one_server injects the E2E backend env

ensure_dev_db() {
    write_step "Ensuring local PostgreSQL (aetheus-local-database) is running..."
    if [[ "$(docker ps --filter 'name=^/aetheus-local-database$' --format '{{.Names}}' 2>/dev/null)" == "aetheus-local-database" ]]; then
        echo "${C_DGRAY}  Already up.${C_RESET}"
        return 0
    fi
    docker compose -f "$DEV_DB_COMPOSE" up -d || { echo "${C_RED}  Failed to start dev database. Is Docker running?${C_RESET}"; exit 1; }
    local i
    for ((i = 0; i < 30; i++)); do
        if [[ "$(docker inspect --format '{{.State.Health.Status}}' aetheus-local-database 2>/dev/null)" == "healthy" ]]; then
            echo "${C_GREEN}  Database healthy.${C_RESET}"
            return 0
        fi
        sleep 0.5
    done
    echo "${C_YELLOW}  Database not healthy after 15s - continuing anyway.${C_RESET}"
}

reset_dev_db() {
    write_step "Snapshotting local PostgreSQL before destructive reset..."
    ensure_dev_db
    local snapshot_dir="$ROOT/TestResults/DbSnapshots"
    local snapshot_tmp="$snapshot_dir/aetheus-dev-$(date +%Y%m%d-%H%M%S).sql.tmp"
    local snapshot="${snapshot_tmp%.tmp}"
    mkdir -p "$snapshot_dir"
    if ! docker exec aetheus-local-database pg_dump -U postgres -d aetheus --no-owner --no-acl > "$snapshot_tmp" \
       || [[ ! -s "$snapshot_tmp" ]]; then
        rm -f "$snapshot_tmp"
        echo "${C_RED}  pg_dump failed or returned an empty snapshot; reset aborted.${C_RESET}"
        exit 1
    fi
    mv "$snapshot_tmp" "$snapshot"
    echo "${C_GREEN}  Snapshot verified: $snapshot${C_RESET}"
    write_step "Resetting local PostgreSQL (drop volume + recreate)..."
    docker compose -f "$DEV_DB_COMPOSE" down -v || { echo "${C_RED}  Could not stop/reset the dev database.${C_RESET}"; exit 1; }
    ensure_dev_db
}

ensure_e2e_db() {
    write_step "Ensuring dedicated E2E PostgreSQL (aetheus-e2e-database :15433) is running..."
    if [[ "$(docker ps --filter 'name=^/aetheus-e2e-database$' --format '{{.Names}}' 2>/dev/null)" == "aetheus-e2e-database" ]]; then
        echo "${C_DGRAY}  Already up.${C_RESET}"
        return 0
    fi
    docker compose -f "$E2E_DB_COMPOSE" up -d || { echo "${C_RED}  Failed to start E2E database. Is Docker running?${C_RESET}"; exit 1; }
    local i
    for ((i = 0; i < 30; i++)); do
        if [[ "$(docker inspect --format '{{.State.Health.Status}}' aetheus-e2e-database 2>/dev/null)" == "healthy" ]]; then
            echo "${C_GREEN}  E2E database healthy.${C_RESET}"
            return 0
        fi
        sleep 0.5
    done
    echo "${C_RED}  E2E database not healthy after 15s.${C_RESET}"
    exit 1
}

# Exported into the E2E backend's subshell only: isolated DB (:15433) + demo seed + login rate-limiter
# disabled. Mirrors launch-core.ps1's per-job $env: overrides for the E2E backend.
_export_e2e_backend_env() {
    export ConnectionStrings__Default="$E2E_CONN_STRING"
    export Seed__Demo="true"
    export RateLimiting__Disabled="true"
    export BackgroundServices__TaskStartupDelay="00:30:00"
}

# ============================================================
#  Helpers
# ============================================================

# E2E category list (must match PowerShell launch-core.ps1 $E2eCategories, same order so a numeric
# -tec index selects the same category on both launchers).
E2E_CATEGORIES=(Auth Dashboard Servers Agents Pipelines Projects Tasks Alerts Settings Security Rbac Logs)

resolve_e2e_categories() {
    # stdout: resolved categories separated by newlines; empty = "all"
    local filter="$1"
    if [[ "$filter" == "all" || "$filter" == "*" ]]; then return 0; fi
    local IFS=','
    read -ra parts <<< "$filter"
    for raw in "${parts[@]}"; do
        local part
        part="$(echo -n "$raw" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
        [[ -z "$part" ]] && continue
        if [[ "$part" =~ ^[0-9]+$ ]]; then
            local idx=$((part - 1))
            if [[ $idx -lt 0 || $idx -ge ${#E2E_CATEGORIES[@]} ]]; then
                echo "${C_RED}  Unknown category number: $part (valid: 1-${#E2E_CATEGORIES[@]})${C_RESET}" >&2
                exit 1
            fi
            echo "${E2E_CATEGORIES[$idx]}"
        else
            local matched=""
            local c
            for c in "${E2E_CATEGORIES[@]}"; do
                if [[ "${c,,}" == "${part,,}" ]]; then matched="$c"; break; fi
            done
            if [[ -z "$matched" ]]; then
                echo "${C_RED}  Unknown category: '$part'${C_RESET}" >&2
                echo "${C_YELLOW}  Valid categories:${C_RESET}" >&2
                local i
                for i in "${!E2E_CATEGORIES[@]}"; do
                    echo "${C_YELLOW}    $((i + 1)). ${E2E_CATEGORIES[$i]}${C_RESET}" >&2
                done
                exit 1
            fi
            echo "$matched"
        fi
    done
}

show_e2e_menu() {
    echo "" >&2
    echo "${C_CYAN}  E2E Test Categories${C_RESET}" >&2
    echo "${C_DGRAY}  -------------------${C_RESET}" >&2
    local i
    for i in "${!E2E_CATEGORIES[@]}"; do
        echo "  $((i + 1)). ${E2E_CATEGORIES[$i]}" >&2
    done
    echo "" >&2
    echo "${C_YELLOW}  Enter numbers or names (comma-separated), or press Enter / * for all:${C_RESET}" >&2
    local ans
    read -r -p "  > " ans
    ans="$(echo -n "$ans" | sed -e 's/^[[:space:]]*//' -e 's/[[:space:]]*$//')"
    if [[ -z "$ans" || "$ans" == "*" || "${ans,,}" == "all" ]]; then return 0; fi
    resolve_e2e_categories "$ans"
}

wait_for_endpoint() {
    local url="$1"
    local label="$2"
    local max_seconds="${3:-60}"
    write_step "Waiting for $label to be ready..."
    local waited=0
    while [[ $waited -lt $max_seconds ]]; do
        local code
        code="$(curl -sk -o /dev/null -w '%{http_code}' --max-time 2 "$url" 2>/dev/null || echo 000)"
        if [[ "$code" =~ ^[123] ]]; then
            echo -e "\r  ${C_GREEN}$label ready after ${waited}s   ${C_RESET}"
            return 0
        fi
        sleep 1
        waited=$((waited + 1))
        echo -ne "\r  ${C_DGRAY}Waiting... ${waited}s   ${C_RESET}"
    done
    echo ""
    echo "${C_RED}  $label did not respond after ${max_seconds}s.${C_RESET}"
    return 1
}

# ---------- Test runner ----------

invoke_test_run() {
    # args: <target> <label> [extra dotnet test args...]
    local target="$1"; shift
    local label="$1"; shift

    write_step "$label"

    local tmp_out
    tmp_out="$(mktemp /tmp/aetheus_test.XXXXXX)"
    local tmp_err="${tmp_out}.err"

    local start_ts
    start_ts=$(date +%s)

    # Microsoft.Testing.Platform mode of `dotnet test` (global.json "test.runner"): the project is
    # named by --project, and its summary is a "Test run summary:" block, one counter per line. It runs
    # from the repository root: the runner comes from the global.json of the working directory.
    (cd "$ROOT" && exec dotnet test --project "$target" --no-build --configuration "$TEST_CONFIGURATION" --no-progress "$@") \
        >"$tmp_out" 2>"$tmp_err" &
    local pid=$!

    while kill -0 "$pid" 2>/dev/null; do
        local elapsed=$(( $(date +%s) - start_ts ))
        echo -ne "\r  ${C_DGRAY}Elapsed: ${elapsed}s   ${C_RESET}"
        sleep 1
    done
    wait "$pid"
    local exit_code=$?
    local end_ts
    end_ts=$(date +%s)
    local duration=$((end_ts - start_ts))
    echo ""

    local combined
    combined="$(cat "$tmp_out" "$tmp_err" 2>/dev/null)"
    rm -f "$tmp_out" "$tmp_err"

    if [[ $exit_code -ne 0 ]]; then
        echo "$combined" | grep -E "Failed|Error|Exception" | while IFS= read -r line; do
            echo "${C_RED}  $line${C_RESET}"
        done
    fi

    summary_counter() {
        local value
        value="$(sed -n "s/^[[:space:]]*$1:[[:space:]]*\([0-9][0-9]*\)[[:space:]]*$/\1/p" <<<"$combined" | tail -n 1)"
        echo "${value:-0}"
    }
    local passed failed total skipped
    total="$(summary_counter total)"; failed="$(summary_counter failed)"
    passed="$(summary_counter succeeded)"; skipped="$(summary_counter skipped)"

    echo ""
    echo "${C_DGRAY}  ----------------------------------------${C_RESET}"
    echo "${C_WHITE}  Test summary  (${duration}s)${C_RESET}"
    echo "${C_DGRAY}  ----------------------------------------${C_RESET}"
    echo "${C_GREEN}  Passed : $passed${C_RESET}"
    if [[ $failed -gt 0 ]]; then
        echo "${C_RED}  Failed : $failed${C_RESET}"
    else
        echo "${C_DGRAY}  Failed : $failed${C_RESET}"
    fi
    if [[ $skipped -gt 0 ]]; then echo "${C_YELLOW}  Skipped: $skipped${C_RESET}"; fi
    echo "${C_WHITE}  Total  : $total${C_RESET}"
    echo "${C_DGRAY}  ----------------------------------------${C_RESET}"
    echo ""

    # Publish results via globals (bash has no easy multi-return)
    LAST_TEST_EXIT=$exit_code
    LAST_TEST_PASSED=$passed
    LAST_TEST_FAILED=$failed
    LAST_TEST_SKIPPED=$skipped
    LAST_TEST_TOTAL=$total
}

invoke_coverage_report() {
    write_step "Generating HTML coverage report..."
    if [[ ! -d "$COVERAGE_DIR" ]]; then
        echo "${C_RED}  Expected exactly 3 product coverage reports in $COVERAGE_DIR, found 0.${C_RESET}"
        return 1
    fi
    # coverlet.MTP stamps its reports; give them the coverage.cobertura.xml layout the CI readers use.
    sh "$ROOT/deploy/scripts/normalize-coverage-report.sh" "$COVERAGE_DIR" >/dev/null || return 1
    local xml_files
    mapfile -t xml_files < <(find "$COVERAGE_DIR" -type f -name "coverage.cobertura.xml" -print 2>/dev/null | sort)
    if [[ ${#xml_files[@]} -ne 3 ]]; then
        echo "${C_RED}  Expected exactly 3 product coverage reports in $COVERAGE_DIR, found ${#xml_files[@]}.${C_RESET}"
        return 1
    fi
    echo "${C_DGRAY}  Found ${#xml_files[@]} coverage file(s)${C_RESET}"
    [[ -d "$REPORT_DIR" ]] && rm -rf "$REPORT_DIR"
    local report_target="$REPORT_DIR"
    # Git Bash does not path-convert a semicolon-joined list inside one argument. Convert every
    # report explicitly for the Windows dotnet host; native Linux keeps its absolute paths unchanged.
    if [[ "${OSTYPE:-}" == msys* || "${OSTYPE:-}" == cygwin* ]]; then
        local i
        for i in "${!xml_files[@]}"; do
            xml_files[$i]="$(cygpath -w "${xml_files[$i]}")"
        done
        report_target="$(cygpath -w "$REPORT_DIR")"
    fi
    local reports
    reports="$(IFS=';'; echo "${xml_files[*]}")"
    if ! (cd "$ROOT" && dotnet tool restore); then
        echo "${C_RED}  dotnet tool restore failed.${C_RESET}"
        return 1
    fi
    if ! (cd "$ROOT" && dotnet tool run reportgenerator -- \
        "-reports:$reports" \
        "-targetdir:$report_target" \
        "-reporttypes:Html;Cobertura" \
        "-assemblyfilters:+Aetheus.Back;+Aetheus.Front;+Aetheus.Agent.Core" \
        "-verbosity:Warning"); then
        echo "${C_RED}  ReportGenerator failed.${C_RESET}"
        return 1
    fi

    local merged_report="$REPORT_DIR/Cobertura.xml"
    if [[ ! -s "$merged_report" ]]; then
        echo "${C_RED}  Merged Cobertura report was not produced.${C_RESET}"
        return 1
    fi
    local coverage_tag lines_covered lines_valid line_rate
    coverage_tag="$(grep -m 1 '<coverage ' "$merged_report" || true)"
    lines_covered="$(sed -n 's/.*lines-covered="\([0-9][0-9]*\)".*/\1/p' <<<"$coverage_tag")"
    lines_valid="$(sed -n 's/.*lines-valid="\([0-9][0-9]*\)".*/\1/p' <<<"$coverage_tag")"
    if [[ -z "$lines_covered" || -z "$lines_valid" || "$lines_valid" -le 0 || "$lines_covered" -gt "$lines_valid" ]]; then
        echo "${C_RED}  Merged Cobertura report contains invalid line totals.${C_RESET}"
        return 1
    fi
    line_rate="$(awk -v covered="$lines_covered" -v valid="$lines_valid" 'BEGIN { printf "%.2f", 100 * covered / valid }')"
    printf "${C_CYAN}  Product line coverage: %s%% (%s/%s)${C_RESET}\n" "$line_rate" "$lines_covered" "$lines_valid"
    if ! awk -v covered="$lines_covered" -v valid="$lines_valid" 'BEGIN { exit !((100 * covered / valid) >= 80.1) }'; then
        echo "${C_RED}  Product line coverage is below the required 80.1%: ${line_rate}%${C_RESET}"
        return 1
    fi
    local index_html="$REPORT_DIR/index.html"
    if [[ -f "$index_html" ]]; then
        echo "${C_GREEN}  Coverage report: $index_html${C_RESET}"
        if command -v xdg-open >/dev/null 2>&1; then
            xdg-open "$index_html" >/dev/null 2>&1 &
        fi
    fi
    return 0
}

# ---------- Server lifecycle ----------

declare -A JOB_PIDS=()
declare -A JOB_LOGS=()

start_one_server() {
    # args: <name> <workdir> <color> <use_watch>
    local name="$1"
    local dir="$2"
    local use_watch="$3"

    local log_file
    log_file="$(mktemp "/tmp/aetheus_${name,,}_log.XXXXXX")"

    if [[ $use_watch -eq 1 ]]; then
        (
            cd "$dir"
            [[ $E2E_BACKEND -eq 1 && "$name" == "Back" ]] && _export_e2e_backend_env
            export DOTNET_WATCH_RESTART_ON_RUDE_EDIT=1
            dotnet watch run --configuration Debug
        ) >"$log_file" 2>&1 &
    else
        (
            cd "$dir"
            [[ $E2E_BACKEND -eq 1 && "$name" == "Back" ]] && _export_e2e_backend_env
            dotnet run --no-build --configuration Debug
        ) >"$log_file" 2>&1 &
    fi

    JOB_PIDS["$name"]=$!
    JOB_LOGS["$name"]="$log_file"
}

start_servers() {
    if [[ $MODE_FRONT -eq 1 ]]; then
        if [[ $HOT_RELOAD -eq 1 ]]; then
            write_step "Starting Backend with hot reload (https://localhost:5301)..."
            start_one_server "Back" "$BACK_DIR" 1
            write_step "Starting Frontend with hot reload (https://localhost:5401)..."
            start_one_server "Front" "$FRONT_DIR" 1
        else
            write_step "Starting Backend (https://localhost:5301)..."
            start_one_server "Back" "$BACK_DIR" 0
            write_step "Starting Frontend (https://localhost:5401)..."
            start_one_server "Front" "$FRONT_DIR" 0
        fi
    fi

    if [[ $MODE_AGENT -eq 1 ]]; then
        if [[ $HOT_RELOAD -eq 1 ]]; then
            write_step "Starting Agent with hot reload..."
            start_one_server "Agent" "$AGENT_DIR" 1
        else
            write_step "Starting Agent..."
            start_one_server "Agent" "$AGENT_DIR" 0
        fi
    fi
}

stop_servers() {
    write_step "Shutting down..."
    local name pid
    for name in "${!JOB_PIDS[@]}"; do
        pid="${JOB_PIDS[$name]}"
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            # kill the whole process group to catch dotnet + child processes
            pkill -TERM -P "$pid" 2>/dev/null || true
            kill -TERM "$pid" 2>/dev/null || true
        fi
    done
    sleep 1
    for name in "${!JOB_PIDS[@]}"; do
        pid="${JOB_PIDS[$name]}"
        if [[ -n "$pid" ]] && kill -0 "$pid" 2>/dev/null; then
            pkill -KILL -P "$pid" 2>/dev/null || true
            kill -KILL "$pid" 2>/dev/null || true
        fi
    done
    for name in "${!JOB_LOGS[@]}"; do
        rm -f "${JOB_LOGS[$name]}" 2>/dev/null || true
    done
    echo "${C_GREEN}  Done.${C_RESET}"
}

# ============================================================
#  MAIN FLOW
# ============================================================

# ---------- 1. Kill existing instances ----------

write_step "Killing existing Aetheus processes..."

kill_port() {
    local port="$1"
    local pids
    if command -v lsof >/dev/null 2>&1; then
        pids="$(lsof -t -i TCP:"$port" -sTCP:LISTEN 2>/dev/null || true)"
    elif command -v fuser >/dev/null 2>&1; then
        pids="$(fuser -n tcp "$port" 2>/dev/null | tr -d ':' || true)"
    else
        pids=""
    fi
    local p
    for p in $pids; do
        [[ -z "$p" ]] && continue
        local pname
        pname="$(ps -o comm= -p "$p" 2>/dev/null || echo '?')"
        echo "${C_YELLOW}  Killing PID $p ($pname) on port $port${C_RESET}"
        kill -TERM "$p" 2>/dev/null || true
    done
}

kill_port 5301
kill_port 5401

# Kill any lingering dotnet processes running Aetheus projects
if command -v pgrep >/dev/null 2>&1; then
    while IFS= read -r p; do
        [[ -z "$p" ]] && continue
        echo "${C_YELLOW}  Killing dotnet PID $p (Aetheus-related)${C_RESET}"
        kill -TERM "$p" 2>/dev/null || true
    done < <(pgrep -f 'dotnet.*Aetheus\.(Back|Front|Agent\.Linux)' 2>/dev/null || true)
fi

sleep 1

# ---------- 2. Build ----------

# Blazor WASM reads this file in the browser. A worktree previously started by launch-windows.ps1 may
# contain that worktree's Windows port (for example :5302), while ybaunch owns the canonical Linux
# pair :5301/:5401. Keep the existing dev banner and rewrite only an explicit API override.
front_dev_settings="$FRONT_DIR/wwwroot/appsettings.Development.json"
if [[ -f "$front_dev_settings" ]] && grep -q '"ApiBaseUrl"' "$front_dev_settings"; then
    sed -i -E 's#("ApiBaseUrl"[[:space:]]*:[[:space:]]*)"[^"]*"#\1"https://localhost:5301"#' "$front_dev_settings"
fi

BUILD_CONFIGURATION="Debug"
TEST_CONFIGURATION="Debug"
if [[ $COVERAGE -eq 1 ]]; then
    BUILD_CONFIGURATION="Release"
    TEST_CONFIGURATION="Release"
fi

if [[ $HOT_RELOAD -eq 1 && $BUILD -eq 0 ]]; then
    echo "${C_DGRAY}  Skipping build (dotnet watch handles it).${C_RESET}"
else
    if [[ $E2E_ONLY -eq 1 ]]; then
        write_step "Building the E2E runtime projects..."
        for project in \
            "$ROOT/src/Aetheus.Back/Aetheus.Back.csproj" \
            "$ROOT/src/Aetheus.Front/Aetheus.Front.csproj" \
            "$ROOT/tests/Aetheus.E2E/Aetheus.E2E.csproj"; do
            if ! dotnet build "$project" --configuration "$BUILD_CONFIGURATION"; then
                echo "${C_RED}BUILD FAILED.${C_RESET}"
                exit 1
            fi
        done
    else
        write_step "Building solution..."
        if ! dotnet build "$SOLUTION" --configuration "$BUILD_CONFIGURATION"; then
            echo "${C_RED}BUILD FAILED.${C_RESET}"
            exit 1
        fi
    fi
    echo "${C_GREEN}  Build succeeded.${C_RESET}"
fi

# ---------- 2b. -b / --build: build only and exit ----------

if [[ $BUILD -eq 1 && $ANY_TEST -eq 0 ]]; then
    exit 0
fi

# ---------- 3. Tests (granular) ----------

declare -a TEST_LABELS=()
declare -a TEST_PASSED=()
declare -a TEST_FAILED=()
declare -a TEST_SKIPPED=()
declare -a TEST_TOTAL=()
TEST_PROCESS_FAILED=0

if [[ $TEST_BACK -eq 1 || $TEST_FRONT -eq 1 || $TEST_AGENT -eq 1 || $TEST_ANALYZERS -eq 1 || $TEST_INTEGRATION -eq 1 ]]; then
    if [[ $COVERAGE -eq 1 && -d "$COVERAGE_DIR" ]]; then
        rm -rf "$COVERAGE_DIR"
    fi

    if [[ $TEST_BACK -eq 1 ]]; then
        BACK_TEST_DIR="$ROOT/tests/Aetheus.Back.Tests"
        extra=()
        if [[ $COVERAGE -eq 1 ]]; then
            extra=(--coverlet --results-directory "$COVERAGE_DIR")
        fi
        invoke_test_run "$BACK_TEST_DIR" "Running backend unit tests..." "${extra[@]}"
        TEST_LABELS+=("Back")
        TEST_PASSED+=("$LAST_TEST_PASSED"); TEST_FAILED+=("$LAST_TEST_FAILED")
        TEST_SKIPPED+=("$LAST_TEST_SKIPPED"); TEST_TOTAL+=("$LAST_TEST_TOTAL")
        if [[ $LAST_TEST_EXIT -ne 0 ]]; then
            echo "${C_RED}BACKEND TESTS FAILED - aborting.${C_RESET}"; exit 1
        fi
    fi

    if [[ $TEST_FRONT -eq 1 ]]; then
        FRONT_TEST_DIR="$ROOT/tests/Aetheus.Front.Tests"
        extra=()
        if [[ $COVERAGE -eq 1 ]]; then
            extra=(--coverlet --results-directory "$COVERAGE_DIR")
        fi
        invoke_test_run "$FRONT_TEST_DIR" "Running frontend unit tests..." "${extra[@]}"
        TEST_LABELS+=("Front")
        TEST_PASSED+=("$LAST_TEST_PASSED"); TEST_FAILED+=("$LAST_TEST_FAILED")
        TEST_SKIPPED+=("$LAST_TEST_SKIPPED"); TEST_TOTAL+=("$LAST_TEST_TOTAL")
        if [[ $LAST_TEST_EXIT -ne 0 ]]; then
            echo "${C_RED}FRONTEND TESTS FAILED - aborting.${C_RESET}"; exit 1
        fi
    fi

    # Agent.Core - part of the unit set (-t) and always run under -c so the aggregated coverage %
    # covers all three xUnit+bUnit suites.
    if [[ $TEST_AGENT -eq 1 || $COVERAGE -eq 1 ]]; then
        AGENT_TEST_DIR="$ROOT/tests/Aetheus.Agent.Core.Tests"
        extra=()
        if [[ $COVERAGE -eq 1 ]]; then
            extra=(--coverlet --results-directory "$COVERAGE_DIR")
        fi
        invoke_test_run "$AGENT_TEST_DIR" "Running agent-core unit tests..." "${extra[@]}"
        TEST_LABELS+=("Agent.Core")
        TEST_PASSED+=("$LAST_TEST_PASSED"); TEST_FAILED+=("$LAST_TEST_FAILED")
        TEST_SKIPPED+=("$LAST_TEST_SKIPPED"); TEST_TOTAL+=("$LAST_TEST_TOTAL")
        if [[ $LAST_TEST_EXIT -ne 0 ]]; then
            echo "${C_RED}AGENT-CORE TESTS FAILED - aborting.${C_RESET}"; exit 1
        fi
    fi

    # Analyzers - Roslyn analyzer regression suite, part of the unit set (-t). Not in coverage.
    if [[ $TEST_ANALYZERS -eq 1 ]]; then
        ANALYZERS_TEST_DIR="$ROOT/tests/Aetheus.Analyzers.Tests"
        invoke_test_run "$ANALYZERS_TEST_DIR" "Running analyzer tests..."
        TEST_LABELS+=("Analyzers")
        TEST_PASSED+=("$LAST_TEST_PASSED"); TEST_FAILED+=("$LAST_TEST_FAILED")
        TEST_SKIPPED+=("$LAST_TEST_SKIPPED"); TEST_TOTAL+=("$LAST_TEST_TOTAL")
        if [[ $LAST_TEST_EXIT -ne 0 ]]; then
            echo "${C_RED}ANALYZER TESTS FAILED - aborting.${C_RESET}"; exit 1
        fi
    fi

    # PLAN-015 lot 0: under -c the integration tests run with coverage too, in their own subdirectory:
    # the product report still merges exactly the three unit suites, the CRAP measure reads every
    # report under the coverage directory. Without -c they run only when asked (-ti), as before.
    if [[ $COVERAGE -eq 1 ]]; then
        INTEGRATION_TEST_DIR="$ROOT/tests/Aetheus.Back.IntegrationTests"
        invoke_test_run "$INTEGRATION_TEST_DIR" "Running integration tests (Testcontainers) with coverage..." \
            --coverlet --results-directory "$COVERAGE_DIR/Integration"
        TEST_LABELS+=("Integration")
        TEST_PASSED+=("$LAST_TEST_PASSED"); TEST_FAILED+=("$LAST_TEST_FAILED")
        TEST_SKIPPED+=("$LAST_TEST_SKIPPED"); TEST_TOTAL+=("$LAST_TEST_TOTAL")
        if [[ $LAST_TEST_EXIT -ne 0 ]]; then
            echo "${C_RED}INTEGRATION TESTS FAILED - aborting.${C_RESET}"; exit 1
        fi
    fi

    if [[ $COVERAGE -eq 1 ]] && ! invoke_coverage_report; then
        exit 1
    fi

    if [[ $TEST_INTEGRATION -eq 1 && $COVERAGE -ne 1 ]]; then
        INTEGRATION_TEST_DIR="$ROOT/tests/Aetheus.Back.IntegrationTests"
        invoke_test_run "$INTEGRATION_TEST_DIR" "Running integration tests (Testcontainers)..."
        TEST_LABELS+=("Integration")
        TEST_PASSED+=("$LAST_TEST_PASSED"); TEST_FAILED+=("$LAST_TEST_FAILED")
        TEST_SKIPPED+=("$LAST_TEST_SKIPPED"); TEST_TOTAL+=("$LAST_TEST_TOTAL")
        if [[ $LAST_TEST_EXIT -ne 0 ]]; then
            echo "${C_RED}INTEGRATION TESTS FAILED - aborting.${C_RESET}"; exit 1
        fi
    fi
fi

# ---------- 4. E2E tests (needs servers) ----------

ensure_playwright_browser() {
    local output_dir="$ROOT/tests/Aetheus.E2E/bin/$BUILD_CONFIGURATION/net10.0"
    local playwright_cli="$output_dir/.playwright/package/cli.js"
    local playwright_node=""
    playwright_node="$(find "$output_dir/.playwright/node" -mindepth 2 -maxdepth 2 -type f -name node -print -quit 2>/dev/null)"
    if [[ -z "$playwright_node" || ! -f "$playwright_cli" ]]; then
        echo "${C_RED}Playwright runtime not found under $output_dir.${C_RESET}"
        return 1
    fi

    write_step "Ensuring Playwright Chromium is installed..."
    "$playwright_node" "$playwright_cli" install --with-deps chromium
}

if [[ $TEST_E2E -eq 1 ]]; then
    declare -a e2e_categories=()
    if [[ $TEST_E2E_FILTER_SET -eq 1 ]]; then
        if [[ "$TEST_E2E_FILTER" == "?" ]]; then
            mapfile -t e2e_categories < <(show_e2e_menu)
        else
            mapfile -t e2e_categories < <(resolve_e2e_categories "$TEST_E2E_FILTER")
        fi
    fi

    e2e_extra=()
    e2e_label="Running E2E tests..."
    if [[ ${#e2e_categories[@]} -gt 0 ]]; then
        filter_expr=""
        for c in "${e2e_categories[@]}"; do
            if [[ -z "$filter_expr" ]]; then filter_expr="Category=$c"
            else filter_expr="$filter_expr|Category=$c"; fi
        done
        e2e_extra=(--filter "$filter_expr")
        e2e_label="Running E2E tests [$(IFS=', '; echo "${e2e_categories[*]}")]..."
    fi

    if ! ensure_playwright_browser; then
        echo "${C_RED}PLAYWRIGHT BROWSER INSTALL FAILED.${C_RESET}"
        exit 1
    fi

    # Preserve the dedicated E2E volume; the fixture resets only the guarded aetheus_e2e database.
    ensure_e2e_db
    E2E_BACKEND=1

    start_servers
    trap 'stop_servers' EXIT INT TERM

    back_ready=1; front_ready=1
    wait_for_endpoint "$BACK_URL"  "Backend"  60 || back_ready=0
    wait_for_endpoint "$FRONT_URL" "Frontend" 60 || front_ready=0
    if [[ $back_ready -eq 0 || $front_ready -eq 0 ]]; then
        echo "${C_RED}Servers not ready - aborting E2E.${C_RESET}"
        exit 1
    fi

    export E2E_FRONTEND_URL="https://localhost:5401"
    export E2E_BACKEND_URL="https://localhost:5301"
    # The local Blazor DevServer does not execute deploy/docker/StaticServer.Program.cs.
    export E2E_REQUIRE_FRONTEND_SECURITY_HEADERS="false"
    invoke_test_run "$ROOT/tests/Aetheus.E2E" "$e2e_label" "${e2e_extra[@]}"
    e2e_exit=$LAST_TEST_EXIT
    TEST_LABELS+=("E2E")
    TEST_PASSED+=("$LAST_TEST_PASSED"); TEST_FAILED+=("$LAST_TEST_FAILED")
    TEST_SKIPPED+=("$LAST_TEST_SKIPPED"); TEST_TOTAL+=("$LAST_TEST_TOTAL")

    stop_servers
    trap - EXIT INT TERM

    if [[ $e2e_exit -ne 0 ]]; then
        TEST_PROCESS_FAILED=1
        echo ""
        if [[ $LAST_TEST_FAILED -gt 0 ]]; then
            echo "${C_RED}  FAILED: $LAST_TEST_FAILED/$LAST_TEST_TOTAL - E2E TESTS FAILED.${C_RESET}"
        else
            echo "${C_RED}  E2E TEST PROCESS FAILED (exit code $e2e_exit) after reporting $LAST_TEST_TOTAL tests.${C_RESET}"
        fi
    fi
fi

# ---------- 5. Combined summary + exit ----------

if [[ ${#TEST_LABELS[@]} -gt 0 ]]; then
    total_passed=0; total_failed=0; total_skipped=0; total_all=0
    for v in "${TEST_PASSED[@]}";  do total_passed=$((total_passed + v)); done
    for v in "${TEST_FAILED[@]}";  do total_failed=$((total_failed + v)); done
    for v in "${TEST_SKIPPED[@]}"; do total_skipped=$((total_skipped + v)); done
    for v in "${TEST_TOTAL[@]}";   do total_all=$((total_all + v)); done

    suites_ran="$(IFS=' + '; echo "${TEST_LABELS[*]}")"
    echo ""
    echo "${C_CYAN}  =========================================${C_RESET}"
    echo "${C_WHITE}  Combined ($suites_ran)${C_RESET}"
    echo "${C_DGRAY}  -----------------------------------------${C_RESET}"
    echo "${C_GREEN}  Passed : $total_passed${C_RESET}"
    if [[ $total_failed -gt 0 ]]; then
        echo "${C_RED}  Failed : $total_failed${C_RESET}"
    else
        echo "${C_DGRAY}  Failed : $total_failed${C_RESET}"
    fi
    if [[ $total_skipped -gt 0 ]]; then echo "${C_YELLOW}  Skipped: $total_skipped${C_RESET}"; fi
    if [[ $TEST_PROCESS_FAILED -ne 0 ]]; then echo "${C_RED}  Process failures: E2E${C_RESET}"; fi
    echo "${C_WHITE}  Total  : $total_all${C_RESET}"
    echo "${C_CYAN}  =========================================${C_RESET}"
    echo ""
    if [[ $total_failed -gt 0 || $TEST_PROCESS_FAILED -ne 0 || $total_all -le 0 ]]; then
        echo "${C_RED}  SOME TESTS FAILED.${C_RESET}"
        exit 1
    fi
    echo "${C_GREEN}  All tests passed.${C_RESET}"
    exit 0
fi

# ---------- 6. Reset database (-r without tests) ----------

if [[ $RESET -eq 1 ]]; then
    reset_dev_db
else
    ensure_dev_db
fi

# ---------- 7. Start servers ----------

start_servers

# ---------- 8. Wait for frontend, open browser ----------

if [[ $MODE_FRONT -eq 1 ]]; then
    wait_for_endpoint "$FRONT_URL" "Frontend" 30 || true

    if [[ $SILENT -eq 0 ]]; then
        write_step "Opening browser -> $FRONT_URL"
        if command -v xdg-open >/dev/null 2>&1; then
            xdg-open "$FRONT_URL" >/dev/null 2>&1 &
        else
            echo "${C_DGRAY}  xdg-open not found; open $FRONT_URL manually.${C_RESET}"
        fi
    else
        write_step "Servers ready (browser open skipped with -s)"
    fi
else
    write_step "Agent started (no frontend in this mode)"
fi

# ---------- 9. SSH tunnel for remote agent (after servers are ready) ----------

SSH_TUNNEL_PID=""
if [[ -n "$REMOTE_AGENT" ]]; then
    RA_FILE="$ROOT/.remoteagent"
    RA_TARGET=""

    # Resolve target: explicit arg > saved file > interactive prompt
    if [[ "$REMOTE_AGENT" != "__from_file__" ]]; then
        RA_TARGET="$REMOTE_AGENT"
    elif [[ -f "$RA_FILE" ]]; then
        RA_TARGET="$(cat "$RA_FILE" | tr -d '[:space:]')"
        echo "${C_DGRAY}  Saved target: $RA_TARGET  (edit .remoteagent to change)${C_RESET}"
    fi

    if [[ -z "$RA_TARGET" ]]; then
        echo ""
        read -r -p "  Enter SSH target (user@host or user@host:port): " RA_TARGET
        RA_TARGET="$(echo -n "$RA_TARGET" | tr -d '[:space:]')"
        if [[ -z "$RA_TARGET" ]]; then
            echo "${C_RED}  No target provided - aborting.${C_RESET}"
            stop_servers
            exit 1
        fi
    fi

    printf '%s' "$RA_TARGET" > "$RA_FILE"

    # Parse host:port (e.g. root@example.com:2222)
    SSH_HOST="$RA_TARGET"
    SSH_PORT=""
    if [[ "$RA_TARGET" =~ ^(.+):([0-9]+)$ ]]; then
        SSH_HOST="${BASH_REMATCH[1]}"
        SSH_PORT="${BASH_REMATCH[2]}"
    fi

    port_label=""
    [[ -n "$SSH_PORT" ]] && port_label=" (SSH port $SSH_PORT)"
    write_step "Opening SSH tunnel -> $SSH_HOST${port_label} (fwd 5300)..."
    echo "${C_YELLOW}  You may be prompted for your SSH password below.${C_RESET}"

    ssh_cmd=(ssh -NR 5300:localhost:5300 "$SSH_HOST"
             -o ServerAliveInterval=60
             -o ExitOnForwardFailure=yes)
    [[ -n "$SSH_PORT" ]] && ssh_cmd+=(-p "$SSH_PORT")

    "${ssh_cmd[@]}" &
    SSH_TUNNEL_PID=$!
    sleep 3
    if ! kill -0 "$SSH_TUNNEL_PID" 2>/dev/null; then
        echo "${C_RED}  SSH tunnel failed. Check your SSH key / credentials for $SSH_HOST.${C_RESET}"
        stop_servers
        exit 1
    fi
    echo "${C_GREEN}  Tunnel active: $SSH_HOST:5300 -> localhost:5300${C_RESET}"
    echo "${C_DGRAY}  Remote agent config: ServerUrl = http://localhost:5300${C_RESET}"
fi

cleanup_all() {
    if [[ -n "$SSH_TUNNEL_PID" ]] && kill -0 "$SSH_TUNNEL_PID" 2>/dev/null; then
        echo "${C_DGRAY}  Closing SSH tunnel (PID $SSH_TUNNEL_PID)...${C_RESET}"
        kill -TERM "$SSH_TUNNEL_PID" 2>/dev/null || true
    fi
    stop_servers
    exit 0
}
trap 'cleanup_all' EXIT INT TERM

# ---------- 10. Stream output until Ctrl+C ----------

echo ""
echo "${C_MAGENTA}Press Ctrl+C to stop all services.${C_RESET}"
echo ""

# Tail each server's log with a colored prefix
declare -a TAIL_PIDS=()
if [[ -n "${JOB_LOGS[Back]:-}" ]]; then
    ( tail -n 0 -F "${JOB_LOGS[Back]}" 2>/dev/null | while IFS= read -r line; do echo "${C_DCYAN}[BACK]  $line${C_RESET}"; done ) &
    TAIL_PIDS+=($!)
fi
if [[ -n "${JOB_LOGS[Front]:-}" ]]; then
    ( tail -n 0 -F "${JOB_LOGS[Front]}" 2>/dev/null | while IFS= read -r line; do echo "${C_DGREEN}[FRONT] $line${C_RESET}"; done ) &
    TAIL_PIDS+=($!)
fi
if [[ -n "${JOB_LOGS[Agent]:-}" ]]; then
    ( tail -n 0 -F "${JOB_LOGS[Agent]}" 2>/dev/null | while IFS= read -r line; do echo "${C_DYELLOW}[AGENT] $line${C_RESET}"; done ) &
    TAIL_PIDS+=($!)
fi

cleanup_tails() {
    local t
    for t in "${TAIL_PIDS[@]}"; do kill "$t" 2>/dev/null || true; done
}
trap 'cleanup_tails; cleanup_all' EXIT INT TERM

# Loop: watch server pids; exit if any dies
while true; do
    any_alive=0
    for name in "${!JOB_PIDS[@]}"; do
        pid="${JOB_PIDS[$name]}"
        if kill -0 "$pid" 2>/dev/null; then
            any_alive=1
        else
            # S-UX-36: report which component went down and with what exit code (the PID is the
            # subshell wrapping dotnet run, so wait yields its exit status).
            code=0
            wait "$pid" 2>/dev/null || code=$?
            if [[ $code -eq 0 ]]; then
                echo "${C_YELLOW}$name stopped (exit code: 0) - shutting down all services.${C_RESET}"
            else
                echo "${C_RED}$name crashed (exit code: $code) - shutting down all services.${C_RESET}"
            fi
            exit 1
        fi
    done
    [[ $any_alive -eq 0 ]] && break
    sleep 1
done
