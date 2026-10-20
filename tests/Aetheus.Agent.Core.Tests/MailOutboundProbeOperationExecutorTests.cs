// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using System.Text;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>The outbound port 25 prerequisite, without any real socket.</summary>
public sealed class MailOutboundProbeOperationExecutorTests
{
    private const string Host = MailOutboundProbe.Target;

    [Fact]
    public async Task A220Greeting_IsReached_SaysQuit_AndPrintsAnOkCheckLine()
    {
        var stream = new ScriptedStream("220 mx.google.com ESMTP ready\r\n");
        var connector = new FakeConnector(_ => Task.FromResult<Stream>(stream));

        var (result, lines) = await RunAsync(connector, Host);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal((Host, 25), connector.Calls.Single());
        Assert.Equal("QUIT\r\n", stream.Written);
        var check = Assert.Single(MailTaskOutputParser.ParseChecks(string.Join('\n', lines)));
        Assert.Equal(MailOutboundProbe.CheckComponent, check.Component);
        Assert.True(check.IsOk);
        Assert.Equal("220 mx.google.com ESMTP ready", check.Detail);
    }

    [Fact]
    public async Task ARefusedConnection_FailsTheCheckAndTheStep()
    {
        var connector = new FakeConnector(_ => throw new SocketException((int)SocketError.ConnectionRefused));

        var (result, lines) = await RunAsync(connector, Host);

        Assert.Equal(1, result.ExitCode);
        var check = Assert.Single(MailTaskOutputParser.ParseChecks(string.Join('\n', lines)));
        Assert.False(check.IsOk);
        Assert.Contains("ConnectionRefused", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoAnswerInTime_IsReportedAsAFilteredPort()
    {
        var connector = new FakeConnector(_ => throw new OperationCanceledException());

        var (result, lines) = await RunAsync(connector, Host);

        Assert.Equal(1, result.ExitCode);
        var check = Assert.Single(MailTaskOutputParser.ParseChecks(string.Join('\n', lines)));
        Assert.False(check.IsOk);
        Assert.Contains("no answer", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnotherGreeting_IsNotAReachedSmtpServer()
    {
        var stream = new ScriptedStream("554 no SMTP service here\r\n");
        var connector = new FakeConnector(_ => Task.FromResult<Stream>(stream));

        var (result, lines) = await RunAsync(connector, Host);

        Assert.Equal(1, result.ExitCode);
        var check = Assert.Single(MailTaskOutputParser.ParseChecks(string.Join('\n', lines)));
        Assert.False(check.IsOk);
        Assert.Contains("554", check.Detail, StringComparison.Ordinal);
        Assert.Equal(string.Empty, stream.Written);
    }

    [Theory]
    [InlineData("bad host!")]
    [InlineData("127.0.0.1;id")]
    public async Task AnInvalidTarget_ConnectsNowhere(string target)
    {
        var connector = new FakeConnector(_ => throw new InvalidOperationException("must not connect"));

        var (result, _) = await RunAsync(connector, target);

        Assert.Equal(-1, result.ExitCode);
        Assert.Empty(connector.Calls);
    }

    [Fact]
    public void TheProbe_HasItsOwnExecutor_AndCapability_OutsideTheMailHelperRange()
    {
        var executor = new MailOutboundProbeOperationExecutor(new FakeConnector(_ => throw new InvalidOperationException()),
            NullLogger<MailOutboundProbeOperationExecutor>.Instance);

        Assert.True(executor.CanHandle(OperationKind.MailOutboundProbe));
        Assert.False(executor.CanHandle(OperationKind.MailTestConfig));
        Assert.Equal(AgentCapabilities.MailOutboundProbe, AgentCapabilities.RequiredFor(OperationKind.MailOutboundProbe));
        Assert.Contains(AgentCapabilities.MailOutboundProbe, AgentCapabilities.SoftwareCapabilities);
        Assert.True(OperationTargetValidator.IsValid(OperationKind.MailOutboundProbe, MailOutboundProbe.Target));
    }

    private static async Task<(ExecutorResult Result, List<string> Lines)> RunAsync(FakeConnector connector, string target)
    {
        var executor = new MailOutboundProbeOperationExecutor(connector, NullLogger<MailOutboundProbeOperationExecutor>.Instance);
        var lines = new List<string>();
        var result = await executor.ExecuteAsync(OperationKind.MailOutboundProbe, target, 30,
            (line, _) => { lines.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);
        return (result, lines);
    }

    private sealed class FakeConnector(Func<string, Task<Stream>> connect) : IOutboundTcpConnector
    {
        public List<(string Host, int Port)> Calls { get; } = [];

        public Task<Stream> ConnectAsync(string host, int port, CancellationToken cancellationToken)
        {
            Calls.Add((host, port));
            return connect(host);
        }
    }

    /// <summary>Reads a scripted greeting and records what the probe writes back.</summary>
    private sealed class ScriptedStream(string greeting) : Stream
    {
        private readonly MemoryStream _input = new(Encoding.ASCII.GetBytes(greeting));
        private readonly MemoryStream _output = new();

        public string Written => Encoding.ASCII.GetString(_output.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
    }
}
