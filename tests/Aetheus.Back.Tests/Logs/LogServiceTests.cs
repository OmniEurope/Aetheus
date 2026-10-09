// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class LogServiceTests
{
    private readonly ILogRepository _repoMock = Substitute.For<ILogRepository>();
    private readonly IHubContext<LogHub> _hubMock = Substitute.For<IHubContext<LogHub>>();
    private readonly IClientProxy _clientProxyMock = Substitute.For<IClientProxy>();
    private readonly ISecretMaskingService _secretMaskingMock = Substitute.For<ISecretMaskingService>();
    private readonly IEncryptionService _encryptionMock = Substitute.For<IEncryptionService>();
    private readonly LogService _sut;

    public LogServiceTests()
    {
        var groupMock = Substitute.For<IClientProxy>();
        _clientProxyMock = groupMock;
        var clientsMock = Substitute.For<IHubClients>();
        clientsMock.Group(Arg.Any<string>()).Returns(groupMock);
        _hubMock.Clients.Returns(clientsMock);

        // Default: masking returns message unchanged
        _secretMaskingMock.MaskAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<string>(0));

        _encryptionMock.DecryptValue(Arg.Any<string>())
            .Returns(ci => ci.ArgAt<string>(0));

        _sut = new LogService(_repoMock, _hubMock, _secretMaskingMock, _encryptionMock);
    }

    [Fact]
    public async Task GetTaskLogsAsync_ReturnsMappedDtos()
    {
        var logs = new List<TaskLog>
        {
            new() { Id = 1, TaskId = 10, Level = TaskLogLevel.Info, Message = "Started", Timestamp = DateTime.UtcNow },
            new() { Id = 2, TaskId = 10, Level = TaskLogLevel.Error, Message = "Failed", Timestamp = DateTime.UtcNow }
        };
        _repoMock.GetTaskLogsAsync(10, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(logs);

        var result = await _sut.GetTaskLogsAsync(10, null, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("Started", result[0].Message);
        Assert.Equal(TaskLogLevel.Error, result[1].Level);
    }

    [Fact]
    public async Task GetTaskLogsAsync_EmptyList_ReturnsEmpty()
    {
        _repoMock.GetTaskLogsAsync(99, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns([]);

        var result = await _sut.GetTaskLogsAsync(99, null, ct: TestContext.Current.CancellationToken);

        Assert.Empty(result);
    }

    [Fact]
    public async Task AppendLogAsync_AddsLogAndBroadcasts()
    {
        var request = new AppendLogRequest { TaskId = 5, Level = TaskLogLevel.Info, Message = "Hello" };

        await _sut.AppendLogAsync(request, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddLogAsync(Arg.Is<TaskLog>(l => l.TaskId == 5 && l.Message == "Hello"), Arg.Any<CancellationToken>());
        await _clientProxyMock.Received(1).SendCoreAsync("LogReceived", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendLogsAsync_AddsBatchAndBroadcastsEach()
    {
        var requests = new List<AppendLogRequest>
        {
            new() { TaskId = 1, Level = TaskLogLevel.Info, Message = "L1" },
            new() { TaskId = 1, Level = TaskLogLevel.Debug, Message = "L2" },
            new() { TaskId = 2, Level = TaskLogLevel.Error, Message = "L3" }
        };

        await _sut.AppendLogsAsync(requests, ct: TestContext.Current.CancellationToken);

        await _repoMock.Received(1).AddLogsAsync(Arg.Is<List<TaskLog>>(l => l.Count == 3), Arg.Any<CancellationToken>());
        // F20: one batched "LogsReceived" send per distinct TaskId (TaskId 1 + TaskId 2 = 2 sends).
        await _clientProxyMock.Received(2).SendCoreAsync("LogsReceived", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendLogsAsync_BroadcastLinesCarryTheirPersistedIds()
    {
        // FRONT-010: without the id a streamed line could not be told apart from the same line read
        // again in a snapshot or in a page of the full log, so the run page duplicated or lost lines.
        _repoMock.AddLogsAsync(Arg.Any<List<TaskLog>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = 100;
                foreach (var log in call.Arg<List<TaskLog>>()) log.Id = ++id;
                return Task.CompletedTask;
            });
        List<TaskLogDto>? sent = null;
        _clientProxyMock.When(proxy => proxy.SendCoreAsync("LogsReceived", Arg.Any<object?[]>(), Arg.Any<CancellationToken>()))
            .Do(call => sent = (List<TaskLogDto>)call.ArgAt<object?[]>(1)[0]!);

        await _sut.AppendLogsAsync(
        [
            new() { TaskId = 1, Level = TaskLogLevel.Info, Message = "L1" },
            new() { TaskId = 1, Level = TaskLogLevel.Info, Message = "L2" }
        ], ct: TestContext.Current.CancellationToken);

        Assert.Equal([101, 102], sent!.Select(line => line.Id));
    }

    [Fact]
    public async Task AppendLogsAsync_EmptyList_DoesNotCallRepo()
    {
        var requests = new List<AppendLogRequest>();

        await _sut.AppendLogsAsync(requests, ct: TestContext.Current.CancellationToken);

        await _repoMock.DidNotReceive().AddLogsAsync(Arg.Any<List<TaskLog>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetTaskLogsAsync_MapsAllFields()
    {
        var ts = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var logs = new List<TaskLog>
        {
            new() { Id = 42, TaskId = 7, Level = TaskLogLevel.Warning, Message = "Warn msg", Timestamp = ts }
        };
        _repoMock.GetTaskLogsAsync(7, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(logs);

        var result = await _sut.GetTaskLogsAsync(7, null, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal(42, result[0].Id);
        Assert.Equal(7, result[0].TaskId);
        Assert.Equal(TaskLogLevel.Warning, result[0].Level);
        Assert.Equal("Warn msg", result[0].Message);
        Assert.Equal(ts, result[0].Timestamp);
    }

    [Fact]
    public async Task GetTaskLogsUnmaskedAsync_ReturnsOriginalMessage()
    {
        var ts = DateTime.UtcNow;
        _repoMock.GetTaskLogsAsync(1, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([new TaskLog { Id = 1, TaskId = 1, Message = "masked", OriginalMessage = "secret-value", Timestamp = ts }]);

        var result = await _sut.GetTaskLogsUnmaskedAsync(1, null, ct: TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("secret-value", result[0].Message);
    }

    [Fact]
    public async Task GetTaskLogsUnmaskedAsync_FallsBackToMessage_WhenOriginalIsNull()
    {
        _repoMock.GetTaskLogsAsync(1, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([new TaskLog { Id = 1, TaskId = 1, Message = "normal", OriginalMessage = null }]);

        var result = await _sut.GetTaskLogsUnmaskedAsync(1, null, ct: TestContext.Current.CancellationToken);

        Assert.Equal("normal", result[0].Message);
    }
}

