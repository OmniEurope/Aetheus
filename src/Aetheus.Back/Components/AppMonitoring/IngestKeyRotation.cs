// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>What a rotation changed: <paramref name="AffectedHashes"/> resolve differently now and must
/// leave the ingest key cache, whether they were demoted, dropped or newly issued.</summary>
internal readonly record struct IngestKeyRotationResult(
    string? PreviousHash,
    DateTime CreatedAt,
    IReadOnlyList<string> AffectedHashes);

internal static class IngestKeyRotation
{
    /// <summary>
    /// A rotation asked by an operator (generate a new key): the replaced key stays valid for the
    /// overlap window, and no older key survives it.
    /// </summary>
    public static IngestKeyRotationResult Apply(
        MonitoredApp app,
        string plaintext,
        IngestKeyHasher hasher,
        IConfiguration configuration,
        TimeProvider timeProvider) =>
        Rotate(app, plaintext, hasher, configuration, timeProvider, keepSecondPrevious: false);

    /// <summary>Deployment rotation retains keys for both live blue-green colours until their
    /// original overlap deadlines. Keeping the earlier key does not extend its lifetime.</summary>
    public static IngestKeyRotationResult ApplyForDeploy(
        MonitoredApp app,
        string plaintext,
        IngestKeyHasher hasher,
        IConfiguration configuration,
        TimeProvider timeProvider) =>
        Rotate(app, plaintext, hasher, configuration, timeProvider, keepSecondPrevious: true);

    private static IngestKeyRotationResult Rotate(
        MonitoredApp app,
        string plaintext,
        IngestKeyHasher hasher,
        IConfiguration configuration,
        TimeProvider timeProvider,
        bool keepSecondPrevious)
    {
        var affected = new List<string>(4);
        AddIfPresent(affected, app.SecondPreviousIngestKeyHash);
        AddIfPresent(affected, app.PreviousIngestKeyHash);

        var previousHash = app.IngestKeyHash;
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var newHash = hasher.Hash(plaintext);
        var overlapDays = Math.Clamp(
            configuration.GetValue(
                "AppMonitoring:IngestKeyOverlapDays",
                AppMonitoringDefaults.DefaultIngestKeyOverlapDays),
            0,
            AppMonitoringDefaults.MaximumIngestKeyOverlapDays);
        // A deployed key that is the previous one coming back (two runs of the same application
        // alternating) moves up without pushing the second previous key out, which a live colour may
        // still hold (R1-11, production refusals of 2026-10-21). A returning second previous key needs
        // nothing special: it leaves its slot as it becomes the current key.
        var previousReturns = string.Equals(app.PreviousIngestKeyHash, newHash, StringComparison.Ordinal);
        if (!keepSecondPrevious)
        {
            app.SecondPreviousIngestKeyHash = null;
            app.SecondPreviousIngestKeyValidUntil = null;
        }
        else if (!previousReturns)
        {
            app.SecondPreviousIngestKeyHash = app.PreviousIngestKeyHash;
            app.SecondPreviousIngestKeyValidUntil = app.PreviousIngestKeyHash is null
                ? null
                : app.PreviousIngestKeyValidUntil;
        }
        app.PreviousIngestKeyHash = previousHash;
        app.PreviousIngestKeyValidUntil = previousHash is null ? null : now.AddDays(overlapDays);
        app.IngestKeyHash = newHash;
        app.IngestKeyCreatedAt = now;
        app.IngestKeyExpiresAt = now.AddDays(AppMonitoringDefaults.MaximumIngestKeyLifetimeDays);
        app.IngestKeyVersion++;

        AddIfPresent(affected, previousHash);
        AddIfPresent(affected, newHash);
        return new IngestKeyRotationResult(previousHash, now, affected);
    }

    private static void AddIfPresent(List<string> hashes, string? hash)
    {
        if (hash is not null && !hashes.Contains(hash, StringComparer.Ordinal))
            hashes.Add(hash);
    }
}
