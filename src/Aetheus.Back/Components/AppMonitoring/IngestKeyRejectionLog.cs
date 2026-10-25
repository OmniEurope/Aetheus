// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.AppMonitoring;

/// <summary>Log the first ingestion-key refusal immediately and coalesce repeats by key, origin and <see
/// cref="Period"/>, reporting suppressed counts on the next period refusal. Identify the key only
/// through a hash prefix; name the sender through its origin so a stale emitter can be found.</summary>
public sealed class IngestKeyRejectionLog(TimeProvider time, ILogger<IngestKeyRejectionLog> logger)
{
    internal static readonly TimeSpan Period = TimeSpan.FromHours(1);

    /// <summary>Distinct keys tracked at once; beyond, refusals share the <see cref="OtherKeys"/> entry.</summary>
    internal const int MaxTrackedKeys = 256;

    internal const string MissingKey = "missing";
    internal const string OtherKeys = "other";
    internal const string UnknownOrigin = "unknown";
    private const int FingerprintLength = 12;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, (DateTime Start, int Suppressed)> _windows = new(StringComparer.Ordinal);

    /// <param name="keyHash">The hash of the refused key, or null when the request carried none.</param>
    /// <param name="origin">Where the request came from (address, user agent), or null when unknown. Refusals
    /// are coalesced per key and origin, so two senders holding the same stale key both show.</param>
    public void Record(string? keyHash, string? origin = null)
    {
        origin = string.IsNullOrWhiteSpace(origin) ? null : origin;
        var fingerprint = keyHash is null
            ? MissingKey
            : keyHash[..Math.Min(FingerprintLength, keyHash.Length)];
        var windowKey = origin is null ? fingerprint : $"{fingerprint}@{origin}";
        var now = time.GetUtcNow().UtcDateTime;
        DateTime? previousReport;
        int count;
        lock (_gate)
        {
            if (!_windows.ContainsKey(windowKey) && _windows.Count >= MaxTrackedKeys)
            {
                foreach (var expired in _windows.Where(entry => now - entry.Value.Start >= Period).Select(entry => entry.Key).ToList())
                    _windows.Remove(expired);
                if (_windows.Count >= MaxTrackedKeys)
                {
                    fingerprint = OtherKeys;
                    windowKey = OtherKeys;
                }
            }

            if (_windows.TryGetValue(windowKey, out var window) && now - window.Start < Period)
            {
                _windows[windowKey] = (window.Start, window.Suppressed + 1);
                return;
            }

            previousReport = window.Suppressed > 0 ? window.Start : null;
            count = window.Suppressed + 1;
            _windows[windowKey] = (now, 0);
        }

        if (previousReport is { } reportedAt)
            logger.LogWarning(
                "Telemetry ingest refused key {KeyFingerprint} {Count} more time(s) since the report of {ReportedAt:O}; the sender still holds no valid ingest key. Origin {Origin}",
                fingerprint,
                count,
                reportedAt,
                origin ?? UnknownOrigin);
        else
            logger.LogWarning(
                "Telemetry ingest refused key {KeyFingerprint}: the sender holds no valid ingest key. Origin {Origin}. Further refusals of this key from this origin are summarised once per {PeriodMinutes} minutes",
                fingerprint,
                origin ?? UnknownOrigin,
                Period.TotalMinutes);
    }
}
