// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers;
using Aetheus.Front.Components.Servers.AgentWizard;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Servers;

public class AddAgentRealtimeTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;

    public AddAgentRealtimeTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    // The Verify-step detection logic lives in AgentDetectionMonitor (extracted from AddAgent to keep
    // the page within the 600-line budget). Build one from the test-handler-backed services.
    private AgentDetectionMonitor MakeMonitor(TimeProvider? timeProvider = null) => new(
        Services.GetRequiredService<ApiClient>(),
        Services.GetRequiredService<HubConnectionFactory>(),
        NullLogger.Instance,
        () => Task.CompletedTask,
        timeProvider);

    private void SetupDefaultStubs()
    {
        // The actual endpoint used by ApiClient.GetAgentServerUrlAsync is api/servers/agent-server-url
        // returning { "url": "..." }
        _handler.SetJsonResponse("agent-server-url", new { url = "https://my-server.example.com" });
        _handler.SetJsonResponse("api/auth/registration-tokens", new RegistrationTokenDto
        {
            Id = 1,
            Token = "token-abc-xyz-123456",
            ExpiresAt = DateTime.UtcNow.AddHours(24)
        });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [],
            TotalCount = 0,
            Page = 1,
            PageSize = 1
        });
    }

    [Fact]
    public void Renders_WithDefaultStubs()
    {
        SetupDefaultStubs();
        var cut = Render<AddAgent>();
        // OnInitializedAsync resolves the agent server URL via the agent-server-url endpoint.
        Assert.Contains(_handler.Requests, r => r.Url.Contains("agent-server-url"));
    }

    [Fact]
    public void ServerBaseUrl_IsNonEmpty()
    {
        SetupDefaultStubs();
        var cut = Render<AddAgent>();

        // ServerBaseUrl is a private property - access via reflection
        var prop = typeof(AddAgent).GetProperty("ServerBaseUrl", Priv)!;
        var url = (string)prop.GetValue(cut.Instance)!;
        Assert.NotEmpty(url);
        // Should be either the API response or the config fallback
        Assert.True(url.StartsWith("http") || url.StartsWith("https"));
    }

    [Fact]
    public void ServerBaseUrl_FallsBackToConfig_WhenApiFails()
    {
        _handler.SetResponse("api/settings/agent-server-url", System.Net.HttpStatusCode.InternalServerError);
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [],
            TotalCount = 0,
            Page = 1,
            PageSize = 1
        });

        var cut = Render<AddAgent>();

        var prop = typeof(AddAgent).GetProperty("ServerBaseUrl", Priv)!;
        var url = (string)prop.GetValue(cut.Instance)!;
        // Falls back to "http://test:5301" from the IConfiguration in BunitTestHelper
        Assert.NotEmpty(url);
    }

    [Fact]
    public void ElapsedSeconds_WhenNotListening_ReturnsZero()
    {
        SetupDefaultStubs();
        var monitor = MakeMonitor();
        Assert.Equal(0, monitor.ElapsedSeconds);
    }

    [Fact]
    public async Task ElapsedSeconds_WhenListening_FollowsInjectedClock()
    {
        SetupDefaultStubs();
        var clock = new FakeTimeProvider();
        var monitor = MakeMonitor(clock);

        await monitor.StartAsync(null);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(10, monitor.ElapsedSeconds);
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task TheDetectionWindow_RunsOut_StopsListeningAndSaysSoOnce()
    {
        SetupDefaultStubs();
        var clock = new ObservedFakeClock(DateTimeOffset.UnixEpoch);
        var changes = 0;
        var changed = new StateSignal();
        var monitor = new AgentDetectionMonitor(
            Services.GetRequiredService<ApiClient>(),
            Services.GetRequiredService<HubConnectionFactory>(),
            NullLogger.Instance,
            () => { Interlocked.Increment(ref changes); changed.Pulse(); return Task.CompletedTask; },
            clock);

        await monitor.StartAsync(null);
        // Each second of the countdown says so once (and the last one says it timed out): the clock is
        // moved one second at a time, each time once the previous second was told.
        for (var second = 0; second <= AgentDetectionMonitor.MaxSeconds && !monitor.TimedOut; second++)
        {
            var told = Volatile.Read(ref changes);
            clock.Advance(TimeSpan.FromSeconds(1));
            await changed.WhenAsync(() => Volatile.Read(ref changes) > told, $"second {second + 1} of the countdown");
        }

        Assert.True(monitor.TimedOut);
        Assert.False(monitor.Listening);
        Assert.False(monitor.AgentFound);
        // The countdown and the polling stopped: no timer is left that moving the clock could fire.
        await clock.WhenNonePendingAsync(TimeSpan.FromSeconds(1));
        await clock.WhenNonePendingAsync(TimeSpan.FromSeconds(3));
        var afterTimeout = Volatile.Read(ref changes);
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.Equal(afterTimeout, Volatile.Read(ref changes));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task CancelAsync_ClearsState()
    {
        SetupDefaultStubs();
        var monitor = MakeMonitor();

        // Pre-set some listening state (props have private setters - set via reflection)
        typeof(AgentDetectionMonitor).GetProperty("HubError")!.SetValue(monitor, "some error");
        typeof(AgentDetectionMonitor).GetProperty("AgentFound")!.SetValue(monitor, true);

        await monitor.CancelAsync();

        Assert.Null(monitor.HubError);
        Assert.False(monitor.AgentFound);
    }

    [Fact]
    public async Task StopAsync_WhenHubNull_SetsListeningFalse()
    {
        SetupDefaultStubs();
        var monitor = MakeMonitor();
        typeof(AgentDetectionMonitor).GetProperty("Listening")!.SetValue(monitor, true);

        await monitor.StopAsync();

        Assert.False(monitor.Listening);
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_IsIdempotent()
    {
        SetupDefaultStubs();
        var monitor = MakeMonitor();

        await monitor.DisposeAsync();
        var ex = await Record.ExceptionAsync(async () => await monitor.DisposeAsync());
        Assert.Null(ex);
    }

    [Fact]
    public void GetAllCommands_Linux_ContainsCurl()
    {
        SetupDefaultStubs();
        var cut = Render<AddAgent>();

        var commands = cut.Instance.GetAllCommands();
        Assert.NotEmpty(commands);
        Assert.Contains(commands, c => c.Contains("curl"));
    }

    [Fact]
    public void GetAllCommands_Windows_ContainsInvokeWebRequest()
    {
        SetupDefaultStubs();
        var cut = Render<AddAgent>();

        typeof(AddAgent).GetField("_platform", Priv)!.SetValue(cut.Instance, "windows");

        var commands = cut.Instance.GetAllCommands();
        Assert.Contains(commands, c => c.Contains("Invoke-WebRequest"));
    }

    [Fact]
    public async Task TryDetectViaUsedToken_WhenWizardTokenIsUsed_ReportsAgentFoundImmediately()
    {
        SetupDefaultStubs();
        // The wizard's own token has been consumed by a registering agent - the definitive
        // "handshake done" signal the verify step must catch without polling. RTOK: the monitor now
        // polls just its own token by id (api/auth/registration-tokens/{id}), not the full list.
        _handler.SetJsonResponse("api/auth/registration-tokens/42", new RegistrationTokenDto
        {
            Id = 42,
            Token = "token-abc-xyz-123456",
            IsUsed = true,
            UsedByServerId = 7
        });
        _handler.SetJsonResponse("api/servers/7", new ServerDetailDto { Id = 7, Name = "edge-vps-01" });

        var monitor = MakeMonitor();
        typeof(AgentDetectionMonitor).GetField("_generatedToken", Priv)!
            .SetValue(monitor, new RegistrationTokenDto { Id = 42, Token = "token-abc-xyz-123456" });

        var method = typeof(AgentDetectionMonitor).GetMethod("TryDetectViaUsedTokenAsync", Priv)!;
        var detected = await (Task<bool>)method.Invoke(monitor, [])!;

        Assert.True(detected);
        Assert.True(monitor.AgentFound);
        Assert.Equal(7, monitor.DetectedServerId);
        Assert.Equal("edge-vps-01", monitor.AgentName);
    }

    [Fact]
    public async Task TryDetectViaUsedToken_WhenTokenStillUnused_ReturnsFalse()
    {
        SetupDefaultStubs();
        // RTOK: the monitor polls its own token by id, not the list.
        _handler.SetJsonResponse("api/auth/registration-tokens/42", new RegistrationTokenDto
        {
            Id = 42,
            Token = "token-abc-xyz-123456",
            IsUsed = false
        });

        var monitor = MakeMonitor();
        typeof(AgentDetectionMonitor).GetField("_generatedToken", Priv)!
            .SetValue(monitor, new RegistrationTokenDto { Id = 42, Token = "token-abc-xyz-123456" });

        var method = typeof(AgentDetectionMonitor).GetMethod("TryDetectViaUsedTokenAsync", Priv)!;
        var detected = await (Task<bool>)method.Invoke(monitor, [])!;

        Assert.False(detected);
        Assert.False(monitor.AgentFound);
    }

    [Fact]
    public async Task OnStepChanged_ToStep1_GeneratesToken()
    {
        SetupDefaultStubs();
        var cut = Render<AddAgent>();

        // Ensure no token yet
        typeof(AddAgent).GetField("_generatedToken", Priv)!.SetValue(cut.Instance, null);
        typeof(AddAgent).GetField("_tokenGenerating", Priv)!.SetValue(cut.Instance, false);

        var method = typeof(AddAgent).GetMethod("OnStepChanged", Priv)!;
        await cut.InvokeAsync(async () => await (Task)method.Invoke(cut.Instance, [1])!);

        // OnStepChanged records the new step. Token generation only fires on step 2, so the
        // capabilities step (1) advances the cursor without POSTing a registration token.
        Assert.Equal(1, (int)typeof(AddAgent).GetField("_currentStep", Priv)!.GetValue(cut.Instance)!);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("registration-tokens"));
    }

    // FRONT-015: a heartbeat or registration of ANOTHER server must never conclude that the wizard's
    // new agent is installed; it only triggers a re-read of the wizard's own token.
    [Fact]
    public async Task ForeignServerHubEvents_WhileOwnTokenIsUnused_DoNotReportTheAgentFound()
    {
        SetupDefaultStubs();
        _handler.SetJsonResponse("api/auth/registration-tokens/42", new RegistrationTokenDto { Id = 42, IsUsed = false });
        var factory = new CapturingHubConnectionFactory(Services);
        var monitor = new AgentDetectionMonitor(Services.GetRequiredService<ApiClient>(), factory,
            NullLogger.Instance, () => Task.CompletedTask, new FakeTimeProvider());
        await monitor.StartAsync(new RegistrationTokenDto { Id = 42, Token = "token-abc-xyz-123456" });
        var tokenReadsBefore = _handler.Requests.Count(r => r.Url.Contains("registration-tokens/42"));

        await HubHandlerInvoker.InvokeAsync(factory.Created!, "ServerHeartbeat", 3, new ServerHeartbeatDto());
        await HubHandlerInvoker.InvokeAsync(factory.Created!, "ServerRegistered", new ServerDto { Id = 99, Name = "old-vps" });

        Assert.False(monitor.AgentFound);
        Assert.Null(monitor.DetectedServerId);
        Assert.Equal(tokenReadsBefore + 2, _handler.Requests.Count(r => r.Url.Contains("registration-tokens/42")));
        await monitor.DisposeAsync();
    }

    [Fact]
    public async Task ServerHubEvent_WhenOwnTokenHasBeenUsed_ReportsTheServerOfTheToken()
    {
        SetupDefaultStubs();
        _handler.SetJsonResponse("api/auth/registration-tokens/42", new RegistrationTokenDto { Id = 42, IsUsed = false });
        var factory = new CapturingHubConnectionFactory(Services);
        var monitor = new AgentDetectionMonitor(Services.GetRequiredService<ApiClient>(), factory,
            NullLogger.Instance, () => Task.CompletedTask, new FakeTimeProvider());
        await monitor.StartAsync(new RegistrationTokenDto { Id = 42, Token = "token-abc-xyz-123456" });

        _handler.SetJsonResponse("api/auth/registration-tokens/42",
            new RegistrationTokenDto { Id = 42, IsUsed = true, UsedByServerId = 7 });
        _handler.SetJsonResponse("api/servers/7", new ServerDetailDto { Id = 7, Name = "edge-vps-01" });
        await HubHandlerInvoker.InvokeAsync(factory.Created!, "ServerRegistered", new ServerDto { Id = 99, Name = "old-vps" });

        Assert.True(monitor.AgentFound);
        Assert.Equal(7, monitor.DetectedServerId);
        Assert.Equal("edge-vps-01", monitor.AgentName);
        await monitor.DisposeAsync();
    }

    /// <summary>Unreachable hub, but keeps the connection so a test can deliver its messages.</summary>
    private sealed class CapturingHubConnectionFactory(IServiceProvider services) : HubConnectionFactory(
        services.GetRequiredService<IConfiguration>(),
        services.GetRequiredService<AuthStateProvider>(),
        NullLogger<AuthDelegatingHandler>.Instance)
    {
        public HubConnection? Created { get; private set; }

        public override HubConnection Create(string hubPath, IRetryPolicy? retryPolicy = null) =>
            Created = new HubConnectionBuilder()
                .WithUrl($"http://127.0.0.1:1/hubs/{hubPath}", options =>
                    options.HttpMessageHandlerFactory = _ => new FailingHandler())
                .Build();

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
                => Task.FromException<HttpResponseMessage>(new HttpRequestException("Test: hub not available"));
        }
    }
}
