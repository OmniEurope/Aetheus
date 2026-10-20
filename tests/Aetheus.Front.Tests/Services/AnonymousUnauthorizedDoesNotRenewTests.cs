// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Front.Tests;

/// <summary>A 401 from an unauthenticated request must not trigger token renewal without
/// credentials.</summary>
public class AnonymousUnauthorizedDoesNotRenewTests
{
    [Fact]
    public async Task AnAnonymous401_SendsNoRenewalRequest()
    {
        var (handler, client, inner) = CreateSetup(token: null);

        var response = await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"),
            Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.DoesNotContain(inner.Paths, p => p.Contains("auth/renew", StringComparison.Ordinal));
        Assert.DoesNotContain(inner.Paths, p => p.Contains("auth/token/refresh", StringComparison.Ordinal));
        Assert.Single(inner.Paths);
        client.Dispose();
        handler.Dispose();
    }

    [Fact]
    public async Task A401WithATokenStillRenews()
    {
        // The guard must not disarm the real recovery path: a session that HAS a credential and gets
        // a 401 is exactly the expired-token case renewal exists for.
        var (handler, client, inner) = CreateSetup(token: "expired-token");

        await client.SendAsync(
            new HttpRequestMessage(HttpMethod.Get, "http://localhost/api/data"),
            Xunit.TestContext.Current.CancellationToken);

        Assert.Contains(inner.Paths, p => p.Contains("auth/renew", StringComparison.Ordinal));
        client.Dispose();
        handler.Dispose();
    }

    private static (AuthDelegatingHandler handler, HttpClient client, RecordingHandler inner) CreateSetup(string? token)
    {
        var jsMock = Substitute.For<Microsoft.JSInterop.IJSRuntime>();
        var authProvider = new AuthStateProvider(jsMock, NullLogger<AuthStateProvider>.Instance);
        if (token is not null)
            typeof(AuthStateProvider).GetProperty("Token")!.SetValue(authProvider, token);

        var inner = new RecordingHandler(HttpStatusCode.Unauthorized);
        var handler = new AuthDelegatingHandler(authProvider, NullLogger<AuthDelegatingHandler>.Instance)
        {
            InnerHandler = inner
        };
        return (handler, new HttpClient(handler), inner);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Paths.Add(request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(statusCode));
        }
    }
}
