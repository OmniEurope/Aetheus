// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>Use a cached Trivy database only when it was built within <see cref="MaxAge"/>. Older,
/// missing or unreadable databases retain refresh to preserve freshness.</summary>
internal static class TrivyDatabaseFreshness
{
    /// <summary>The oldest vulnerability data a scan accepts without refreshing. The capability probe
    /// reports the agent degraded past 48 h; this stays well inside that.</summary>
    internal static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    internal const string SkipUpdateArgument = "--skip-db-update";

    private const long MaxMetadataBytes = 65_536;

    /// <summary>Only the scanners that download the database: Trivy, with the shared cache mounted.
    /// Trivy IaC declares no cache and never downloads it.</summary>
    internal static bool Applies(ScannerManifestEntry scanner) =>
        string.Equals(scanner.EntryPoint, "trivy", StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(scanner.CacheDirectoryName);

    /// <summary>Whether this scan runs on the cached database, said in the task log when it does.</summary>
    internal static async Task<bool> ShouldSkipUpdateAsync(
        ScannerManifestEntry scanner,
        string? cacheDirectory,
        DateTimeOffset now,
        Func<string, TaskLogLevel, Task> onOutput)
    {
        if (cacheDirectory is null || !Applies(scanner) || FreshUpdatedAt(cacheDirectory, now) is not { } updatedAt)
            return false;
        await onOutput(
            $"Trivy database built {updatedAt:O}, less than {MaxAge.TotalHours:0} h ago: "
            + "scanning with the cached database, no refresh.",
            TaskLogLevel.Info).ConfigureAwait(false);
        return true;
    }

    /// <summary>The build time of the cached database when it is younger than <see cref="MaxAge"/>,
    /// otherwise null.</summary>
    internal static DateTimeOffset? FreshUpdatedAt(string cacheDirectory, DateTimeOffset now)
    {
        var metadataPath = Path.Combine(cacheDirectory, "db", "metadata.json");
        try
        {
            var info = new FileInfo(metadataPath);
            if (!info.Exists || info.Length is <= 0 or > MaxMetadataBytes)
                return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(metadataPath), new JsonDocumentOptions { MaxDepth = 8 });
            // UpdatedAt is when the vulnerability data was built, which is what freshness is about;
            // DownloadedAt would only say when this agent fetched it.
            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("UpdatedAt", out var updated)
                || updated.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(updated.GetString(), out var updatedAt))
                return null;
            var age = now - updatedAt.ToUniversalTime();
            return age >= TimeSpan.Zero && age < MaxAge ? updatedAt.ToUniversalTime() : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}
