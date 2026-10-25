#!/usr/bin/env sh
# SPDX-License-Identifier: EUPL-1.2
set -eu

downloads_path="${1:?downloads path is required}"
software_version="${2:?software version is required}"
commit="${3:?commit is required}"

case "$software_version" in
  *[!0-9A-Za-z.+-]*|'') echo "Invalid software version" >&2; exit 2 ;;
esac
case "${#commit}" in 40|64) ;; *) echo "Commit must be a full immutable SHA." >&2; exit 2 ;; esac
case "$commit" in *[!0-9a-fA-F]*) echo "Commit must be hexadecimal." >&2; exit 2 ;; esac

linux_name="aetheus-agent-linux-x64-v${software_version}.tar.gz"
windows_name="aetheus-agent-win-x64-v${software_version}.zip"
linux_source="${downloads_path}/${linux_name}"
windows_source="${downloads_path}/${windows_name}"
test -f "$linux_source"
test -f "$windows_source"

release_path="${downloads_path}/releases/${software_version}"
mkdir -p "$release_path"
cp "$linux_source" "${release_path}/${linux_name}"
cp "$windows_source" "${release_path}/${windows_name}"

linux_size="$(stat -c %s "$linux_source")"
windows_size="$(stat -c %s "$windows_source")"
linux_sha="$(sha256sum "$linux_source" | cut -d ' ' -f 1)"
windows_sha="$(sha256sum "$windows_source" | cut -d ' ' -f 1)"
produced_at="$(date -u +%Y-%m-%dT%H:%M:%SZ)"

manifest="${downloads_path}/agent-release-manifest.json"
# Keep the bridge window until every protocol-2 agent with the historical
# 1-2 validator has consumed a release containing the range-aware validator.
printf '%s\n' \
  '{' \
  "  \"softwareVersion\": \"${software_version}\"," \
  '  "protocolVersion": 2,' \
  '  "minimumSupportedProtocol": 1,' \
  '  "maximumSupportedProtocol": 2,' \
  '  "softwareCapabilities": [' \
  '    "agent.self-update",' \
  '    "ai.run",' \
  '    "analysis.run",' \
  '    "artifact.collect",' \
  '    "artifact.restore",' \
  '    "pipeline.build",' \
  '    "shell.execute"' \
  '  ],' \
  '  "archives": [' \
  '    {' \
  '      "platform": "linux",' \
  '      "architecture": "x64",' \
  "      \"fileName\": \"${linux_name}\"," \
  "      \"sizeBytes\": ${linux_size}," \
  "      \"sha256\": \"${linux_sha}\"" \
  '    },' \
  '    {' \
  '      "platform": "windows",' \
  '      "architecture": "x64",' \
  "      \"fileName\": \"${windows_name}\"," \
  "      \"sizeBytes\": ${windows_size}," \
  "      \"sha256\": \"${windows_sha}\"" \
  '    }' \
  '  ],' \
  "  \"producedAtUtc\": \"${produced_at}\"," \
  "  \"commit\": \"${commit}\"" \
  '}' > "$manifest"
