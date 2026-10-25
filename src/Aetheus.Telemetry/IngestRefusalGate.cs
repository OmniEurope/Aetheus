// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Aetheus.Telemetry;

/// <summary> Remembers that Aetheus refused the ingest key (HTTP 401 or 403, a permanent answer) and
/// keeps the exporters from sending again with that same key until <see cref="Backoff"/> has passed,
/// instead of repeating a refused request at every export interval. Another key (the configuration
/// changed) is never held back. Transient answers (429, 5xx, timeouts) leave the gate untouched, so
/// the exporters keep their usual retry. One gate is shared by every exporter of the application,
/// as they all send the same key. </summary>
internal sealed class IngestRefusalGate(TimeProvider time)
{
    /// <summary>How long a refused key is left alone before a single request tries it again.</summary>
    internal static readonly TimeSpan Backoff = TimeSpan.FromHours(1);

    private readonly Lock _sync = new();
    private string? _refusedKey;
    private DateTimeOffset _retryAt;

    /// <summary>Where the notices go. Set once the host has a logging factory; a notice with no logger
    /// is not written.</summary>
    internal ILogger? Logger { get; set; }

    /// <summary>True while a request carrying <paramref name="key"/> would only be refused again.</summary>
    internal bool IsBlocked(string key)
    {
        lock (_sync)
            return _refusedKey is not null && _refusedKey == Fingerprint(key) && time.GetUtcNow() < _retryAt;
    }

    /// <summary>Takes note of Aetheus' answer to a request sent with <paramref name="key"/>.</summary>
    internal void Observe(string key, Uri? endpoint, HttpStatusCode status, string emitter)
    {
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            Refused(key, endpoint, status, emitter);
        else if ((int)status is >= 200 and < 300)
            Accepted(endpoint);
    }

    private void Refused(string key, Uri? endpoint, HttpStatusCode status, string emitter)
    {
        var fingerprint = Fingerprint(key);
        bool first;
        lock (_sync)
        {
            first = _refusedKey != fingerprint;
            _refusedKey = fingerprint;
            _retryAt = time.GetUtcNow() + Backoff;
        }

        // The key itself is never written; the endpoint (configuration, not a secret) and the emitter
        // let the operator find which application still holds a stale key.
        if (first)
            Logger?.LogWarning(
                "Aetheus telemetry: {Endpoint} refused the ingest key (HTTP {Status}). The key is no longer valid for this application (rotated, expired, or from another environment). Sending is suspended for {PeriodMinutes} minutes, then tried once again; set the current key and restart the application to resume now. Emitter {Emitter}",
                Describe(endpoint),
                (int)status,
                Backoff.TotalMinutes,
                emitter);
        else
            Logger?.LogDebug(
                "Aetheus telemetry: {Endpoint} refused the ingest key again (HTTP {Status}); still suspended",
                Describe(endpoint),
                (int)status);
    }

    private void Accepted(Uri? endpoint)
    {
        bool resumed;
        lock (_sync)
        {
            resumed = _refusedKey is not null;
            _refusedKey = null;
        }

        if (resumed)
            Logger?.LogInformation("Aetheus telemetry: {Endpoint} accepts the ingest key again; sending resumed", Describe(endpoint));
    }

    private static string Describe(Uri? endpoint) =>
        endpoint is null ? "the ingest endpoint" : endpoint.GetLeftPart(UriPartial.Path);

    private static string Fingerprint(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
