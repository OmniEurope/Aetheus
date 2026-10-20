// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// An unreachable hub (every start fails at once) that keeps each connection it built, so a test can
/// deliver its messages through <see cref="HubHandlerInvoker"/> or count the connections a component opened.
/// </summary>
internal sealed class CapturingHubConnectionFactory(IServiceProvider services) : HubConnectionFactory(
    services.GetRequiredService<IConfiguration>(),
    services.GetRequiredService<AuthStateProvider>(),
    NullLogger<AuthDelegatingHandler>.Instance)
{
    private readonly List<(string Path, HubConnection Connection)> _created = [];

    public IReadOnlyList<(string Path, HubConnection Connection)> Created
    {
        get { lock (_created) return [.. _created]; }
    }

    private readonly StateSignal _creations = new();

    public HubConnection Hub(string path) => Created.Last(created => created.Path == path).Connection;

    /// <summary>Completes once a connection to <paramref name="path"/> was built.</summary>
    public Task WhenCreatedAsync(string path) =>
        _creations.WhenAsync(() => Created.Any(created => created.Path == path), $"a connection to {path}");

    public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"http://127.0.0.1:1/hubs/{hubPath}", options =>
                options.HttpMessageHandlerFactory = _ => new FailingHandler())
            .Build();
        lock (_created) _created.Add((hubPath, connection));
        _creations.Pulse();
        return connection;
    }

    private sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromException<HttpResponseMessage>(new HttpRequestException("Test: hub not available"));
    }
}
