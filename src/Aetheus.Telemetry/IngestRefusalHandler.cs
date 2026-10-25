// SPDX-License-Identifier: EUPL-1.2
using System.Net;

namespace Aetheus.Telemetry;

/// <summary> Sits under the HTTP client of every OTLP exporter and of the configuration read. It
/// names the emitter in the <c>User-Agent</c> (Aetheus logs it with each refusal) and consults the
/// shared <see cref="IngestRefusalGate"/>: once the ingest key is refused it answers 401 locally,
/// without a request, until the back-off ends or the key changes. The SDK exporters treat a 401 as
/// final and drop the batch, so nothing is retried against Aetheus meanwhile. </summary>
internal sealed class IngestRefusalHandler(IngestRefusalGate gate, string emitter, HttpMessageHandler inner)
    : DelegatingHandler(inner)
{
    private const string KeyHeader = "x-aetheus-ingest-key";

    protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var key = Prepare(request);
        if (gate.IsBlocked(key))
            return Suspended(request);
        var response = base.Send(request, cancellationToken);
        gate.Observe(key, request.RequestUri, response.StatusCode, emitter);
        return response;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var key = Prepare(request);
        if (gate.IsBlocked(key))
            return Suspended(request);
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        gate.Observe(key, request.RequestUri, response.StatusCode, emitter);
        return response;
    }

    private string Prepare(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", emitter);
        return request.Headers.TryGetValues(KeyHeader, out var values) ? values.FirstOrDefault() ?? string.Empty : string.Empty;
    }

    private static HttpResponseMessage Suspended(HttpRequestMessage request) =>
        new(HttpStatusCode.Unauthorized)
        {
            RequestMessage = request,
            ReasonPhrase = "Ingest key refused earlier; not sent again until the back-off ends"
        };
}
