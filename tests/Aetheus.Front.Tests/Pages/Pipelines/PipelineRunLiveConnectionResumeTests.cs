// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.Services;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// 2026-10-06: a candidate run page left open for two hours never updated again. SignalR's own schedule
/// gave up after four attempts and nothing restarted the connection. Now the connections retry without
/// end, the page fetches the run again when it comes back to the foreground, and a run page whose live
/// updates are down says so and reloads a running run by itself.
/// </summary>
public class PipelineRunLiveConnectionResumeTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 5)]
    [InlineData(5, 30)]
    [InlineData(500, 30)]
    public void TheDefaultRetryPolicy_NeverGivesUp(int previousRetries, int expectedSeconds)
    {
        var delay = new EndlessRetryPolicy().NextRetryDelay(new RetryContext { PreviousRetryCount = previousRetries });

        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
    }

    [Fact]
    public async Task WhenThePageComesBack_ADownConnectionRestarts_AndTheRunReloads()
    {
        var auth = new AuthStateProvider(new FakeJsRuntime(), NullLogger<AuthStateProvider>.Instance);
        var factory = new SilentHubConnectionFactory(auth);
        var reloaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var connection = new PipelineRunLiveConnection(
            factory, NullLogger.Instance, 2586, () => [],
            () => { reloaded.TrySetResult(); return Task.CompletedTask; },
            () => Task.CompletedTask,
            callback => callback());
        await connection.StartAsync();
        var hub = factory.Connection!;

        factory.AnnouncePageResumed();

        await reloaded.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Equal(2, hub.Starts);
    }

    [Fact]
    public async Task ALiveConnectionThatCannotStart_IsShownInterrupted_OnlyAfterTheThreshold()
    {
        var auth = new AuthStateProvider(new FakeJsRuntime(), NullLogger<AuthStateProvider>.Instance);
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var factory = new FailingHubConnectionFactory(auth);
        await using var connection = new PipelineRunLiveConnection(
            factory, NullLogger.Instance, 2586, () => [], () => Task.CompletedTask, () => Task.CompletedTask,
            callback => callback(), runInProgress: () => true, timeProvider: clock);

        await connection.StartAsync();
        Assert.Null(connection.InterruptedSince);

        clock.Advance(PipelineRunLiveConnection.InterruptionThreshold);
        Assert.Equal(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero), connection.InterruptedSince);
    }

    private sealed class FailingHubConnectionFactory(AuthStateProvider auth) : HubConnectionFactory(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ApiBaseUrl"] = "http://test" }).Build(),
        auth,
        NullLogger<AuthDelegatingHandler>.Instance)
    {
        public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null) => new FailingHubConnection();
    }

    /// <summary>A connection whose start fails, as when the backend is unreachable.</summary>
    private sealed class FailingHubConnection() : HubConnection(
        new RefusingConnectionFactory(),
        new JsonHubProtocol(),
        new UriEndPoint(new Uri("http://test/hubs/pipelines")),
        new ServiceCollection().BuildServiceProvider(),
        NullLoggerFactory.Instance)
    {
        public override Task StartAsync(CancellationToken cancellationToken = default) =>
            Task.FromException(new HttpRequestException("The backend is unreachable."));

        private sealed class RefusingConnectionFactory : IConnectionFactory
        {
            public ValueTask<ConnectionContext> ConnectAsync(EndPoint endpoint, CancellationToken cancellationToken = default)
                => throw new InvalidOperationException("The fake hub connection never opens a transport.");
        }
    }
}
