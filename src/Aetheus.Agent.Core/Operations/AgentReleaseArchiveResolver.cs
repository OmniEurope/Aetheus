// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;

namespace Aetheus.Agent.Core.Operations;

internal sealed class AgentReleaseArchiveResolver(
    IHttpClientFactory httpClientFactory,
    AetheusAgentOptions options)
{
    internal const long MaxArchiveBytes = 256L * 1024 * 1024;

    public async Task<ResolvedAgentReleaseArchive?> ResolveAsync(
        IReadOnlyDictionary<string, string>? envVars,
        bool isWindows,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (envVars is null
            || !envVars.TryGetValue("AETHEUS_AGENT_TARGET_VERSION", out var targetVersion)
            || string.IsNullOrWhiteSpace(targetVersion))
        {
            return null;
        }

        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");
        var manifestUrl = $"{options.ServerUrl.TrimEnd('/')}/downloads/agent-release-manifest.json";
        using var response = await client.GetAsync(manifestUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await onOutput(
                $"Release manifest download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }

        var manifest = await response.Content
            .ReadFromJsonAsync<AgentReleaseManifestDto>(cancellationToken: ct)
            .ConfigureAwait(false);
        if (!IsValidManifest(manifest, targetVersion))
        {
            await onOutput(
                $"Release manifest does not describe requested version {targetVersion} and the supported protocol window.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }

        var platform = isWindows ? "windows" : "linux";
        var archive = manifest!.Archives.SingleOrDefault(item =>
            item.Platform == platform && item.Architecture == "x64");
        if (!IsValidArchive(archive))
        {
            await onOutput(
                $"Release manifest has no valid {platform}-x64 archive.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return null;
        }

        return new ResolvedAgentReleaseArchive(
            manifest!.SoftwareVersion,
            manifest.Commit.ToLowerInvariant(),
            archive!);
    }

    private static bool IsValidManifest(AgentReleaseManifestDto? manifest, string targetVersion) =>
        manifest is not null
        && string.Equals(manifest.SoftwareVersion, targetVersion, StringComparison.Ordinal)
        && manifest.ProtocolVersion == AgentProtocol.CurrentVersion
        && manifest.MinimumSupportedProtocol <= AgentProtocol.CurrentVersion
        && manifest.MaximumSupportedProtocol >= AgentProtocol.CurrentVersion
        && manifest.Commit.Length is 40 or 64
        && manifest.Commit.All(Uri.IsHexDigit);

    private static bool IsValidArchive(AgentReleaseArchiveDto? archive) =>
        archive is not null
        && Path.GetFileName(archive.FileName) == archive.FileName
        && archive.SizeBytes > 0
        && archive.SizeBytes <= MaxArchiveBytes
        && archive.Sha256.Length == 64
        && archive.Sha256.All(Uri.IsHexDigit);
}

internal sealed record ResolvedAgentReleaseArchive(
    string SoftwareVersion,
    string Commit,
    AgentReleaseArchiveDto Archive);
