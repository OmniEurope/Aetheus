// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aetheus.Agent.Core.Tests;

public class TeamspeakSetupOperationExecutorTests
{
    private static TeamspeakSetupOperationExecutor Build() =>
        new(Options.Create(new AetheusAgentOptions()), NullLogger<TeamspeakSetupOperationExecutor>.Instance);

    [Theory]
    [InlineData(OperationKind.TeamspeakSetup, true)]
    [InlineData(OperationKind.TeamspeakServerQuery, false)]
    [InlineData(OperationKind.ServiceInstall, false)]
    [InlineData(OperationKind.MailSetup, false)]
    public void CanHandle_OnlyTeamspeakSetup(OperationKind kind, bool expected)
        => Assert.Equal(expected, Build().CanHandle(kind));

    [Fact]
    public void BuildSetupArgv_ProducesExactArgv()
        => Assert.Equal(
            new[] { "-n", "/usr/local/lib/aetheus/teamspeak-setup", "/opt/teamspeak3-server_linux_amd64", "9987", "10011" },
            TeamspeakSetupOperationExecutor.BuildSetupArgv("/opt/teamspeak3-server_linux_amd64", 9987, 10011));

    private static Dictionary<string, string> ValidEnv() => new(StringComparer.Ordinal)
    {
        [TeamspeakSetupEnv.VoicePort] = "9987",
        [TeamspeakSetupEnv.QueryPort] = "10011"
    };

    [Fact]
    public async Task ExecuteAsync_InvalidInstallPath_ReturnsFailureWithoutSpawning()
    {
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.TeamspeakSetup, "not a path", ValidEnv(), 60,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains(messages, m => m.Contains("Invalid teamspeak setup parameters", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("/opt/../etc/teamspeak")]   // parent traversal
    [InlineData("/opt/teamspeak/..")]        // trailing traversal
    [InlineData("/etc")]                     // DEPLOY-001: a system directory root would chown
    [InlineData("/usr")]
    [InlineData("/home/teamspeak")]          // outside the dedicated /opt/teamspeak* prefix
    public async Task ExecuteAsync_PathTraversal_ReturnsFailureWithoutSpawning(string path)
    {
        // S-TECH-TSPR: the install-path regex must reject any ".." segment so the helper cannot be
        // pointed outside its intended tree.
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.TeamspeakSetup, path, ValidEnv(), 60,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("Invalid teamspeak setup parameters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_OutOfRangePort_ReturnsFailureWithoutSpawning()
    {
        var env = ValidEnv();
        env[TeamspeakSetupEnv.QueryPort] = "70000"; // > 65535
        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.TeamspeakSetup, "/opt/teamspeak", env, 60,
            (m, _) => { messages.Add(m); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(messages, m => m.Contains("Invalid teamspeak setup parameters", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void CanHandle_WrongKind_DoesNotHandle()
        => Assert.False(Build().CanHandle(OperationKind.None));
}
