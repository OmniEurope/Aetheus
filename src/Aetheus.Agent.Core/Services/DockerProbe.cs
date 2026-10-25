// SPDX-License-Identifier: EUPL-1.2
using System.Runtime.InteropServices;


namespace Aetheus.Agent.Core.Services;

/// <summary>What one Docker probe established.</summary>
public enum DockerProbeOutcome
{
    /// <summary>A daemon answered <c>docker info</c> with a server version.</summary>
    Available,
    /// <summary>No candidate binary could reach a daemon: binary missing, daemon down or permission denied.</summary>
    Unavailable,
    /// <summary>A binary was found but <c>docker info</c> did not answer within the probe timeout (a
    /// busy daemon, typically during heavy builds): the probe proves neither presence nor absence.</summary>
    Indeterminate
}

/// <summary>
/// Detects whether the agent host can actually RUN containers. Reported as a server capability so
/// the backend can gate container-isolated pipeline stages: a stage requesting
/// <c>isolation: container</c> only schedules onto a runner where this returns true (otherwise the
/// run is blocked, never silently downgraded to direct process execution).
/// <para>
/// "Can run containers" means more than "the binary exists": the probe runs <c>docker info</c>,
/// which only exits 0 when the daemon is reachable AND the agent user has socket permission (i.e.
/// is in the <c>docker</c> group). A box with the CLI installed but the agent outside the docker
/// group correctly reports <c>false</c> - that's the fail-closed signal an operator needs.
/// </para>
/// <para>
/// It also probes several absolute paths, not just <c>docker</c> on <c>PATH</c>: the agent runs
/// under a hardened systemd unit whose minimal <c>PATH</c> excludes <c>/snap/bin</c>, so a
/// snap-installed Docker would otherwise be invisible.
/// </para>
/// </summary>
public static class DockerProbe
{
    // Probed in order; the first that reports a reachable daemon wins. "docker" (PATH) first so a
    // correctly-configured host resolves immediately; absolute fallbacks cover minimal-PATH units
    // and snap installs.
    private static readonly string[] LinuxCandidates =
        ["docker", "/usr/bin/docker", "/usr/local/bin/docker", "/snap/bin/docker"];

    public static async Task<bool> IsAvailableAsync(IShellRunner shell, CancellationToken ct = default)
    {
        try
        {
            return await ProbeAsync(shell, ct).ConfigureAwait(false) == DockerProbeOutcome.Available;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Callers of the boolean form have always read a cancelled probe as "not available".
            return false;
        }
    }

    /// <summary>Probes the candidates and says whether Docker answered, is absent, or did not answer
    /// in time. Cancellation of <paramref name="ct"/> is propagated rather than read as absence.</summary>
    public static async Task<DockerProbeOutcome> ProbeAsync(IShellRunner shell, CancellationToken ct = default)
    {
        var candidates = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? ["docker"]
            : LinuxCandidates;

        foreach (var docker in candidates)
        {
            var outcome = await ProbeCandidateAsync(shell, docker, ct).ConfigureAwait(false);
            // A timed-out candidate is a binary that exists in front of a slow daemon: the other paths
            // reach the same daemon, so probing them would only double the wait past the heartbeat budget.
            if (outcome != DockerProbeOutcome.Unavailable)
                return outcome;
        }
        return DockerProbeOutcome.Unavailable;
    }

    private static async Task<DockerProbeOutcome> ProbeCandidateAsync(IShellRunner shell, string docker, CancellationToken ct)
    {
        try
        {
            // `info --format {{.ServerVersion}}` is tiny and exits 0 only when the daemon answers.
            var result = await shell.RunExecAsync(
                docker, ["info", "--format", "{{.ServerVersion}}"], ct, AgentRuntimeDefaults.CapabilityProbeTimeout).ConfigureAwait(false);
            return result.ExitCode == 0 && !string.IsNullOrWhiteSpace(result.StdOut)
                ? DockerProbeOutcome.Available
                : DockerProbeOutcome.Unavailable;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The probe's own timeout fired: the process started, so the binary exists, but the daemon
            // did not answer in time. That is not evidence that Docker is gone.
            return DockerProbeOutcome.Indeterminate;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Binary not found on this candidate path, daemon down, or permission denied - try the next.
            return DockerProbeOutcome.Unavailable;
        }
    }
}
