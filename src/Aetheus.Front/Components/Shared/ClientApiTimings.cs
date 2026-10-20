// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Aetheus.Front.Components.Shared;

/// <summary>One API call as this browser lived it: network and server together.</summary>
public sealed record ClientApiCall(DateTime At, string Method, string Path, int StatusCode, double DurationMs, long? Bytes);

/// <summary>Measure browser API wait time so the Performance page can compare it with server
/// timing; normalize path identifiers and discard the query to group endpoints without retaining
/// values.</summary>
public sealed partial class ClientApiTimings
{
    /// <summary>A session is short; the last few hundred calls say where the waiting went.</summary>
    public const int Capacity = 500;

    private readonly LinkedList<ClientApiCall> _calls = new();
    private readonly Lock _gate = new();

    public void Record(ClientApiCall call)
    {
        lock (_gate)
        {
            _calls.AddLast(call);
            if (_calls.Count > Capacity) _calls.RemoveFirst();
        }
    }

    public IReadOnlyList<ClientApiCall> Snapshot()
    {
        lock (_gate) return [.. _calls];
    }

    /// <summary><c>/api/pipelines/51/runs?page=2</c> becomes <c>api/pipelines/{id}/runs</c>.</summary>
    public static string Normalize(Uri? uri)
    {
        var path = uri is null ? string.Empty : (uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', '#')[0]);
        return IdentifierSegment().Replace(path.Trim('/'), "{id}");
    }

    [GeneratedRegex(@"(?<=^|/)(\d+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}|[0-9a-fA-F]{40})(?=/|$)")]
    private static partial Regex IdentifierSegment();
}

/// <summary>Times each call of the API client; the outermost handler, so retries count too. A call
/// that ends without an answer (network failure, timeout after the retries) is kept with status 0:
/// it is the longest wait of all. A call the page itself cancelled is not a wait and is left out.</summary>
public sealed class ClientApiTimingHandler(ClientApiTimings timings) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var at = DateTime.UtcNow;
        HttpResponseMessage? response = null;
        try
        {
            response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            return response;
        }
        finally
        {
            if (response is not null || !cancellationToken.IsCancellationRequested)
            {
                timings.Record(new ClientApiCall(
                    at,
                    request.Method.Method,
                    ClientApiTimings.Normalize(request.RequestUri),
                    response is null ? 0 : (int)response.StatusCode,
                    Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                    response?.Content.Headers.ContentLength));
            }
        }
    }
}
