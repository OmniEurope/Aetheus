// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

internal static class LinuxAgentRollbackGuardScript
{
    internal static string Write(string stagingRoot)
    {
        var scriptPath = Path.Combine(stagingRoot, "guard-update.sh");
        const string script = """
            #!/bin/sh
            set -eu

            old_pid="$1"
            install_dir="$2"
            rollback_dir="$3"
            marker="$4"
            timeout_seconds="$5"
            log_path="$(dirname "$marker")/.agent-update/update.log"

            while kill -0 "$old_pid" 2>/dev/null; do
                sleep 1
            done

            elapsed=0
            while [ -e "$marker" ] && [ "$elapsed" -lt "$timeout_seconds" ]; do
                sleep 1
                elapsed=$((elapsed + 1))
            done

            if [ ! -e "$marker" ]; then
                exit 0
            fi

            new_pid="$(cat "$marker" 2>/dev/null || printf '0')"
            case "$new_pid" in
                ''|*[!0-9]*) new_pid=0 ;;
            esac
            if [ "$new_pid" -gt 0 ] && kill -0 "$new_pid" 2>/dev/null; then
                kill "$new_pid" 2>/dev/null || true
                wait_elapsed=0
                while kill -0 "$new_pid" 2>/dev/null && [ "$wait_elapsed" -lt 30 ]; do
                    sleep 1
                    wait_elapsed=$((wait_elapsed + 1))
                done
                if kill -0 "$new_pid" 2>/dev/null; then
                    kill -9 "$new_pid" 2>/dev/null || true
                fi
            fi

            if [ ! -d "$rollback_dir" ]; then
                printf '%s\n' "Automatic rollback failed: rollback snapshot is missing." >> "$log_path"
                exit 2
            fi

            find "$install_dir" -mindepth 1 -maxdepth 1 ! -name appsettings.json -exec rm -rf -- {} +
            cp -a "$rollback_dir"/. "$install_dir"/
            rm -f -- "$install_dir/rollback-version.txt" "$marker"
            printf '%s\n' "No confirming heartbeat arrived; previous binaries restored automatically." >> "$log_path"
            exit 4
            """;
        // sh reads a CR as part of the command: an agent built from a Windows checkout would
        // otherwise write a CRLF script and the guard would fail on its first line.
        File.WriteAllText(scriptPath, script.ReplaceLineEndings("\n"));
        return scriptPath;
    }
}
