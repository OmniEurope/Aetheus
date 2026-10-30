// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Front.Tests.Services;

public class PendingApprovalsServiceTests
{
    [Fact]
    public async Task EnsureStartedAsync_WhenTheHubRefusesTheJoin_KeepsTheLoadedListInsteadOfThrowing()
    {
        // The hub answers JoinPipelineUpdatesGroup with HubException("Access denied.") for a user who
        // can read no pipeline. The service starts from the top bar: an escaping exception crashed the
        // layout on every page for such a user (QA run 2351).
        var auth = AuthenticatedUser();
        var api = new ApiClient(new HttpClient(new ApprovalsHandler()) { BaseAddress = new Uri("http://test/") });
        var factory = new SilentHubConnectionFactory(auth, refuseJoin: true);
        await using var service = new PendingApprovalsService(api, auth, factory, NullLogger<PendingApprovalsService>.Instance);

        await service.EnsureStartedAsync();

        Assert.Equal(["JoinPipelineUpdatesGroup"], factory.Connection!.Invoked);
        Assert.Empty(service.Items);
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenTheHubRefusesTheJoin_TheRefusalIsAnExpectedStateNotAWarning()
    {
        // A user the hub refuses (no pipeline of the organizations they hold) is in a normal state, not a
        // fault: a warning on every page of every session buried the real ones (local instance, Organization
        // Admin of organization 2 only). The refusal is kept at debug; the list stays as loaded.
        var auth = AuthenticatedUser();
        var api = new ApiClient(new HttpClient(new ApprovalsHandler()) { BaseAddress = new Uri("http://test/") });
        var factory = new SilentHubConnectionFactory(auth, refuseJoin: true);
        var logger = new CapturingLogger();
        await using var service = new PendingApprovalsService(api, auth, factory, logger);

        await service.EnsureStartedAsync();

        Assert.Equal(["JoinPipelineUpdatesGroup"], factory.Connection!.Invoked);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug);
    }

    [Fact]
    public async Task WhenTheBackendIsReplaced_TheRefusedJoinIsAlsoNotAWarning()
    {
        var auth = AuthenticatedUser();
        var api = new ApiClient(new HttpClient(new ApprovalsHandler()) { BaseAddress = new Uri("http://test/") });
        var factory = new SilentHubConnectionFactory(auth, refuseJoin: true);
        var logger = new CapturingLogger();
        await using var service = new PendingApprovalsService(api, auth, factory, logger);
        await service.EnsureStartedAsync();

        factory.AnnounceBackendReplaced();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (factory.Connection!.Starts < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Equal(2, factory.Connection!.Starts);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task EnsureStartedAsync_WhenTheJoinFailsForAnotherReason_StillWarns()
    {
        var auth = AuthenticatedUser();
        var api = new ApiClient(new HttpClient(new ApprovalsHandler()) { BaseAddress = new Uri("http://test/") });
        var factory = new SilentHubConnectionFactory(auth, joinFailure: new HubException("Boom."));
        var logger = new CapturingLogger();
        await using var service = new PendingApprovalsService(api, auth, factory, logger);

        await service.EnsureStartedAsync();

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task WhenTheBackendIsReplaced_TheHubRestartsOnceAndTheListIsFetchedOnce()
    {
        // aetheus-deploy-prod, run 2458: the Confirm approval is raised after the blue-green Switch, by
        // the NEW colour's backend. The browser's socket is still held by the PREVIOUS colour, which
        // stays up in reserve until Commit, and SignalR has no backplane, so ApprovalRequired never
        // reaches it and no reconnect ever happens. The new-version signal must move the socket.
        var auth = AuthenticatedUser();
        var handler = new ApprovalsHandler();
        var api = new ApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://test/") });
        var factory = new SilentHubConnectionFactory(auth);
        await using var service = new PendingApprovalsService(api, auth, factory, NullLogger<PendingApprovalsService>.Instance);
        await service.EnsureStartedAsync();
        var hub = factory.Connection!;
        Assert.Equal(1, hub.Starts);
        Assert.Equal(1, handler.Requests);
        Assert.Empty(service.Items);

        handler.Pending = [new PendingApprovalDto
        {
            ApprovalId = 91, PipelineRunId = 2458, PipelineName = "aetheus-deploy-prod",
            StageName = "Confirm", EnvironmentName = "prod", RequestedAt = new DateTime(2026, 9, 27, 8, 0, 0, DateTimeKind.Utc)
        }];
        factory.AnnounceBackendReplaced();

        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (service.Items.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20, TestContext.Current.CancellationToken);
        // Let any stray second restart or fetch surface before counting.
        await Task.Delay(200, TestContext.Current.CancellationToken);

        Assert.Same(hub, factory.Connection);
        Assert.Equal(1, factory.Created);
        Assert.Equal(2, hub.Starts);
        Assert.Equal(["JoinPipelineUpdatesGroup", "JoinPipelineUpdatesGroup"], hub.Invoked);
        Assert.Equal(2, handler.Requests);
        Assert.Equal(2458, Assert.Single(service.Items).PipelineRunId);
    }

    private static AuthStateProvider AuthenticatedUser()
    {
        var auth = new AuthStateProvider(new FakeJsRuntime(), NullLogger<AuthStateProvider>.Instance);
        typeof(AuthStateProvider).GetProperty(nameof(AuthStateProvider.Token))!.SetValue(auth, "jwt");
        typeof(AuthStateProvider).GetProperty(nameof(AuthStateProvider.Roles))!.SetValue(auth, new List<string>());
        return auth;
    }

    private sealed class CapturingLogger : ILogger<PendingApprovalsService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception)));
        }
    }

    private sealed class ApprovalsHandler : HttpMessageHandler
    {
        private int _requests;
        public volatile List<PendingApprovalDto> Pending = [];
        public int Requests => Volatile.Read(ref _requests);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = BunitTestHelper.Json(Pending) });
        }
    }
}
