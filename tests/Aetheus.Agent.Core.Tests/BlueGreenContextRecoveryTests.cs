// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The recovery context rebuilds only what a rollback needs (state directory and ports) when
/// the retained workspace is gone, and still refuses an environment that is incoherent.</summary>
public sealed class BlueGreenContextRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"bg-recovery-{Guid.NewGuid():N}");

    public BlueGreenContextRecoveryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private Dictionary<string, string> Ports(string stateDir) => new(StringComparer.Ordinal)
    {
        ["AETHEUS_BG_STATE_DIR"] = stateDir,
        ["AETHEUS_BG_PORT_FRONT_BLUE"] = "10029",
        ["AETHEUS_BG_PORT_BACK_BLUE"] = "10030",
        ["AETHEUS_BG_PORT_FRONT_GREEN"] = "10031",
        ["AETHEUS_BG_PORT_BACK_GREEN"] = "10032"
    };

    [Fact]
    public void CompleteEnvironment_YieldsTheFullContext()
    {
        var envFile = Path.Combine(_root, ".env");
        var composeFile = Path.Combine(_root, "compose.yml");
        File.WriteAllText(envFile, "APPNAME=aetheus\nENV=demo\nDB_USER=aetheus\nDB_NAME=aetheus_demo\n");
        File.WriteAllText(composeFile, "services: {}\n");
        var env = Ports(_root);
        env["AETHEUS_BG_ENV_FILE"] = envFile;
        env["AETHEUS_BG_COMPOSE_FILES"] = composeFile;

        Assert.True(BlueGreenContext.TryCreateForRecovery("aetheus-demo", env, out var context, out var error));

        Assert.Equal(string.Empty, error);
        Assert.Equal([composeFile], context!.ComposeFiles);
        Assert.Equal("aetheus_demo", context.DatabaseName);
    }

    [Fact]
    public void MissingWorkspace_StillYieldsAContextCarryingTheStateDirectoryAndPorts()
    {
        var env = Ports(_root);
        env["AETHEUS_BG_COMPOSE_ENV_APP_VERSION"] = "1.2.3";
        env["AETHEUS_BG_ENV_FILE"] = Path.Combine(_root, "gone.env");

        Assert.True(BlueGreenContext.TryCreateForRecovery("aetheus-demo", env, out var context, out _));

        Assert.Equal(_root, context!.StateDir);
        Assert.Empty(context.ComposeFiles);
        Assert.Equal(string.Empty, context.DatabaseContainer);
        Assert.Equal((10031, 10032), context.PortsFor("green"));
        Assert.Equal("1.2.3", context.ComposeEnvironment["APP_VERSION"]);
    }

    [Fact]
    public void RelativeStateDirectory_IsRefused()
    {
        Assert.False(BlueGreenContext.TryCreateForRecovery("aetheus-demo", Ports("relative/state"), out var context, out var error));

        Assert.Null(context);
        Assert.Contains("AETHEUS_BG_STATE_DIR must be an absolute path", error, StringComparison.Ordinal);
    }

    [Fact]
    public void RepeatedPort_IsRefused()
    {
        var env = Ports(_root);
        env["AETHEUS_BG_PORT_BACK_GREEN"] = "10029";

        Assert.False(BlueGreenContext.TryCreateForRecovery("aetheus-demo", env, out var context, out var error));

        Assert.Null(context);
        Assert.Contains("must be distinct", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PortHolders_ReportsTheContainerPublishingAPort_AndAHostProcessWhenNoneDoes()
    {
        var env = Ports(_root);
        Assert.True(BlueGreenContext.TryCreateForRecovery("aetheus-demo", env, out var context, out _));
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(
                "docker",
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("publish=10031")),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "portfolio-prod-front | Up 3 days | 0.0.0.0:10031->80/tcp\n", string.Empty));
        shell.RunExecAsync(
                "docker",
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("publish=10032")),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<int>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(0, "  \n", string.Empty));
        var messages = new List<(string Text, TaskLogLevel Level)>();

        await new BlueGreenPortHolders(shell).ReportAsync(
            context!, "green", 30, (text, level) => { messages.Add((text, level)); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(
            [
                ("Port 10031 is published by: portfolio-prod-front | Up 3 days | 0.0.0.0:10031->80/tcp", TaskLogLevel.Warning),
                ("Port 10032: no container publishes it; the holder is a host process.", TaskLogLevel.Warning)
            ],
            messages);
    }
}
