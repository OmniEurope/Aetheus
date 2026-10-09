// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Components.Audit;

public class AuditChainService(IAuditRepository repo) : IAuditChainService
{
    private const long TicksPerMicrosecond = TimeSpan.TicksPerMillisecond / 1000;

    /// <summary>
    /// v3 payload (audit SITE-SEC-003/004): adds <see cref="AuditLog.EntityId"/>, which v2 left out so
    /// an entry could be re-pointed at another entity undetected, and hashes a timestamp already
    /// truncated to the microsecond by the writer (<see cref="ToStoredPrecision"/>): PostgreSQL stores
    /// microseconds, so a v2 hash taken over .NET ticks (100 ns) could not be recomputed from the row
    /// read back. Like v2, it excludes the Id so the hash is computed before the single insert.
    /// </summary>
    public string ComputeHash(AuditLog entry, string previousHash)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return Sha256(string.Join('|',
            "v3",
            entry.Timestamp.ToString("O", CultureInfo.InvariantCulture),
            entry.Username,
            entry.Action,
            entry.EntityType,
            entry.EntityId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            entry.Details ?? string.Empty,
            previousHash));
    }

    /// <summary>Truncates a timestamp to the microsecond PostgreSQL keeps, so the hashed value is the stored one.</summary>
    public static DateTime ToStoredPrecision(DateTime timestamp) =>
        new(timestamp.Ticks - (timestamp.Ticks % TicksPerMicrosecond), timestamp.Kind);

    /// <summary>v2 formula (no EntityId, untruncated timestamp) - verification-only.</summary>
    private static string ComputeV2Hash(AuditLog entry, DateTime timestamp, string previousHash) =>
        Sha256(string.Join('|',
            "v2",
            timestamp.ToString("O", CultureInfo.InvariantCulture),
            entry.Username,
            entry.Action,
            entry.EntityType,
            entry.Details ?? string.Empty,
            previousHash));

    /// <summary>Pre-v2 formula (included Id, no EntityType) - verification-only.</summary>
    private static string ComputeLegacyHash(AuditLog entry, DateTime timestamp, string previousHash) =>
        Sha256(string.Join('|',
            entry.Id,
            timestamp.ToString("O", CultureInfo.InvariantCulture),
            entry.Username,
            entry.Action,
            entry.Details ?? string.Empty,
            previousHash));

    /// <summary>
    /// A v2 or legacy row was hashed over the full .NET timestamp, of which the database kept only the
    /// microseconds: the lost part is 0 to 9 ticks, so each candidate is tried (ten SHA-256 per old
    /// row). This also covers the rows the previous colour still writes during a blue-green switch. The
    /// tolerance never applies to v3, whose writer truncates before hashing.
    /// </summary>
    private static bool MatchesOlderFormula(AuditLog entry, string previousHash)
    {
        for (var lostTicks = 0; lostTicks < TicksPerMicrosecond; lostTicks++)
        {
            var timestamp = entry.Timestamp.AddTicks(lostTicks);
            if (entry.Hash == ComputeV2Hash(entry, timestamp, previousHash) ||
                entry.Hash == ComputeLegacyHash(entry, timestamp, previousHash))
                return true;
        }
        return false;
    }

    private static string Sha256(string payload) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

    public async Task<AuditChainVerificationResult> VerifyChainAsync(CancellationToken ct = default)
    {
        return (await VerifyAsync(targetEntryId: null, ct).ConfigureAwait(false))!;
    }

    public async Task<AuditChainVerificationResult?> VerifyUpToEntryAsync(int entryId, CancellationToken ct = default)
    {
        return await VerifyAsync(entryId, ct).ConfigureAwait(false);
    }

    private async Task<AuditChainVerificationResult?> VerifyAsync(int? targetEntryId, CancellationToken ct)
    {
        // F-006: streamed verification - bounded memory regardless of table size.
        var previousHash = string.Empty;
        var count = 0;

        await foreach (var entry in repo.StreamOrderedAsync().WithCancellation(ct).ConfigureAwait(false))
        {
            count++;

            var failure = ValidateEntry(entry, previousHash, count);
            if (failure is not null)
                return failure;

            // Reached the target with every preceding link intact: the entry is verified.
            if (entry.Id == targetEntryId)
                return new AuditChainVerificationResult { IsValid = true, TotalEntries = count };

            previousHash = entry.Hash;
        }

        return targetEntryId.HasValue
            ? null
            : new AuditChainVerificationResult { IsValid = true, TotalEntries = count };
    }

    private AuditChainVerificationResult? ValidateEntry(AuditLog entry, string previousHash, int count)
    {
        if (entry.PreviousHash != previousHash)
            return InvalidEntry(entry, count, "PreviousHash mismatch");

        if (entry.Hash != ComputeHash(entry, previousHash) && !MatchesOlderFormula(entry, previousHash))
            return InvalidEntry(entry, count, "Hash mismatch");

        return null;
    }

    private static AuditChainVerificationResult InvalidEntry(AuditLog entry, int count, string reason) => new()
    {
        IsValid = false,
        TotalEntries = count,
        FirstInvalidId = entry.Id,
        ErrorMessage = $"{reason} at entry {entry.Id}"
    };
}
