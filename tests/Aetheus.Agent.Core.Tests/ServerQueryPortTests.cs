// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The TeamSpeak query port comes from the task environment; a missing or out-of-range value must be
/// refused with a logged error, never defaulted, because the query then talks to the wrong socket.</summary>
public class ServerQueryPortTests
{
    private static async Task<(int? Port, List<(string Message, TaskLogLevel Level)> Output)> ReadAsync(
        Dictionary<string, string> environment)
    {
        var output = new List<(string, TaskLogLevel)>();
        var port = await ServerQueryHelper.GetQueryPortAsync(environment, (message, level) =>
        {
            output.Add((message, level));
            return Task.CompletedTask;
        });
        return (port, output);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("10011", 10011)]
    [InlineData("65535", 65535)]
    public async Task GetQueryPortAsync_AValidPort_IsReturnedWithoutLoggingAnything(string value, int expected)
    {
        var (port, output) = await ReadAsync(new() { [TeamspeakSetupEnv.RuntimeQueryPort] = value });

        Assert.Equal(expected, port);
        Assert.Empty(output);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-5")]
    [InlineData("ten")]
    [InlineData("")]
    public async Task GetQueryPortAsync_AnInvalidPort_ReturnsNullAndLogsAnError(string value)
    {
        var (port, output) = await ReadAsync(new() { [TeamspeakSetupEnv.RuntimeQueryPort] = value });

        Assert.Null(port);
        var entry = Assert.Single(output);
        Assert.Equal(TaskLogLevel.Error, entry.Level);
        Assert.Contains("TEAMSPEAK_QUERY_PORT", entry.Message);
    }

    [Fact]
    public async Task GetQueryPortAsync_AMissingVariable_ReturnsNullAndLogsAnError()
    {
        var (port, output) = await ReadAsync([]);

        Assert.Null(port);
        Assert.Equal(TaskLogLevel.Error, Assert.Single(output).Level);
    }
}
