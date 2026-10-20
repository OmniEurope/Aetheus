// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Text;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A QA backend held in memory for the functions that drive it over HTTP: each <c>METHOD path</c> is
/// answered by a status and a JSON body, or by <see cref="Respond"/> for answers that change between
/// polls; an unknown route answers 404 and <see cref="Refuse"/> makes a route unreachable. Every
/// request is recorded with its bearer token and body.
/// </summary>
internal sealed class FakeQaBackend : HttpMessageHandler
{
    public Dictionary<string, (HttpStatusCode Status, string Body)> Routes { get; } = new(StringComparer.Ordinal);
    public Func<string, (HttpStatusCode Status, string Body)?> Respond { get; set; } = _ => null;
    public HashSet<string> Refuse { get; } = new(StringComparer.Ordinal);
    public List<(string Route, string? Bearer, string? Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = $"{request.Method} {request.RequestUri!.PathAndQuery}";
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (Requests)
            Requests.Add((route, request.Headers.Authorization?.Parameter, body));
        if (Refuse.Contains(route))
            throw new HttpRequestException("Connection refused");
        var (status, content) = Respond(route) ?? (Routes.TryGetValue(route, out var answer) ? answer : (HttpStatusCode.NotFound, "{}"));
        return new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") };
    }
}
