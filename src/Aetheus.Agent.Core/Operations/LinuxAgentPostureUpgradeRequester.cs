// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Operations;

internal static class LinuxAgentPostureUpgradeRequester
{
    /// <summary>
    /// The tab-separated request line the root-owned supervisor reads: version, archive name, size,
    /// SHA-256, commit and, only when the update must also grant capabilities (R3-58), a sixth field of
    /// comma-separated whitelist tokens. Without capabilities the line stays the five-field form every
    /// installed supervisor accepts. The tokens must already be validated by the caller.
    /// </summary>
    internal static string BuildQualifiedRequest(
        ResolvedAgentReleaseArchive release,
        IReadOnlyList<string>? enableCapabilities = null)
    {
        var fields = new List<string>
        {
            release.SoftwareVersion,
            release.Archive.FileName,
            release.Archive.SizeBytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            release.Archive.Sha256.ToLowerInvariant(),
            release.Commit
        };
        if (enableCapabilities is { Count: > 0 })
            fields.Add(AgentEnableableCapabilities.Join(enableCapabilities));
        return string.Join('\t', fields);
    }

    internal static async Task<bool> TryRequestAsync(
        string workDirectory,
        string postureVersionPath,
        string requestPath,
        string expectedVersion,
        string qualifiedRequest,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(postureVersionPath)
            || !string.Equals(
                (await File.ReadAllTextAsync(postureVersionPath, cancellationToken)
                    .ConfigureAwait(false)).Trim(),
                expectedVersion,
                StringComparison.Ordinal))
        {
            await onOutput(
                "Self-update refused: the installed Linux integration posture does not support autonomous full upgrades.",
                TaskLogLevel.Error).ConfigureAwait(false);
            return false;
        }

        AgentUpdateRecoveryState.MarkPending(workDirectory);
        var temporaryRequest = requestPath + ".tmp";
        await File.WriteAllTextAsync(
            temporaryRequest,
            qualifiedRequest + "\n",
            cancellationToken).ConfigureAwait(false);
        File.Move(temporaryRequest, requestPath, overwrite: true);
        await onOutput(
            "Full Linux upgrade requested; the system supervisor will update binaries, systemd policy and privileged helpers.",
            TaskLogLevel.Info).ConfigureAwait(false);
        return true;
    }
}
