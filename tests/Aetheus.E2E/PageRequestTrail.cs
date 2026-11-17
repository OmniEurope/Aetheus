// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Playwright;

namespace Aetheus.E2E;

/// <summary>
/// Follows the requests of one page: when each started, when it finished, its status or failure. The browser
/// runs in a container whose files are gone after the run, so a failing wait carries this text in its message.
/// </summary>
internal sealed class PageRequestTrail
{
    private const int SlowMilliseconds = 1000;
    private const int ShownSlowRequests = 8;

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentDictionary<IRequest, long> _pending = new();
    private readonly ConcurrentQueue<Finished> _finished = new();

    public PageRequestTrail(IPage page)
    {
        page.Request += (_, request) => _pending[request] = _clock.ElapsedMilliseconds;
        page.RequestFinished += (_, request) => Complete(request, "finished", null);
        page.RequestFailed += (_, request) => Complete(request, "failed", request.Failure);
        page.Response += (_, response) => _statuses[response.Request] = response.Status;
    }

    private readonly ConcurrentDictionary<IRequest, int> _statuses = new();

    private void Complete(IRequest request, string outcome, string? failure)
    {
        if (!_pending.TryRemove(request, out var startedAt)) return;
        _statuses.TryRemove(request, out var status);
        _finished.Enqueue(new Finished(
            request.Method, Shorten(request.Url), startedAt, _clock.ElapsedMilliseconds - startedAt, status, outcome, failure));
    }

    public string Describe()
    {
        var now = _clock.ElapsedMilliseconds;
        var lines = new List<string> { $"requests at +{now} ms: {_pending.Count} pending, {_finished.Count} done" };
        lines.AddRange(_pending.OrderBy(p => p.Value)
            .Select(p => $"  PENDING {now - p.Value} ms {p.Key.Method} {Shorten(p.Key.Url)}"));
        var done = _finished.ToArray();
        lines.AddRange(done.Where(f => f.Outcome == "failed" || f.Status >= 400)
            .Select(f => $"  {f.Outcome.ToUpperInvariant()} {f.Status} {f.Method} {f.Url} {f.Failure}"));
        lines.AddRange(done.Where(f => f.Milliseconds >= SlowMilliseconds && f.Url.StartsWith("/api/", StringComparison.Ordinal))
            .OrderByDescending(f => f.Milliseconds).Take(ShownSlowRequests)
            .Select(f => $"  SLOW +{f.StartedAt} ms took {f.Milliseconds} ms {f.Method} {f.Url}"));
        return string.Join('\n', lines);
    }

    private static string Shorten(string url)
    {
        var uri = new Uri(url);
        return uri.PathAndQuery.Length > 140 ? uri.PathAndQuery[..140] : uri.PathAndQuery;
    }

    private sealed record Finished(
        string Method, string Url, long StartedAt, long Milliseconds, int Status, string Outcome, string? Failure);
}
