// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Configuration;
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
}
