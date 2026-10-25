// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Plugins;
using Aetheus.Agent.Core.Services;
using Aetheus.Agent.Core.Workers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class AgentStartupServiceTests
{
    [Fact]
    public async Task ExecuteAsync_WhenAlreadyEnrolled_ReturnsImmediately()
    {
        var (enrollment, apiClient, _) = CreateEnrollmentService(isEnrolled: true);
        var options = Options.Create(new AetheusAgentOptions { WorkDirectory = "" });
        var logger = Substitute.For<ILogger<AgentStartupService>>();

        var pluginLoader = CreatePluginLoader();
        var service = new AgentStartupService(enrollment, pluginLoader, options, new FakeTimeProvider(), logger);
        using var cts = new CancellationTokenSource();
        await service.StartAsync(cts.Token);
        await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
        await service.StopAsync(cts.Token);

        // Already enrolled ⇒ the service must short-circuit before attempting any registration.
        await apiClient.DidNotReceive().RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>());
        await pluginLoader.Received(1).LoadPluginsAsync(options.Value.PluginDirectory, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_EnrollmentSucceeds_OnFirstTry()
    {
        var (enrollment, apiClient, state) = CreateEnrollmentService(isEnrolled: false);
        apiClient.RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new ServerRegistrationResponse { ServerId = 42, BearerToken = "bearer" });

        var tempDir = Path.Combine(Path.GetTempPath(), "prom-test-" + Guid.NewGuid().ToString("N")[..8]);
        var options = Options.Create(new AetheusAgentOptions { WorkDirectory = tempDir });
        var logger = Substitute.For<ILogger<AgentStartupService>>();

        try
        {
            var service = new AgentStartupService(enrollment, CreatePluginLoader(), options, new FakeTimeProvider(), logger);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await service.StartAsync(cts.Token);

            // Enrollment succeeds on the first attempt, so ExecuteAsync returns WITHOUT hitting the
            // backoff Task.Delay. Await its running task (bounded) rather than yield-polling
            // state.IsEnrolled - the poll races the thread-pool continuations that reach RegisterAsync
            // and can give up before the call lands (observed as a deterministic zero-call failure).
            await service.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), cts.Token);
            await service.StopAsync(TestContext.Current.CancellationToken);

            // Succeeds on the first attempt - exactly one register call, agent now enrolled.
            await apiClient.Received(1).RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>());
            Assert.True(state.IsEnrolled);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task ExecuteAsync_EnrollmentFails_RetriesToMax()
    {
        var (enrollment, apiClient, _) = CreateEnrollmentService(isEnrolled: false);
        apiClient.RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>())
            .Returns((ServerRegistrationResponse?)null);

        var options = Options.Create(new AetheusAgentOptions { WorkDirectory = "" });
        var logger = Substitute.For<ILogger<AgentStartupService>>();
        var clock = new FakeTimeProvider();

        var service = new AgentStartupService(enrollment, CreatePluginLoader(), options, clock, logger);
        // Bound the whole drive so a regression fails fast instead of hanging the suite.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await service.StartAsync(cts.Token);

        // The retry loop backs off via Task.Delay(timeProvider) and exits after exactly maxRetries
        // attempts. Drive it to completion deterministically by advancing the fake clock past each
        // backoff. ExecuteTask (the BackgroundService's running loop) is the termination signal -
        // not a fixed iteration budget, which races the thread-pool continuation that re-registers
        // the next timer.
        await DrainBackoffLoopAsync(clock, service.ExecuteTask!, cts.Token);

        await service.StopAsync(TestContext.Current.CancellationToken);

        // Gives up after exactly the configured maximum (10) attempts.
        await apiClient.Received(10).RegisterAsync(Arg.Any<ServerRegistrationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExecuteAsync_CreatesWorkDirectory()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "prom-test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var (enrollment, _, _) = CreateEnrollmentService(isEnrolled: true);
            var options = Options.Create(new AetheusAgentOptions { WorkDirectory = tempDir });
            var logger = Substitute.For<ILogger<AgentStartupService>>();

            var service = new AgentStartupService(enrollment, CreatePluginLoader(), options, new FakeTimeProvider(), logger);
            using var cts = new CancellationTokenSource();
            await service.StartAsync(cts.Token);
            // The BackgroundService creates the work directory on its first iteration; poll
            // (no fixed sleep) until it appears rather than guessing a delay.
            await WaitUntilAsync(() => Directory.Exists(tempDir), cts.Token);
            await service.StopAsync(cts.Token);

            Assert.True(Directory.Exists(tempDir));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);
        }
    }

    // Yield-poll a synchronous predicate without a fixed Task.Delay; bounded so a regression
    // fails fast instead of hanging.
    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken ct)
    {
        for (var i = 0; i < 5000 && !predicate(); i++)
            await Task.Yield();
    }

    // Drive a FakeTimeProvider-backed backoff loop to completion. Each iteration first gives the
    // thread-pool continuation parked on the previous Task.Delay a real (tiny) slice to run the
    // next attempt and register its next timer, THEN advances the fake clock past the 60s backoff
    // cap to fire it. Looping on executeTask.IsCompleted (not a fixed budget) avoids the race where
    // a tight advance-only loop runs the clock past a not-yet-registered timer and strands it.
    private static async Task DrainBackoffLoopAsync(FakeTimeProvider clock, Task executeTask, CancellationToken ct)
    {
        while (!executeTask.IsCompleted)
        {
            await Task.Delay(1, ct).ConfigureAwait(false);
            clock.Advance(TimeSpan.FromSeconds(60));
        }
        await executeTask.ConfigureAwait(false);
    }

    private static (EnrollmentService service, IServerApiClient apiClient, AgentState state) CreateEnrollmentService(bool isEnrolled)
    {
        var apiClient = Substitute.For<IServerApiClient>();
        var state = new AgentState(TimeProvider.System);
        var logger = Substitute.For<ILogger<EnrollmentService>>();
        var options = Options.Create(new AetheusAgentOptions
        {
            ServerUrl = "http://test",
            WorkDirectory = ""
        });

        var configData = new Dictionary<string, string?>
        {
            ["Aetheus:RegistrationToken"] = "test-token"
        };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configData)
            .Build();

        if (isEnrolled)
        {
            state.ServerId = 1;
            state.BearerToken = "test-bearer-token";
        }

        var protector = Substitute.For<ICredentialProtector>();
        protector.Protect(Arg.Any<byte[]>()).Returns(Array.Empty<byte>());
        protector.Unprotect(Arg.Any<byte[]>()).Returns(Array.Empty<byte>());
        var enrollment = new EnrollmentService(apiClient, options, state, configuration, protector, Substitute.For<IShellRunner>(), TimeProvider.System, logger);
        return (enrollment, apiClient, state);
    }

    private static IPluginLoader CreatePluginLoader()
    {
        var loader = Substitute.For<IPluginLoader>();
        loader.LoadPluginsAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        loader.UnloadAllAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        return loader;
    }
}
