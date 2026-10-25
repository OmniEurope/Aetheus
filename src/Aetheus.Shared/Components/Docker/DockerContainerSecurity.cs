// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Shared.Components.Docker;

/// <summary>Container settings that give a container a way out to the host (PLAN-010 lot 1).</summary>
[Flags]
public enum DockerContainerRisks
{
    None = 0,
    /// <summary><c>HostConfig.Privileged</c>: every device and capability of the host.</summary>
    Privileged = 1,
    /// <summary>The host Docker socket (or its directory) is bind-mounted: root on the host.</summary>
    DockerSocket = 2,
    /// <summary><c>PidMode: host</c>: the host's processes are visible and signalable.</summary>
    HostPid = 4,
    /// <summary><c>NetworkMode: host</c>: the host's network stack, loopback services included.</summary>
    HostNetwork = 8,
    /// <summary><c>CapAdd</c> holds a capability that escapes the container (see
    /// <see cref="DockerContainerSecurity.DangerousCapabilityNames"/>).</summary>
    DangerousCapabilities = 16
}

/// <summary>A container Aetheus itself starts and that needs some of the <see cref="DockerContainerRisks"/>.
/// Recognised by the agent and named in the interface; never a reason to hide the flags.</summary>
public enum AetheusContainerRole
{
    None = 0,
    /// <summary>The agent's own Buildx BuildKit container (<c>buildx_buildkit_{builder}N</c>): privileged by
    /// the docker-container driver.</summary>
    BuildKitBuilder = 1,
    /// <summary>The browser smoke run (label <c>io.aetheus.role=browser-smoke</c>): host network.</summary>
    BrowserSmoke = 2,
    /// <summary>The local VPS simulator test harness (compose service <c>vpssim</c>): privileged for systemd
    /// and the nested Docker daemon.</summary>
    VpsSimulator = 3,
    /// <summary>Ryuk, the Testcontainers reaper a pipeline step's tests start (R-33): privileged with the Docker
    /// socket by design. Recognised by the agent only for the exact <c>testcontainers/ryuk</c> image, the
    /// Testcontainers label and the <c>testcontainers-ryuk-{session}</c> name, created while one of its own
    /// pipeline tasks ran and no longer than shortly after that task; a look-alike outside a run stays
    /// flagged.</summary>
    TestcontainersReaper = 4
}

public static class DockerContainerSecurity
{
    /// <summary>Capabilities whose addition is reported (Docker may prefix them with <c>CAP_</c>).</summary>
    public static readonly IReadOnlyList<string> DangerousCapabilityNames =
        ["ALL", "SYS_ADMIN", "NET_ADMIN", "SYS_MODULE", "SYS_PTRACE", "SYS_RAWIO", "DAC_READ_SEARCH"];

    /// <summary>What the Aetheus role needs; anything beyond it is reported as for any other container.</summary>
    public static DockerContainerRisks ExpectedRisks(AetheusContainerRole role) => role switch
    {
        AetheusContainerRole.BuildKitBuilder => DockerContainerRisks.Privileged,
        AetheusContainerRole.BrowserSmoke => DockerContainerRisks.HostNetwork,
        AetheusContainerRole.VpsSimulator => DockerContainerRisks.Privileged,
        AetheusContainerRole.TestcontainersReaper => DockerContainerRisks.Privileged | DockerContainerRisks.DockerSocket,
        _ => DockerContainerRisks.None
    };

    /// <summary>The flags of <paramref name="container"/> its Aetheus role does not explain.</summary>
    public static DockerContainerRisks UnexpectedRisks(DockerContainerDto container)
    {
        ArgumentNullException.ThrowIfNull(container);
        return container.Risks & ~ExpectedRisks(container.AetheusRole);
    }

    /// <summary>Privileged, the socket, the host PID namespace, ALL or SYS_ADMIN are root on the host;
    /// the host network or another capability alone is a warning.</summary>
    public static bool IsCritical(DockerContainerRisks unexpected, string dangerousCapabilities)
    {
        if ((unexpected & (DockerContainerRisks.Privileged | DockerContainerRisks.DockerSocket
                | DockerContainerRisks.HostPid)) != DockerContainerRisks.None)
            return true;
        if ((unexpected & DockerContainerRisks.DangerousCapabilities) == DockerContainerRisks.None)
            return false;
        var caps = (dangerousCapabilities ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return caps.Contains("ALL", StringComparer.Ordinal) || caps.Contains("SYS_ADMIN", StringComparer.Ordinal);
    }
}
