// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// <c>curl -fsS</c> as the ported QA scripts called it: no redirect followed, an HTTP status of 400 or
/// more is a failure (<see cref="FunctionHttpException"/>), and no client timeout, since the step
/// timeout is the function's token. JSON bodies are sent as <c>Content-Type: application/json</c> and a
/// bearer token, when given, as <c>Authorization: Bearer</c>.
/// </summary>
internal sealed class FunctionHttp(HttpClient client)
{
    /// <summary>The client outside tests: redirects stay unfollowed, as curl without <c>-L</c>, curl's
    /// default connection timeout of 300 seconds, and no overall timeout.</summary>
    public static HttpClient Shared { get; } = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        ConnectTimeout = TimeSpan.FromSeconds(300),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    })
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    public Task<string> GetAsync(string url, string? bearer, CancellationToken cancellationToken) =>
        SendAsync(new HttpRequestMessage(HttpMethod.Get, url), bearer, cancellationToken);

    public Task<string> PostJsonAsync(string url, string json, string? bearer, CancellationToken cancellationToken) =>
        SendAsync(
            new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") },
            bearer,
            cancellationToken);

    private async Task<string> SendAsync(HttpRequestMessage request, string? bearer, CancellationToken cancellationToken)
    {
        using (request)
        {
            if (bearer is not null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            HttpResponseMessage response;
            try
            {
                response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                throw new FunctionHttpException($"{request.Method} {request.RequestUri} failed: {ex.Message}", ex);
            }
            using (response)
            {
                if ((int)response.StatusCode >= 400)
                    throw new FunctionHttpException($"{request.Method} {request.RequestUri} answered HTTP {(int)response.StatusCode}.");
                return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}

/// <summary>What <c>curl -f</c> exits non-zero for: no answer, or an HTTP error status.</summary>
internal sealed class FunctionHttpException : Exception
{
    public FunctionHttpException(string message)
        : base(message)
    {
    }

    public FunctionHttpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
