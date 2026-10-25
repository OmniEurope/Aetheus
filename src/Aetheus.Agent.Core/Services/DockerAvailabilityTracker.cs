// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Services;

/// <summary>
/// Turns successive <see cref="DockerProbeOutcome"/>s into the Docker availability the heartbeat
/// reports (R-31). The backend refuses container-isolated stages while it reads <c>false</c>, so a
/// daemon that is merely slow to answer during heavy builds must not flip the capability: an
/// <see cref="DockerProbeOutcome.Indeterminate"/> probe keeps the last confirmed state, but only
/// within <see cref="AgentRuntimeDefaults.DockerProbeIndeterminateHold"/> of that confirmation, so a
/// daemon that stays hung is reported unavailable. A definitive
/// <see cref="DockerProbeOutcome.Unavailable"/> answer is reported at once (fail-closed), and with
/// no confirmed state yet the tracker reports <c>false</c>.
/// </summary>
public sealed class DockerAvailabilityTracker(TimeProvider time)
{
    private readonly object _sync = new();
    private bool _confirmedAvailable;
    private DateTimeOffset? _confirmedAt;

    public bool Observe(DockerProbeOutcome outcome)
    {
        lock (_sync)
        {
            var now = time.GetUtcNow();
            switch (outcome)
            {
                case DockerProbeOutcome.Available:
                    _confirmedAvailable = true;
                    _confirmedAt = now;
                    return true;
                case DockerProbeOutcome.Unavailable:
                    _confirmedAvailable = false;
                    _confirmedAt = now;
                    return false;
                default:
                    return _confirmedAvailable
                        && _confirmedAt is { } confirmedAt
                        && now - confirmedAt <= AgentRuntimeDefaults.DockerProbeIndeterminateHold;
            }
        }
    }
}
