// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The verification that the former <c>extract-agent-release-from-image.sh</c> script ran as inline Node.js on
/// an extracted agent release: the manifest names the expected commit, speaks protocol 2 within the
/// range the policy requires, publishes the <c>ai.run</c> capability, and every archive it declares
/// has exactly the declared size and SHA-256. Each refusal carries the script's message.
/// </summary>
internal static class AgentReleaseManifestCheck
{
    /// <summary>Null when the release in <paramref name="directory"/> is sound, else why it is not.</summary>
    public static async Task<string?> VerifyAsync(
        string directory, string expectedCommit, string protocolPolicy, CancellationToken cancellationToken)
    {
        var manifestPath = Path.Combine(directory, AgentReleaseManifest.FileName);
        if (!File.Exists(manifestPath))
            return $"Agent manifest {manifestPath} was not found in the image downloads.";

        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        var manifest = document.RootElement;
        if (manifest.ValueKind != JsonValueKind.Object)
            return "Agent manifest is not a JSON object.";

        var commit = String(manifest, "commit");
        if (!string.Equals(commit, expectedCommit, StringComparison.Ordinal))
            return $"Agent manifest commit {commit ?? "undefined"} does not match {expectedCommit}.";
        return ProtocolProblem(manifest, protocolPolicy)
               ?? CapabilityProblem(manifest)
               ?? await ArchiveProblemAsync(manifest, directory, cancellationToken).ConfigureAwait(false);
    }

    private static string? ProtocolProblem(JsonElement manifest, string protocolPolicy)
    {
        var minimum = Number(manifest, "minimumSupportedProtocol");
        var maximum = Number(manifest, "maximumSupportedProtocol");
        var upgradeBridge = minimum == 1 && maximum == 2;
        var historicalCurrentOnly = minimum == 2 && maximum == 2;
        if (Number(manifest, "protocolVersion") != 2 || (protocolPolicy == "upgrade-bridge" && !upgradeBridge))
            return "Agent manifest does not expose the required protocol 1-2 upgrade bridge.";
        if (protocolPolicy == "historical-compatible" && !upgradeBridge && !historicalCurrentOnly)
            return "Historical agent manifest is not compatible with protocol 2.";
        return null;
    }

    private static string? CapabilityProblem(JsonElement manifest)
    {
        if (!manifest.TryGetProperty("softwareCapabilities", out var capabilities) || capabilities.ValueKind != JsonValueKind.Array)
            return "Agent manifest does not list its software capabilities.";
        return capabilities.EnumerateArray().Any(capability =>
            capability.ValueKind == JsonValueKind.String && capability.GetString() == "ai.run")
            ? null
            : "Agent manifest does not publish the ai.run capability.";
    }

    private static async Task<string?> ArchiveProblemAsync(JsonElement manifest, string directory, CancellationToken cancellationToken)
    {
        if (!manifest.TryGetProperty("archives", out var archives) || archives.ValueKind != JsonValueKind.Array)
            return "Agent manifest does not list its archives.";
        foreach (var archive in archives.EnumerateArray())
        {
            var fileName = archive.ValueKind == JsonValueKind.Object ? String(archive, "fileName") : null;
            var declaredSha = archive.ValueKind == JsonValueKind.Object ? String(archive, "sha256") : null;
            if (fileName is null || declaredSha is null)
                return "Agent manifest contains an invalid archive entry.";
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
                return $"Agent archive {fileName} is missing.";
            var bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
            var sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (Number(archive, "sizeBytes") != bytes.Length || sha256 != declaredSha.ToLowerInvariant())
                return $"Agent archive integrity mismatch: {fileName}.";
        }
        return null;
    }

    private static string? String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>The script compared with <c>===</c> against numbers: anything but a JSON number never
    /// matches.</summary>
    private static double? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
}
