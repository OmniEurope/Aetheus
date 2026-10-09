// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// PLAN-005 lot 2. The on-demand scan must actually report, because the UI tells the operator the
/// refresh happened. A failure to report has to surface as a failed task, never as a silent success.
/// </summary>
public class PortsObserveOperationExecutorTests
{
    private readonly IListeningPortsCollector _collector = Substitute.For<IListeningPortsCollector>();
    private readonly IServerApiClient _apiClient = Substitute.For<IServerApiClient>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 9, 7, 8, 0, 0, TimeSpan.Zero));

    private PortsObserveOperationExecutor Build(int? serverId = 7)
    {
        var state = new AgentState(_time) { ServerId = serverId };
        return new PortsObserveOperationExecutor(
            _collector, _apiClient, state, _time, NullLogger<PortsObserveOperationExecutor>.Instance);
    }

    private static Task NoOutput(string _, TaskLogLevel __) => Task.CompletedTask;

    [Theory]
    [InlineData(OperationKind.PortsObserve, true)]
    [InlineData(OperationKind.PortsentryStatus, false)]
    [InlineData(OperationKind.FirewallAllow, false)]
    public void CanHandle_OnlyClaimsTheScan(OperationKind kind, bool expected)
        => Assert.Equal(expected, Build().CanHandle(kind));

    [Fact]
    public async Task Execute_ReportsWhatTheCollectorSaw()
    {
        _collector.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<ObservedPortDto>
        {
            new() { Port = 10031, Protocol = "tcp", Holder = "portfolio-prod-front", Interface = "0.0.0.0" }
        });

        var result = await Build().ExecuteAsync(
            OperationKind.PortsObserve, string.Empty, 30, NoOutput, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        // A completed report is a success; TimedOut=true made the task read as a timeout.
        Assert.False(result.TimedOut);
        await _apiClient.Received(1).ReportObservedPortsAsync(
            7,
            Arg.Is<ObservedPortsReportDto>(report =>
                report.Ports.Count == 1 && report.Ports[0].Port == 10031),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_EmptyScan_StillReports()
    {
        // "Nothing is listening" is a real answer the registry must record; skipping the call would
        // leave stale observations in place and let the page claim ports are still taken.
        _collector.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<ObservedPortDto>());

        var result = await Build().ExecuteAsync(
            OperationKind.PortsObserve, string.Empty, 30, NoOutput, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        await _apiClient.Received(1).ReportObservedPortsAsync(
            7, Arg.Is<ObservedPortsReportDto>(report => report.Ports.Count == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ReportFails_FailsTheTask()
    {
        _collector.CollectAsync(Arg.Any<CancellationToken>()).Returns(new List<ObservedPortDto>());
        _apiClient
            .ReportObservedPortsAsync(Arg.Any<int>(), Arg.Any<ObservedPortsReportDto>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new HttpRequestException("backend down")));

        var messages = new List<string>();
        var result = await Build().ExecuteAsync(
            OperationKind.PortsObserve, string.Empty, 30,
            (message, _) => { messages.Add(message); return Task.CompletedTask; },
            TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);

        Assert.Contains(messages, message => message.Contains("could not be reported", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Execute_CollectorThrows_FailsWithoutReporting()
    {
        _collector.CollectAsync(Arg.Any<CancellationToken>())
            .Returns<List<ObservedPortDto>>(_ => throw new InvalidOperationException("ss missing"));

        var result = await Build().ExecuteAsync(
            OperationKind.PortsObserve, string.Empty, 30, NoOutput, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        await _apiClient.DidNotReceive().ReportObservedPortsAsync(
            Arg.Any<int>(), Arg.Any<ObservedPortsReportDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_NotEnrolled_RefusesInsteadOfGuessingAServer()
    {
        var result = await Build(serverId: null).ExecuteAsync(
            OperationKind.PortsObserve, string.Empty, 30, NoOutput, TestContext.Current.CancellationToken);

        Assert.NotEqual(0, result.ExitCode);
        await _apiClient.DidNotReceive().ReportObservedPortsAsync(
            Arg.Any<int>(), Arg.Any<ObservedPortsReportDto>(), Arg.Any<CancellationToken>());
    }
}
