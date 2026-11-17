// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.Servers;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>
/// Recette R-37 (user decision 2026-11-08): the one place that says which server tasks are blocking. A blocking task
/// veils the server page (content kept mounted) until it ends, because the host is not usable meanwhile: the system
/// package upgrade and a server reboot. Every other task, the agent update included, only shows its progress
/// (the task follower's block, the agent update card) and leaves the page usable.
/// </summary>
public static class ServerBlockingTasks
{
    /// <summary>The starts of the task names that veil the page.</summary>
    public static IReadOnlyList<string> Prefixes { get; } = [ServerTaskNames.PatchUpgrade, ServerTaskNames.ScheduledReboot];

    /// <summary>True when the task, by the name the backend gives it, is a blocking one.</summary>
    public static bool Veils(string taskName) =>
        Prefixes.Any(prefix => taskName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
}
