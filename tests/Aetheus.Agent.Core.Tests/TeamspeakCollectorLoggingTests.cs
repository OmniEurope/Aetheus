// SPDX-License-Identifier: EUPL-1.2
using System.Net.Sockets;
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R3-40: TeamSpeak ran on the server, the page showed 0 channels and the agent
/// journal held no TeamSpeak line in six hours. Each outcome of the collection, failures first, is now
/// logged once when it occurs or changes, at Debug while it repeats.</summary>
public sealed class TeamspeakCollectorLoggingTests
{
    private const string CredentialPath = "/opt/aetheus-agent/teamspeak/query-credentials";
    private const string Password = "s3cret-serveradmin";

    [Fact]
    public async Task RunningWithoutCredential_WarnsOnceNamingTheFileAndWhatIsLost()
    {
        var logger = new RecordingLogger<TeamspeakCollector>();
        var collector = Build(logger, running: true, credential: null, Substitute.For<ITeamspeakQueryClient>());

        await collector.CollectAsync(TestContext.Current.CancellationToken);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains(CredentialPath, warning.Message, StringComparison.Ordinal);
        Assert.Contains("is missing", warning.Message, StringComparison.Ordinal);
        Assert.Contains("not collected", warning.Message, StringComparison.Ordinal);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Debug && entry.Message.Contains("is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EmptyCredential_IsReportedAsEmpty()
    {
        var logger = new RecordingLogger<TeamspeakCollector>();
        await Build(logger, running: true, credential: "  \n", Substitute.For<ITeamspeakQueryClient>())
            .CollectAsync(TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning && entry.Message.Contains("is empty", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ServerQueryWithoutAnswer_Warns()
    {
        var logger = new RecordingLogger<TeamspeakCollector>();
        var query = Substitute.For<ITeamspeakQueryClient>();
        query.ExecuteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((string?)null);

        await Build(logger, running: true, Password, query).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("127.0.0.1:10011 gave no answer", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RefusedLogin_WarnsWithTheServerQueryErrorAndNeverThePassword()
    {
        var logger = new RecordingLogger<TeamspeakCollector>();
        var query = Substitute.For<ITeamspeakQueryClient>();
        query.ExecuteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns("TS3\nerror id=520 msg=invalid\\sloginname\\sor\\spassword\nerror id=1024 msg=invalid\\sserverID\n");

        var dto = await Build(logger, running: true, Password, query).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, dto.ChannelCount);
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains("id 520: invalid loginname or password", StringComparison.Ordinal));
        Assert.DoesNotContain(logger.Entries, entry => entry.Message.Contains(Password, StringComparison.Ordinal));
    }

    [Fact]
    public async Task SuccessfulCollection_LogsWhatWasCollectedOnce()
    {
        var logger = new RecordingLogger<TeamspeakCollector>();
        var query = Substitute.For<ITeamspeakQueryClient>();
        query.ExecuteAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(
            "error id=0 msg=ok\nerror id=0 msg=ok\n" +
            "virtualserver_name=Guild virtualserver_version=3.13.7 virtualserver_port=9987\nerror id=0 msg=ok\n" +
            "clid=1 cid=1 client_nickname=Ann client_type=0|clid=2 cid=1 client_nickname=serveradmin client_type=1\nerror id=0 msg=ok\n" +
            "cid=1 pid=0 channel_name=Lobby total_clients=2|cid=2 pid=0 channel_name=AFK total_clients=0\nerror id=0 msg=ok\n");
        var collector = Build(logger, running: true, Password, query);

        await collector.CollectAsync(TestContext.Current.CancellationToken);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        var info = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Equal("TeamSpeak collection: server 'Guild' on ServerQuery port 10011, 2 channels, 1 clients online, 0 bans", info.Message);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task NotInstalled_SaysSoOnce()
    {
        var logger = new RecordingLogger<TeamspeakCollector>();
        var collector = Build(logger, running: false, credential: null, Substitute.For<ITeamspeakQueryClient>());

        await collector.CollectAsync(TestContext.Current.CancellationToken);
        await collector.CollectAsync(TestContext.Current.CancellationToken);

        var info = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.Contains("no running ts3server process", info.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACollectionThatThrows_IsAWarningWithTheException()
    {
        var logger = new RecordingLogger<TeamspeakCollector>();
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .ThrowsAsync(new InvalidOperationException("boom"));
        var collector = new TeamspeakCollector(logger, shell, Substitute.For<ITeamspeakQueryClient>(), fileExists: _ => false);

        await collector.CollectAsync(TestContext.Current.CancellationToken);

        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.IsType<InvalidOperationException>(warning.Exception);
    }

    [Fact]
    public async Task QueryClient_LogsWhyTheConnectionFailed()
    {
        var logger = new RecordingLogger<TcpTeamspeakQueryClient>();
        var transport = Substitute.For<ITeamspeakQueryTransport>();
        transport.ExchangeAsync(Arg.Any<int>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new SocketException((int)SocketError.ConnectionRefused));

        var result = await new TcpTeamspeakQueryClient(transport, logger)
            .ExecuteAsync(10011, $"login serveradmin {Password}\nquit\n", TestContext.Current.CancellationToken);

        Assert.Null(result);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains("127.0.0.1:10011 failed: SocketException", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Password, warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task QueryClient_LogsAnEmptyReply()
    {
        var logger = new RecordingLogger<TcpTeamspeakQueryClient>();
        var transport = Substitute.For<ITeamspeakQueryTransport>();
        transport.ExchangeAsync(Arg.Any<int>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>()).Returns(" \r\n");

        await new TcpTeamspeakQueryClient(transport, logger).ExecuteAsync(10011, "quit\n", TestContext.Current.CancellationToken);

        Assert.Contains("without any reply", Assert.Single(logger.Entries).Message, StringComparison.Ordinal);
    }

    /// <summary>Running state from `pidof`, no binary readable on disk (the VPS layout), and the
    /// credential file given by <paramref name="credential"/> (null: absent).</summary>
    private static TeamspeakCollector Build(
        RecordingLogger<TeamspeakCollector> logger, bool running, string? credential, ITeamspeakQueryClient query)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(1, string.Empty, string.Empty));
        if (running)
            shell.RunExecAsync("pidof", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
                .Returns(new ShellExecResult(0, "771", string.Empty));
        return new TeamspeakCollector(logger, shell, query,
            fileExists: path => credential is not null && path == CredentialPath,
            readText: (_, _) => Task.FromResult(credential ?? string.Empty));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }
}
