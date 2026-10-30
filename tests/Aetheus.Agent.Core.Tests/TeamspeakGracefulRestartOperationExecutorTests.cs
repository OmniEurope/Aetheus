// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class TeamspeakGracefulRestartOperationExecutorTests
{
    private static TeamspeakGracefulRestartOperationExecutor Build(ITeamspeakQueryClient? client = null) =>
        new(client ?? Substitute.For<ITeamspeakQueryClient>(),
            Options.Create(new AetheusAgentOptions()),
            NullLogger<TeamspeakGracefulRestartOperationExecutor>.Instance);

    private static Task NoOutput(string _, TaskLogLevel __) => Task.CompletedTask;
    private static readonly IReadOnlyDictionary<string, string> NoEnv = new Dictionary<string, string>();

    [Theory]
    [InlineData(OperationKind.TeamspeakGracefulRestart, true)]
    [InlineData(OperationKind.TeamspeakGetLogs, true)]
    [InlineData(OperationKind.TeamspeakServerQuery, false)]
    [InlineData(OperationKind.TeamspeakSetup, false)]
    public void CanHandle_OnlyGracefulRestartAndLogs(OperationKind kind, bool expected)
        => Assert.Equal(expected, Build().CanHandle(kind));

    [Fact]
    public async Task GetLogs_AbsentLogsDir_ReturnsEmptySuccess()
    {
        var result = await Build().ExecuteAsync(
            OperationKind.TeamspeakGetLogs, "/opt/aetheus-nonexistent-ts", NoEnv, 15,
            NoOutput, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
    }

    [Fact]
    public async Task GetLogs_ReadsNewestLogFileTail()
    {
        var install = Path.Combine(Path.GetTempPath(), "aetheus-ts-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(install, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            var older = Path.Combine(logs, "ts3server_1.log");
            var newer = Path.Combine(logs, "ts3server_2.log");
            await File.WriteAllTextAsync(older, "OLD LINE\n", TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(newer, "NEW LINE ALPHA\nNEW LINE BETA\n", TestContext.Current.CancellationToken);
            File.SetLastWriteTimeUtc(older, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(newer, new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc));

            var messages = new List<string>();
            var result = await Build().ExecuteAsync(
                OperationKind.TeamspeakGetLogs, install, NoEnv, 15,
                (m, _) => { messages.Add(m); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            // The newest file's content is surfaced; the older file is not.
            Assert.Contains(messages, m => m.Contains("NEW LINE BETA", StringComparison.Ordinal));
            Assert.DoesNotContain(messages, m => m.Contains("OLD LINE", StringComparison.Ordinal));
        }
        finally
        {
            try { Directory.Delete(install, true); } catch { /* best-effort cleanup */ }
        }
    }

    // The restart step shells to systemctl, so the op is Linux-only. On Windows CI it must reject
    // up-front (the gm/kick/restart sequence itself is validated on the Linux VPS-sim).
    [PlatformFact("windows")]
    public async Task GracefulRestart_OnNonLinux_ReturnsNotSupported()
    {
        var env = new Dictionary<string, string> { [TeamspeakSetupEnv.RuntimeQueryPort] = "10011" };
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.TeamspeakGracefulRestart, "-", env, 90,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("only supported on Linux", StringComparison.OrdinalIgnoreCase));
    }

    [PlatformFact("linux")]
    public async Task GracefulRestart_OnLinux_MissingQueryPort_ReturnsFailure()
    {
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.TeamspeakGracefulRestart, "-", NoEnv, 90,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("TEAMSPEAK_QUERY_PORT", StringComparison.Ordinal));
    }

    // A Linux executor whose credential file, grace wait and sudo runner are the test's own: the
    // whole gm / wait / kick / restart sequence runs on any platform without a network or sudo.
    private sealed class RestartHarness : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "aetheus-ts-restart-" + Guid.NewGuid().ToString("N"));

        public RestartHarness(string? credential = "s3cret pass", int restartExitCode = 0)
        {
            Directory.CreateDirectory(_directory);
            CredentialPath = Path.Combine(_directory, "query-credentials");
            if (credential is not null) File.WriteAllText(CredentialPath, credential + "\n");
            Client.ExecuteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
                .Returns(call => { Events.Add("query " + call.ArgAt<string>(1)); return "error id=0 msg=ok"; });
            Executor = new TeamspeakGracefulRestartOperationExecutor(
                Client,
                Options.Create(new AetheusAgentOptions { MinTimeoutSeconds = 10, MaxTimeoutSeconds = 120 }),
                NullLogger<TeamspeakGracefulRestartOperationExecutor>.Instance)
            {
                IsLinux = true,
                CredentialPath = CredentialPath,
                DelayAsync = (delay, _) => { Events.Add($"wait {delay.TotalSeconds}"); return Task.CompletedTask; },
                UnitExistsAsync = (unit, _) => Task.FromResult(unit == "teamspeak3.service"),
                RunProcessAsync = (startInfo, timeout, _, _, _) =>
                {
                    Events.Add($"run {startInfo.FileName} {string.Join(' ', startInfo.ArgumentList)} timeout={timeout}");
                    return Task.FromResult(new ExecutorResult(restartExitCode, false));
                },
            };
        }

        public string CredentialPath { get; }
        public ITeamspeakQueryClient Client { get; } = Substitute.For<ITeamspeakQueryClient>();
        public List<string> Events { get; } = [];
        public List<(string Message, TaskLogLevel Level)> Output { get; } = [];
        public TeamspeakGracefulRestartOperationExecutor Executor { get; }

        public Task<ExecutorResult> RestartAsync(Dictionary<string, string> env, int timeoutSeconds = 90) =>
            Executor.ExecuteAsync(
                OperationKind.TeamspeakGracefulRestart, "-", env, timeoutSeconds,
                (message, level) => { Output.Add((message, level)); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);

        public void Dispose() => Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task GracefulRestart_WarnsWaitsKicksThenRestartsTheUnit_InThatOrder()
    {
        using var harness = new RestartHarness();
        var env = new Dictionary<string, string>
        {
            [TeamspeakSetupEnv.RuntimeQueryPort] = "10011",
            [TeamspeakSetupEnv.WarnSeconds] = "30",
            [TeamspeakSetupEnv.WarnMessage] = "Back in 2 min|ok",
        };

        var result = await harness.RestartAsync(env, timeoutSeconds: 500);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Equal(
            [
                "query login serveradmin s3cret\\spass\nuse sid=1\ngm msg=Back\\sin\\s2\\smin\\pok\nquit\n",
                "wait 30",
                "query login serveradmin s3cret\\spass\nuse sid=1\nclientkick reasonid=5 reasonmsg=Restart cldbid=0\nquit\n",
                // The restart timeout is clamped to the configured maximum. R-119: the unit teamspeak-setup installs,
                // through the aetheus-teamspeak grant (ts3server named no unit on any host).
                "run sudo -n /bin/systemctl restart teamspeak3.service timeout=120",
            ],
            harness.Events);
        await harness.Client.Received(2).ExecuteAsync(10011, Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(
            ["Broadcasting restart warning (grace 30s)…", "Kicking connected clients…", "Restarting teamspeak3.service…"],
            harness.Output.Select(line => line.Message));
    }

    [Theory]
    [InlineData(null, 0)]
    [InlineData("not-a-number", 0)]
    [InlineData("-5", 0)]
    [InlineData("0", 0)]
    [InlineData("9999", 600)]
    public async Task GracefulRestart_GracePeriod_IsParsedAndClampedBetweenZeroAndTenMinutes(string? raw, int expectedSeconds)
    {
        using var harness = new RestartHarness();
        var env = new Dictionary<string, string> { [TeamspeakSetupEnv.RuntimeQueryPort] = "10011" };
        if (raw is not null) env[TeamspeakSetupEnv.WarnSeconds] = raw;

        var result = await harness.RestartAsync(env, timeoutSeconds: 1);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains($"grace {expectedSeconds}s", harness.Output[0].Message, StringComparison.Ordinal);
        // No wait at all for a zero grace period, exactly one otherwise.
        Assert.Equal(expectedSeconds == 0 ? [] : [$"wait {expectedSeconds}"], harness.Events.Where(e => e.StartsWith("wait", StringComparison.Ordinal)));
        // The default warning text is sent when none is configured; the timeout is raised to the minimum.
        Assert.Contains("gm msg=Server\\srestarting\n", harness.Events[0], StringComparison.Ordinal);
        Assert.EndsWith("timeout=10", harness.Events[^1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task GracefulRestart_AFailedRestart_IsReportedAsTheResult()
    {
        using var harness = new RestartHarness(restartExitCode: 1);

        var result = await harness.RestartAsync(new Dictionary<string, string> { [TeamspeakSetupEnv.RuntimeQueryPort] = "10011" });

        Assert.Equal(1, result.ExitCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("0")]
    [InlineData("70000")]
    [InlineData("abc")]
    public async Task GracefulRestart_WithoutAValidQueryPort_FailsBeforeAnyQuery(string? port)
    {
        using var harness = new RestartHarness();
        var env = new Dictionary<string, string>();
        if (port is not null) env[TeamspeakSetupEnv.RuntimeQueryPort] = port;

        var result = await harness.RestartAsync(env);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(harness.Events);
        Assert.Contains(harness.Output, line => line.Level == TaskLogLevel.Error && line.Message.Contains("TEAMSPEAK_QUERY_PORT", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null, "Cannot read TeamSpeak query credential at")]
    [InlineData("   ", "TeamSpeak query credential file is empty")]
    public async Task GracefulRestart_WithoutAUsableCredential_FailsBeforeAnyQuery(string? credential, string expected)
    {
        using var harness = new RestartHarness(credential);

        var result = await harness.RestartAsync(new Dictionary<string, string> { [TeamspeakSetupEnv.RuntimeQueryPort] = "10011" });

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(harness.Events);
        Assert.Contains(harness.Output, line => line.Level == TaskLogLevel.Error && line.Message.StartsWith(expected, StringComparison.Ordinal));
    }

    [Fact]
    public async Task GracefulRestart_OffLinux_RejectsBeforeReadingAnything()
    {
        using var harness = new RestartHarness();
        var executor = new TeamspeakGracefulRestartOperationExecutor(
            harness.Client,
            Options.Create(new AetheusAgentOptions()),
            NullLogger<TeamspeakGracefulRestartOperationExecutor>.Instance)
        {
            IsLinux = false,
            CredentialPath = harness.CredentialPath,
        };

        var result = await executor.ExecuteAsync(
                OperationKind.TeamspeakGracefulRestart, "-",
                new Dictionary<string, string> { [TeamspeakSetupEnv.RuntimeQueryPort] = "10011" }, 90,
                (message, level) => { harness.Output.Add((message, level)); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(("TeamSpeak operations are only supported on Linux", TaskLogLevel.Error), Assert.Single(harness.Output));
        await harness.Client.DidNotReceiveWithAnyArgs().ExecuteAsync(default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetLogs_EmptyLogsDirectory_ReportsNoLogFiles()
    {
        var install = Path.Combine(Path.GetTempPath(), "aetheus-ts-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(install, "logs"));
        try
        {
            var messages = new List<(string Message, TaskLogLevel Level)>();
            var result = await Build().ExecuteAsync(
                OperationKind.TeamspeakGetLogs, install, NoEnv, 15,
                (m, l) => { messages.Add((m, l)); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            var (message, level) = Assert.Single(messages);
            Assert.Equal(TaskLogLevel.Warning, level);
            Assert.StartsWith("No .log files under", message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(install, true);
        }
    }

    [Fact]
    public async Task GetLogs_LongLogFile_ShowsOnlyItsLastHundredLines()
    {
        var install = Path.Combine(Path.GetTempPath(), "aetheus-ts-" + Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(install, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            await File.WriteAllLinesAsync(
                Path.Combine(logs, "ts3server_1.log"),
                Enumerable.Range(1, 150).Select(i => $"line {i}"),
                TestContext.Current.CancellationToken);
            var messages = new List<string>();

            var result = await Build().ExecuteAsync(
                OperationKind.TeamspeakGetLogs, install, NoEnv, 15,
                (m, _) => { messages.Add(m); return Task.CompletedTask; },
                TestContext.Current.CancellationToken);

            Assert.Equal(0, result.ExitCode);
            Assert.Equal(Enumerable.Range(51, 100).Select(i => $"line {i}"), Assert.Single(messages).Split('\n'));
        }
        finally
        {
            Directory.Delete(install, true);
        }
    }
}
