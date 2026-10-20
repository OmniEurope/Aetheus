// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The outage reporting the hub factory attaches to every connection, on a connection to an in-memory
/// hub: a connection that closes on an error after its reconnection gave up says so in the system logs,
/// a brief outage that comes back reports nothing, and without an error reporter nothing is attached.
/// </summary>
public sealed class HubConnectionOutageReportTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public HubConnectionOutageReportTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetResponse(HttpMethod.Post, "api/client-errors", System.Net.HttpStatusCode.NoContent);
    }

    private HubConnectionFactory Factory(bool withReporter) => new(
        new ConfigurationBuilder().Build(),
        Services.GetRequiredService<AuthStateProvider>(),
        NullLogger<AuthDelegatingHandler>.Instance,
        errorReporter: withReporter ? Services.GetRequiredService<ClientErrorReporter>() : null,
        clock: new FakeTimeProvider());

    private int Reports() =>
        _handler.Requests.ToList().Count(IsReport);

    private static bool IsReport((string Method, string Url) request) =>
        request.Method == "POST" && request.Url.Contains("api/client-errors", StringComparison.Ordinal);

    /// <summary>Waits for <paramref name="connection"/> to be in <paramref name="state"/>, re-checked on each
    /// of its lifecycle events rather than against the clock.</summary>
    private static async Task StateAsync(HubConnection connection, HubConnectionState state)
    {
        var changed = new StateSignal();
        Task OnLifecycle(Exception? _) { changed.Pulse(); return Task.CompletedTask; }
        Task OnReconnected(string? _) { changed.Pulse(); return Task.CompletedTask; }
        connection.Closed += OnLifecycle;
        connection.Reconnecting += OnLifecycle;
        connection.Reconnected += OnReconnected;
        try
        {
            await changed.WhenAsync(() => connection.State == state, $"the connection {state}");
        }
        finally
        {
            connection.Closed -= OnLifecycle;
            connection.Reconnecting -= OnLifecycle;
            connection.Reconnected -= OnReconnected;
        }
    }

    [Fact]
    public async Task AConnectionThatCannotComeBack_ReportsItsClosingError()
    {
        var hubs = new InMemoryHubConnectionFactory(Services.GetRequiredService<AuthStateProvider>());
        var server = hubs.Server("servers");
        await using var connection = hubs.Create("servers");
        Factory(withReporter: true).ReportOutages(connection, "servers");
        await connection.StartAsync(Xunit.TestContext.Current.CancellationToken);

        server.RefuseConnections = true;
        await server.DropAsync();

        await StateAsync(connection, Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected);
        await _handler.WhenRequestedAsync(IsReport, 1, "the outage report");
        Assert.Equal(1, Reports());
        Assert.Contains("Realtime connection closed on an error", _handler.RequestDetails.ToList()
            .Single(request => request.Url.Contains("api/client-errors", StringComparison.Ordinal)).Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABriefOutageThatComesBack_ReportsNothing()
    {
        var hubs = new InMemoryHubConnectionFactory(Services.GetRequiredService<AuthStateProvider>());
        var server = hubs.Server("servers");
        await using var connection = hubs.Create("servers");
        Factory(withReporter: true).ReportOutages(connection, "servers");
        await connection.StartAsync(Xunit.TestContext.Current.CancellationToken);

        await server.DropAsync();
        await server.WhenAsync(() => server.Handshakes >= 2, "the reconnection handshake");
        await StateAsync(connection, HubConnectionState.Connected);
        Assert.Equal(2, server.ConnectAttempts);
        await connection.StopAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(0, Reports());
    }

    [Fact]
    public async Task WithoutAnErrorReporter_ALostConnectionReportsNothing()
    {
        var hubs = new InMemoryHubConnectionFactory(Services.GetRequiredService<AuthStateProvider>());
        var server = hubs.Server("servers");
        await using var connection = hubs.Create("servers");
        Factory(withReporter: false).ReportOutages(connection, "servers");
        await connection.StartAsync(Xunit.TestContext.Current.CancellationToken);

        server.RefuseConnections = true;
        await server.DropAsync();
        await StateAsync(connection, Microsoft.AspNetCore.SignalR.Client.HubConnectionState.Disconnected);

        Assert.Equal(0, Reports());
    }
}
