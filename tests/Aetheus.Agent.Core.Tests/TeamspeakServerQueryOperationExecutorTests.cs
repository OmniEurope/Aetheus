// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

public class TeamspeakServerQueryOperationExecutorTests
{
    [Fact]
    public void CanHandle_OnlyTeamspeakServerQuery()
    {
        var sut = new TeamspeakServerQueryOperationExecutor(new StubQueryClient(null));
        Assert.True(sut.CanHandle(OperationKind.TeamspeakServerQuery));
        Assert.False(sut.CanHandle(OperationKind.ServiceRestart));
        Assert.False(sut.CanHandle(OperationKind.PipelinePublishComplexity));
    }

    // The agent must NOT report a failed ServerQuery action as green: TS3 signals the outcome with a
    // trailing `error id=N` line - only id=0 is success. A failed login (id=520) or a rejected command
    // is a real failure (anti-fake rule).
    [Theory]
    [InlineData("error id=0 msg=ok", true)]
    [InlineData("clid=42 client_nickname=Bob\nerror id=0 msg=ok", true)]
    [InlineData("error id=520 msg=invalid\\sloginname\\sor\\spassword", false)]
    [InlineData("error id=512 msg=invalid\\sclientID", false)]
    [InlineData("some banner with no error line", false)]
    [InlineData("", false)]
    public void ServerQueryReportsSuccess_ParsesTrailingErrorLine(string reply, bool expected)
    {
        Assert.Equal(expected, TeamspeakServerQueryOperationExecutor.ServerQueryReportsSuccess(reply));
    }

    [Fact]
    public void ServerQueryReportsSuccess_LastErrorLineWins()
    {
        // A multi-command batch ends with the final command's error line - that is the verdict.
        const string reply = "error id=0 msg=ok\nsome output\nerror id=768 msg=database\\sempty\\sresult\\sset";
        Assert.False(TeamspeakServerQueryOperationExecutor.ServerQueryReportsSuccess(reply));
    }

    [Theory]
    // login rejected, then use/command refused, then quit answers ok
    [InlineData("error id=520 msg=invalid\\sloginname\\sor\\spassword\nerror id=1281 msg=database\\sempty\\sresult\\sset\nerror id=0 msg=ok")]
    // login and use ok, the command itself rejected, quit ok
    [InlineData("error id=0 msg=ok\nerror id=0 msg=ok\nerror id=2568 msg=insufficient\\sclient\\spermissions\nerror id=0 msg=ok")]
    public void ServerQueryReportsSuccess_AFailureBeforeASuccessfulQuitIsStillAFailure(string reply)
    {
        Assert.False(TeamspeakServerQueryOperationExecutor.ServerQueryReportsSuccess(reply));
    }

    [Fact]
    public void ServerQueryReportsSuccess_EveryAnswerOkIsASuccess()
    {
        const string reply = "error id=0 msg=ok\nerror id=0 msg=ok\nclid=42 client_nickname=Bob\nerror id=0 msg=ok\nerror id=0 msg=ok";
        Assert.True(TeamspeakServerQueryOperationExecutor.ServerQueryReportsSuccess(reply));
    }

    [Fact]
    public void ResolveQueryCommand_PrefersTheProtectedCommand()
    {
        Assert.Equal("serveredit virtualserver_password=s3cr3t",
            TeamspeakServerQueryOperationExecutor.ResolveQueryCommand("clientlist",
                new Dictionary<string, string> { ["TEAMSPEAK_QUERY_CMD"] = "serveredit virtualserver_password=s3cr3t" }));
        Assert.Equal("clientlist",
            TeamspeakServerQueryOperationExecutor.ResolveQueryCommand("clientlist",
                new Dictionary<string, string> { ["TEAMSPEAK_QUERY_CMD"] = " " }));
    }

    [Fact]
    public async Task RunQueryAsync_SendsTheEscapedLoginAndTheCommand()
    {
        var client = new RecordingQueryClient("error id=0 msg=ok\nerror id=0 msg=ok\nerror id=0 msg=ok\nerror id=0 msg=ok");
        var log = new List<string>();

        var result = await new TeamspeakServerQueryOperationExecutor(client).RunQueryAsync(
            10011, "pa ss", "clientlist", (line, _) => { log.Add(line); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(10011, client.Port);
        Assert.Equal("login serveradmin pa\\sss\nuse sid=1\nclientlist\nquit\n", client.Script);
        Assert.Single(log);
    }

    [Theory]
    [InlineData(null, -1)]
    [InlineData("error id=520 msg=invalid\\sloginname\nerror id=0 msg=ok", 1)]
    public async Task RunQueryAsync_NoReplyOrARejectedLine_Fails(string? reply, int exitCode)
    {
        var result = await new TeamspeakServerQueryOperationExecutor(new StubQueryClient(reply)).RunQueryAsync(
            10011, "pass", "clientlist", (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(exitCode, result.ExitCode);
    }

    [PlatformFact("windows")]
    public async Task ExecuteAsync_OutsideLinux_IsRefusedBeforeAnyQuery()
    {
        var client = new RecordingQueryClient("error id=0 msg=ok");
        var log = new List<string>();

        var result = await new TeamspeakServerQueryOperationExecutor(client).ExecuteAsync(
            OperationKind.TeamspeakServerQuery, "clientlist", new Dictionary<string, string>(), 30,
            (line, _) => { log.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal(["TeamSpeak ServerQuery operations are only supported on Linux"], log);
        Assert.Null(client.Script);
    }

    private sealed class RecordingQueryClient(string reply) : Aetheus.Agent.Core.Collectors.ITeamspeakQueryClient
    {
        public int Port { get; private set; }
        public string? Script { get; private set; }

        public Task<string?> ExecuteAsync(int port, string commands, CancellationToken ct = default)
        {
            Port = port;
            Script = commands;
            return Task.FromResult<string?>(reply);
        }
    }

    private sealed class StubQueryClient(string? reply) : Aetheus.Agent.Core.Collectors.ITeamspeakQueryClient
    {
        public Task<string?> ExecuteAsync(int port, string commands, CancellationToken ct = default) => Task.FromResult(reply);
    }
}
