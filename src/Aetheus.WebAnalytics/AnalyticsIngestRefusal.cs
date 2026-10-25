// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;

namespace Aetheus.WebAnalytics;

/// <summary> Remembers that Aetheus refused the ingest key (HTTP 401 or 403, a permanent answer) so the
/// exporter does not send a batch again with that same key until <see cref="Backoff"/> has passed. A
/// different key (the configuration changed) is never held back. Transient answers (429, 5xx,
/// timeouts) are not recorded here: the exporter keeps its usual behaviour for them. </summary>
internal sealed class AnalyticsIngestRefusal(TimeProvider time)
{
    /// <summary>How long a refused key is left alone before one batch tries it again.</summary>
    internal static readonly TimeSpan Backoff = TimeSpan.FromHours(1);

    private readonly object _sync = new();
    private string? _refusedKey;
    private DateTimeOffset _retryAt;

    /// <summary>True while a batch sent with <paramref name="key"/> would only be refused again.</summary>
    internal bool IsSuspended(string key)
    {
        lock (_sync)
            return _refusedKey is not null && _refusedKey == Fingerprint(key) && time.GetUtcNow() < _retryAt;
    }

    /// <summary>Records a refusal; true when it is the first one for this key (the one to report).</summary>
    internal bool NoteRefused(string key)
    {
        var fingerprint = Fingerprint(key);
        lock (_sync)
        {
            var first = _refusedKey != fingerprint;
            _refusedKey = fingerprint;
            _retryAt = time.GetUtcNow() + Backoff;
            return first;
        }
    }

    /// <summary>Records an accepted batch; true when it ends a suspension.</summary>
    internal bool NoteAccepted()
    {
        lock (_sync)
        {
            var resumed = _refusedKey is not null;
            _refusedKey = null;
            return resumed;
        }
    }

    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
