// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http.Json;

namespace Aetheus.Agent.Core.Operations;

internal sealed class AgentReleaseArchiveResolver(
    IHttpClientFactory httpClientFactory,
    AetheusAgentOptions options)
{
    internal const long MaxArchiveBytes = 256L * 1024 * 1024;

    /// <summary>Diagnostic reported when the server publishes no manifest, or no archive for this platform (the
    /// downloads folder holds none): not a manifest that fails to read.</summary>
    internal const string ArchiveAbsentDiagnostic = "release archive absent";

    public async Task<AgentReleaseResolution> ResolveAsync(
        IReadOnlyDictionary<string, string>? envVars,
        bool isWindows,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        if (envVars is null
            || !envVars.TryGetValue("AETHEUS_AGENT_TARGET_VERSION", out var targetVersion)
            || string.IsNullOrWhiteSpace(targetVersion))
        {
            return AgentReleaseResolution.None;
        }

        using var client = httpClientFactory.CreateClient("AetheusServerTransfer");
        var manifestUrl = $"{options.ServerUrl.TrimEnd('/')}/downloads/agent-release-manifest.json";
        using var response = await client.GetAsync(manifestUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            await onOutput(
                $"Release manifest download failed: HTTP {(int)response.StatusCode} {response.ReasonPhrase}",
                TaskLogLevel.Error).ConfigureAwait(false);
            return response.StatusCode == System.Net.HttpStatusCode.NotFound
                ? AgentReleaseResolution.Failed(ArchiveAbsentDiagnostic)
                : AgentReleaseResolution.Failed(InvalidDiagnostic);
        }

        var manifest = await response.Content
            .ReadFromJsonAsync<AgentReleaseManifestDto>(cancellationToken: ct)
            .ConfigureAwait(false);
        // A backend without agent-release-manifest.json (a Development host with no release archives built)
        // answers a manifest with no archive at all: it is absent, not malformed.
        if (manifest is { Archives.Count: 0 })
        {
            await onOutput(
                "The server publishes no agent release archive (no agent-release-manifest.json or no archive in its downloads folder).",
                TaskLogLevel.Error).ConfigureAwait(false);
            return AgentReleaseResolution.Failed(ArchiveAbsentDiagnostic);
        }

        if (!IsValidManifest(manifest, targetVersion))
        {
            await onOutput(
                $"Release manifest does not describe requested version {targetVersion} and the supported protocol window.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return AgentReleaseResolution.Failed(InvalidDiagnostic);
        }

        var platform = isWindows ? "windows" : "linux";
        var archive = manifest!.Archives.SingleOrDefault(item =>
            item.Platform == platform && item.Architecture == "x64");
        if (archive is null)
        {
            await onOutput(
                $"Release manifest has no {platform}-x64 archive.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return AgentReleaseResolution.Failed(ArchiveAbsentDiagnostic);
        }

        if (!IsValidArchive(archive))
        {
            await onOutput(
                $"Release manifest has no valid {platform}-x64 archive.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return AgentReleaseResolution.Failed(InvalidDiagnostic);
        }

        return AgentReleaseResolution.Resolved(new ResolvedAgentReleaseArchive(
            manifest.SoftwareVersion,
            manifest.Commit.ToLowerInvariant(),
            archive));
    }

    internal const string InvalidDiagnostic = "release manifest invalid";

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

/// <summary>Outcome of the release lookup: the archive, or the fixed diagnostic reported when a target version
/// was asked and could not be resolved, or neither when no target version was asked.</summary>
internal sealed record AgentReleaseResolution(ResolvedAgentReleaseArchive? Release, string? FailureDiagnostic)
{
    public static AgentReleaseResolution None { get; } = new(null, null);

    public static AgentReleaseResolution Resolved(ResolvedAgentReleaseArchive release) => new(release, null);

    public static AgentReleaseResolution Failed(string diagnostic) => new(null, diagnostic);
}

internal sealed record ResolvedAgentReleaseArchive(
    string SoftwareVersion,
    string Commit,
    AgentReleaseArchiveDto Archive);
