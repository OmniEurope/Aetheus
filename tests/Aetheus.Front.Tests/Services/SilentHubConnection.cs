// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Services;

/// <summary>Hands out one <see cref="SilentHubConnection"/> per <see cref="Create"/> and counts them.</summary>
internal sealed class SilentHubConnectionFactory(AuthStateProvider auth, bool refuseJoin = false, Exception? joinFailure = null)
    : HubConnectionFactory(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://test" }).Build(),
        auth,
        NullLogger<AuthDelegatingHandler>.Instance)
{
    public SilentHubConnection? Connection { get; private set; }
    public int Created { get; private set; }

    public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null)
    {
        Created++;
        return Connection = new SilentHubConnection(refuseJoin, joinFailure);
    }
}

/// <summary>
/// Starts and answers invocations without a transport, and never delivers a single event: the
/// connection that is still attached to the previous colour after a blue-green switch.
/// </summary>
internal sealed class SilentHubConnection(bool refuseJoin, Exception? joinFailure = null) : HubConnection(
    new UnusedConnectionFactory(),
    new JsonHubProtocol(),
    new UriEndPoint(new Uri("http://test/hubs/pipelines")),
    new ServiceCollection().BuildServiceProvider(),
    NullLoggerFactory.Instance)
{
    private int _starts;
    private readonly List<string> _invoked = [];

    public int Starts => Volatile.Read(ref _starts);

    public IReadOnlyList<string> Invoked
    {
        get { lock (_invoked) return [.. _invoked]; }
    }

    public override Task StartAsync(CancellationToken cancellationToken = default)
    {
        Interlocked.Increment(ref _starts);
        return Task.CompletedTask;
    }

    public override Task<object?> InvokeCoreAsync(
        string methodName, Type returnType, object?[] args, CancellationToken cancellationToken = default)
    {
        lock (_invoked) _invoked.Add(methodName);
        return joinFailure is not null ? Task.FromException<object?>(joinFailure) : refuseJoin
            ? Task.FromException<object?>(new HubException("Access denied."))
            : Task.FromResult<object?>(null);
    }

    private sealed class UnusedConnectionFactory : IConnectionFactory
    {
        public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("The fake hub connection never opens a transport.");
    }
}
