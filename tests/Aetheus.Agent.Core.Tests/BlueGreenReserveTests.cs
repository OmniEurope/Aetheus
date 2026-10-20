// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Exercise reserve, rollback, retirement and confirmation deadlines using the real
/// executor and a real state directory. Docker, reload and readiness dependencies are test
/// doubles.</summary>
public sealed class BlueGreenReserveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bg-reserve-{Guid.NewGuid():N}");
    private readonly string _work;
    private readonly string _conf;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BlueGreenReserveTests()
    {
        Directory.CreateDirectory(_root);
        _work = Path.Combine(_root, "agent-work");
        Directory.CreateDirectory(_work);
        _conf = Path.Combine(_root, "upstream.conf");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task Commit_KeepsTheReplacedColourRunningInReserve_WithWhatServedIt()
    {
        var shell = new RecordingShell();
        var env = await SwitchedBlueToGreenAsync("c-new", previousRevision: "c-old");

        var log = new List<string>();
        var result = await Executor(shell).ExecuteAsync(OperationKind.BlueGreenCommit, "aetheus-prod", env, 60, Collect(log), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(shell.DockerCalls, call => call.Contains("stop", StringComparison.Ordinal));
        var reserve = new BlueGreenReserve(Context(env));
        Assert.Equal("blue", reserve.Colour);
        Assert.Equal("c-old", reserve.Revision);
        Assert.Equal("serve blue\n", reserve.Upstream);
        Assert.Contains("##aetheus[setvariable name=BLUEGREEN_RESERVE_COLOR]blue", log);
        Assert.Equal("c-new", File.ReadAllText(Context(env).SourceCommitFile).Trim());
    }

    [Fact]
    public async Task Revert_PutsTrafficBackOnTheReserve_AndKeepsTheColourItLeavesInReserve()
    {
        var env = await CommittedBlueToGreenAsync();
        await File.WriteAllTextAsync(_conf, "serve green\n", Ct);

        var log = new List<string>();
        var result = await Executor(new RecordingShell(), HttpStatusCode.OK)
            .ExecuteAsync(OperationKind.BlueGreenRevert, "aetheus-prod", env, 60, Collect(log), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("serve blue\n", await File.ReadAllTextAsync(_conf, Ct));
        var context = Context(env);
        Assert.Equal("blue", new BlueGreenJournal(context).ReadLiveColour());
        Assert.Equal("c-old", File.ReadAllText(context.SourceCommitFile).Trim());
        var reserve = new BlueGreenReserve(context);
        Assert.Equal("green", reserve.Colour);
        Assert.Equal("c-new", reserve.Revision);
        Assert.Equal("serve green\n", reserve.Upstream);
        Assert.Contains("##aetheus[setvariable name=BLUEGREEN_REVERTED_REVISION]c-old", log);

        // The same operation returns to N.
        var again = await Executor(new RecordingShell(), HttpStatusCode.OK)
            .ExecuteAsync(OperationKind.BlueGreenRevert, "aetheus-prod", env, 60, Collect([]), Ct);
        Assert.Equal(0, again.ExitCode);
        Assert.Equal("serve green\n", await File.ReadAllTextAsync(_conf, Ct));
        Assert.Equal("c-new", File.ReadAllText(context.SourceCommitFile).Trim());
    }

    [Fact]
    public async Task Revert_ToAReserveThatDoesNotAnswer_MovesNothing()
    {
        var env = await CommittedBlueToGreenAsync();
        await File.WriteAllTextAsync(_conf, "serve green\n", Ct);

        var log = new List<string>();
        var result = await Executor(new RecordingShell(), HttpStatusCode.ServiceUnavailable)
            .ExecuteAsync(OperationKind.BlueGreenRevert, "aetheus-prod", env, 60, Collect(log), Ct);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal("serve green\n", await File.ReadAllTextAsync(_conf, Ct));
        Assert.Equal("green", new BlueGreenJournal(Context(env)).ReadLiveColour());
        Assert.Contains(log, line => line.Contains("does not answer its readiness probe", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Revert_WithoutAReserve_OrDuringAnOpenTransaction_IsRefused()
    {
        var env = Environment();
        Assert.True(new BlueGreenJournal(Context(env)).TryOpen("none", "blue", "c-1", out _));
        new BlueGreenJournal(Context(env)).Commit();
        var none = await Executor(new RecordingShell(), HttpStatusCode.OK)
            .ExecuteAsync(OperationKind.BlueGreenRevert, "aetheus-prod", env, 60, Collect([]), Ct);
        Assert.NotEqual(0, none.ExitCode);

        var committed = await CommittedBlueToGreenAsync();
        Assert.True(new BlueGreenJournal(Context(committed)).TryOpen("green", "blue", "c-next", out _));
        var log = new List<string>();
        var open = await Executor(new RecordingShell(), HttpStatusCode.OK)
            .ExecuteAsync(OperationKind.BlueGreenRevert, "aetheus-prod", committed, 60, Collect(log), Ct);
        Assert.NotEqual(0, open.ExitCode);
        Assert.Contains(log, line => line.Contains("owns the environment", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Up_OfTheNextDeployment_RecyclesTheReserve()
    {
        var env = await CommittedBlueToGreenAsync();

        var log = new List<string>();
        await Executor(new RecordingShell(), HttpStatusCode.OK)
            .ExecuteAsync(OperationKind.BlueGreenUp, "aetheus-prod", env, 60, Collect(log), Ct);

        Assert.False(new BlueGreenReserve(Context(env)).Exists);
        Assert.Contains(log, line => line.Contains("kept in reserve (N-1) is recycled", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Retire_WithNoOpenTransaction_StopsTheReserve_NeverTheLiveColour()
    {
        var shell = new RecordingShell();
        var env = await CommittedBlueToGreenAsync();

        var result = await Executor(shell).ExecuteAsync(OperationKind.BlueGreenRetire, "aetheus-prod", env, 60, Collect([]), Ct);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(shell.DockerCalls, call => call.Contains("stop back-blue front-blue", StringComparison.Ordinal));
        Assert.DoesNotContain(shell.DockerCalls, call => call.Contains("green", StringComparison.Ordinal));
        Assert.False(new BlueGreenReserve(Context(env)).Exists);
    }

    [Fact]
    public async Task Switch_ArmsTheConfirmationWindow_AndCommitDisarmsIt()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var env = Environment();
        var context = Context(env);
        new BlueGreenJournal(context).WriteLiveColour("blue");
        await File.WriteAllTextAsync(_conf, "serve blue\n", Ct);
        var template = Path.Combine(_root, "upstream.template");
        await File.WriteAllTextAsync(template, "serve #{COLOR}# #{FRONT_PORT}# #{BACK_PORT}#\n", Ct);
        env["AETHEUS_BG_UPSTREAM_TEMPLATE"] = template;
        env["AETHEUS_BG_REVISION"] = "c-new";
        env[BlueGreenConfirmation.MinutesVariable] = "10";

        var executor = Executor(new RecordingShell(), time: time);
        Assert.Equal(0, (await executor.ExecuteAsync(OperationKind.BlueGreenSwitch, "aetheus-prod", env, 60, Collect([]), Ct)).ExitCode);

        var (_, watch) = Assert.Single(BlueGreenConfirmation.ReadAll(_work));
        Assert.Equal(time.GetUtcNow().UtcDateTime.AddMinutes(10).Add(BlueGreenConfirmation.Grace), watch!.DeadlineUtc);
        Assert.Equal("c-new", watch.Revision);

        Assert.Equal(0, (await executor.ExecuteAsync(OperationKind.BlueGreenCommit, "aetheus-prod", env, 60, Collect([]), Ct)).ExitCode);
        Assert.Empty(BlueGreenConfirmation.ReadAll(_work));
    }

    [Fact]
    public async Task Watchdog_UndoesAnUnconfirmedSwitch_OnlyOnceTheWindowHasRunOut()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var shell = new RecordingShell();
        var env = await SwitchedBlueToGreenAsync("c-new", previousRevision: "c-old");
        BlueGreenConfirmation.Arm(_work, "aetheus-prod", 10, "c-new", env, time.GetUtcNow().UtcDateTime);
        var watchdog = new BlueGreenConfirmationWatchdog(
            shell, Options.Create(new AetheusAgentOptions { WorkDirectory = _work }), time,
            NullLogger<BlueGreenConfirmationWatchdog>.Instance);

        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(0, await watchdog.SweepAsync(Ct));
        Assert.Equal("serve green\n", await File.ReadAllTextAsync(_conf, Ct));

        time.Advance(BlueGreenConfirmation.Grace + TimeSpan.FromSeconds(1));
        Assert.Equal(1, await watchdog.SweepAsync(Ct));

        var context = Context(env);
        Assert.Equal("serve blue\n", await File.ReadAllTextAsync(_conf, Ct));
        Assert.Equal("blue", new BlueGreenJournal(context).ReadLiveColour());
        Assert.False(new BlueGreenJournal(context).Exists);
        Assert.Contains(shell.DockerCalls, call => call.Contains("stop back-green front-green", StringComparison.Ordinal));
        Assert.Contains("revision=c-new", await File.ReadAllTextAsync(Path.Combine(_root, "auto-reverted"), Ct));
        Assert.Empty(BlueGreenConfirmation.ReadAll(_work));
    }

    [Fact]
    public async Task Watchdog_LeavesACommittedOrReplacedDeploymentAlone()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var env = await SwitchedBlueToGreenAsync("c-new", previousRevision: "c-old");
        // Armed for another revision: this transaction is not the one the window was about.
        BlueGreenConfirmation.Arm(_work, "aetheus-prod", 1, "c-other", env, time.GetUtcNow().UtcDateTime);
        var watchdog = new BlueGreenConfirmationWatchdog(
            new RecordingShell(), Options.Create(new AetheusAgentOptions { WorkDirectory = _work }), time,
            NullLogger<BlueGreenConfirmationWatchdog>.Instance);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await watchdog.SweepAsync(Ct));

        Assert.Equal("serve green\n", await File.ReadAllTextAsync(_conf, Ct));
        Assert.Equal(BlueGreenJournal.Switched, new BlueGreenJournal(Context(env)).State);
        Assert.Empty(BlueGreenConfirmation.ReadAll(_work));
    }

    [Fact]
    public async Task Watchdog_AnUnusableEnvironment_DisarmsTheWatchWithoutTouchingTraffic()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var env = await SwitchedBlueToGreenAsync("c-new", previousRevision: "c-old");
        var unusable = new Dictionary<string, string>(env, StringComparer.OrdinalIgnoreCase) { ["AETHEUS_BG_STATE_DIR"] = "relative/state" };
        BlueGreenConfirmation.Arm(_work, "aetheus-prod", 1, "c-new", unusable, time.GetUtcNow().UtcDateTime);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await Watchdog(time).SweepAsync(Ct));

        Assert.Equal("serve green\n", await File.ReadAllTextAsync(_conf, Ct));
        Assert.Equal(BlueGreenJournal.Switched, new BlueGreenJournal(Context(env)).State);
        Assert.Empty(BlueGreenConfirmation.ReadAll(_work));
    }

    [Fact]
    public async Task Watchdog_WhileAnotherOperationHoldsTheEnvironment_RetriesOnTheNextPass()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var env = await SwitchedBlueToGreenAsync("c-new", previousRevision: "c-old");
        BlueGreenConfirmation.Arm(_work, "aetheus-prod", 1, "c-new", env, time.GetUtcNow().UtcDateTime);
        var watchdog = Watchdog(time);
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(BlueGreenEnvironmentLease.TryAcquire(Context(env), out var lease, out var error), error);
        using (lease)
        {
            Assert.Equal(0, await watchdog.SweepAsync(Ct));
            Assert.Equal("serve green\n", await File.ReadAllTextAsync(_conf, Ct));
            Assert.Single(BlueGreenConfirmation.ReadAll(_work));
        }

        Assert.Equal(1, await watchdog.SweepAsync(Ct));
        Assert.Equal("serve blue\n", await File.ReadAllTextAsync(_conf, Ct));
    }

    [Fact]
    public async Task Watchdog_WithoutAReloadHelper_KeepsTheJournalAndDisarms()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 12, 10, 0, 0, TimeSpan.Zero));
        var env = await SwitchedBlueToGreenAsync("c-new", previousRevision: "c-old");
        env.Remove("AETHEUS_BG_RELOAD_HELPER");
        BlueGreenConfirmation.Arm(_work, "aetheus-prod", 1, "c-new", env, time.GetUtcNow().UtcDateTime);

        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(0, await Watchdog(time).SweepAsync(Ct));

        Assert.Equal("serve green\n", await File.ReadAllTextAsync(_conf, Ct));
        Assert.Equal(BlueGreenJournal.Switched, new BlueGreenJournal(Context(env)).State);
        Assert.False(File.Exists(Path.Combine(_root, "auto-reverted")));
        Assert.Empty(BlueGreenConfirmation.ReadAll(_work));
    }

    private BlueGreenConfirmationWatchdog Watchdog(TimeProvider time) =>
        new(new RecordingShell(), Options.Create(new AetheusAgentOptions { WorkDirectory = _work }), time,
            NullLogger<BlueGreenConfirmationWatchdog>.Instance);

    // ── Fixtures ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Blue served c-old; the switch moved traffic to green for c-new and snapshotted blue's configuration.</summary>
    private async Task<Dictionary<string, string>> SwitchedBlueToGreenAsync(string revision, string previousRevision)
    {
        var env = Environment();
        var context = Context(env);
        var journal = new BlueGreenJournal(context);
        journal.WriteLiveColour("blue");
        journal.WriteDeployedRevision(previousRevision);
        Assert.True(journal.TryOpen("blue", "green", revision, out _));
        await File.WriteAllTextAsync(Path.Combine(context.JournalDir, "upstream.before"), "serve blue\n", Ct);
        await File.WriteAllTextAsync(_conf, "serve green\n", Ct);
        journal.MarkSwitched();
        journal.WriteLiveColour("green");
        env["AETHEUS_BG_REVISION"] = revision;
        return env;
    }

    private async Task<Dictionary<string, string>> CommittedBlueToGreenAsync()
    {
        var env = await SwitchedBlueToGreenAsync("c-new", previousRevision: "c-old");
        var result = await Executor(new RecordingShell()).ExecuteAsync(OperationKind.BlueGreenCommit, "aetheus-prod", env, 60, Collect([]), Ct);
        Assert.Equal(0, result.ExitCode);
        return env;
    }

    private Dictionary<string, string> Environment()
    {
        var envFile = Path.Combine(_root, ".env");
        var composeFile = Path.Combine(_root, "compose.yml");
        if (!File.Exists(envFile)) File.WriteAllText(envFile, "APPNAME=aetheus\nENV=prod\nDB_USER=aetheus\nDB_NAME=aetheus\n");
        if (!File.Exists(composeFile)) File.WriteAllText(composeFile, "services: {}\n");
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["AETHEUS_BG_STATE_DIR"] = _root,
            ["AETHEUS_BG_ENV_FILE"] = envFile,
            ["AETHEUS_BG_COMPOSE_FILES"] = composeFile,
            ["AETHEUS_BG_PORT_FRONT_BLUE"] = "10025",
            ["AETHEUS_BG_PORT_BACK_BLUE"] = "10026",
            ["AETHEUS_BG_PORT_FRONT_GREEN"] = "10027",
            ["AETHEUS_BG_PORT_BACK_GREEN"] = "10028",
            ["AETHEUS_BG_UPSTREAM_CONF"] = _conf,
            ["AETHEUS_BG_RELOAD_HELPER"] = "/usr/local/lib/aetheus/aetheus-apache-reload"
        };
    }

    private static BlueGreenContext Context(IReadOnlyDictionary<string, string> env)
    {
        Assert.True(BlueGreenContext.TryCreate("aetheus-prod", env, out var context, out var error), error);
        return context!;
    }

    private BlueGreenOperationExecutor Executor(
        IShellRunner shell, HttpStatusCode readiness = HttpStatusCode.OK, TimeProvider? time = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new FixedStatusHandler(readiness)));
        return new BlueGreenOperationExecutor(
            shell, Options.Create(new AetheusAgentOptions { WorkDirectory = _work }), factory,
            NullLogger<BlueGreenOperationExecutor>.Instance, time);
    }

    private static Func<string, TaskLogLevel, Task> Collect(List<string> log) =>
        (line, _) => { log.Add(line); return Task.CompletedTask; };

    private sealed class FixedStatusHandler(HttpStatusCode status) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status));
    }

    /// <summary>Answers Docker and the reload helper with success and records every Docker call.</summary>
    private sealed class RecordingShell : IShellRunner
    {
        internal List<string> DockerCalls { get; } = [];

        [Obsolete("Matches the interface; the executor never uses the shell form.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null) => Task.FromResult(string.Empty);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin, CancellationToken ct, TimeSpan? timeout = null) =>
            Task.FromResult(string.Empty);

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            if (fileName == "docker") DockerCalls.Add(string.Join(' ', args));
            if (args.Count > 0 && args[0] == "info") return Task.FromResult(new ShellExecResult(0, "27.0.0", string.Empty));
            return Task.FromResult(new ShellExecResult(0, string.Empty, string.Empty));
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, IReadOnlyDictionary<string, string> environmentVariables,
            string workingDirectory, bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null) =>
            RunExecAsync(fileName, args, ct, timeout);
    }
}
