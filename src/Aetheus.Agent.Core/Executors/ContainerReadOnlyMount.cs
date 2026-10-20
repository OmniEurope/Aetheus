// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Executors;

/// <summary>
/// An agent-owned host directory bind-mounted read-only into a step container (for example the
/// <c>aetheus-fn</c> executables). The step can run what it holds but never change it, so a step cannot
/// tamper with the code the agent ships.
/// </summary>
public sealed record ContainerReadOnlyMount(string HostPath, string ContainerPath)
{
    /// <summary>True when the container path is an absolute POSIX path that docker's <c>-v</c> syntax
    /// cannot misread (no <c>:</c> or <c>,</c>) and that stays clear of the <c>/w</c> workspace.</summary>
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(HostPath)
        && ContainerPath.StartsWith('/')
        && ContainerPath.Length > 1
        && !ContainerPath.Contains(':', StringComparison.Ordinal)
        && !ContainerPath.Contains(',', StringComparison.Ordinal)
        && !string.Equals(ContainerPath, "/w", StringComparison.Ordinal)
        && !ContainerPath.StartsWith("/w/", StringComparison.Ordinal);
}
