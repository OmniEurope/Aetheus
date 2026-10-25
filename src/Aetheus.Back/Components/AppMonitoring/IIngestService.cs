// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

public readonly record struct IngestOutcome(int Accepted, int Dropped);

public interface IIngestService
{
    /// <summary>Resolves an OTLP ingest key (plaintext header value) to its app id, cached. Null = invalid/unknown.</summary>
    Task<int?> ResolveAppIdAsync(string ingestKey, CancellationToken ct = default);

    Task<IngestOutcome> IngestMetricsAsync(int appId, IReadOnlyList<ParsedMetricPoint> points, CancellationToken ct = default);
    Task<IngestOutcome> IngestLogsAsync(int appId, IReadOnlyList<ParsedLogRecord> records, CancellationToken ct = default);
    Task<IngestOutcome> IngestErrorsAsync(int appId, IReadOnlyList<ParsedError> errors, CancellationToken ct = default);

    /// <summary>Evicts the cached key-hash -> app-id mapping (positive OR negative) so a revoked/rotated key
    /// stops resolving immediately instead of lingering for the cache TTL. Pass the SHA-256 hash of the key.</summary>
    void InvalidateKeyCache(string ingestKeyHash);
}
