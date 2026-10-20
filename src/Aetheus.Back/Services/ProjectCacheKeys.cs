// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Services;

/// <summary>Shares project cache keys between read producers and write-path invalidators so Git
/// pushes and mirror synchronization invalidate exactly the values consumed by project
/// reads.</summary>
internal static class ProjectCacheKeys
{
    /// <summary>Per-project "last git update" timestamp (TTL ~10 min).</summary>
    public static string GitUpdate(int projectId) => $"projects:gitupdate:{projectId}";
}
