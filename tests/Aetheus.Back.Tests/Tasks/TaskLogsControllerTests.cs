// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Logs;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class TaskLogsControllerTests
{
    private readonly ILogService _serviceMock = Substitute.For<ILogService>();
    private readonly ITaskService _taskServiceMock = Substitute.For<ITaskService>();
    private readonly IResourceAuthorizationService _authzMock = Substitute.For<IResourceAuthorizationService>();
    private readonly TaskLogsController _sut;

    public TaskLogsControllerTests()
    {
        _taskServiceMock.GetTaskServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(1);
        _taskServiceMock.GetServerIdsForTasksAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<int>>(0).ToDictionary(id => id, _ => 1));
        _taskServiceMock.HasCurrentAgentLeaseAsync(
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _taskServiceMock.HaveCurrentAgentLeasesAsync(
                Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new TaskLogsController(_serviceMock, _taskServiceMock, _authzMock, Substitute.For<IAuditService>());
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("ServerId", "1")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetTaskLogs_ReturnsOkWithLogs()
    {
        var logs = new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 5, Level = TaskLogLevel.Info, Message = "ok" }
        };
        _serviceMock.GetTaskLogsAsync(5, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns(logs);

        var result = await _sut.GetTaskLogs(5, null, TestContext.Current.CancellationToken);

        var okResult = Assert.IsType<OkObjectResult>(result.Result);
        var data = Assert.IsType<List<TaskLogDto>>(okResult.Value);
        Assert.Single(data);
    }

    [Fact]
    public async Task GetTaskLogs_WithoutServerReadPermission_ForbidsAndDoesNotReadLogs()
    {
        // The task resolves to serverId 1 (ctor stub); deny Read on it and assert Forbid + no log read.
        _authzMock.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _sut.GetTaskLogs(5, null, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _serviceMock.DidNotReceive().GetTaskLogsAsync(Arg.Any<int>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendLog_ReturnsOk()
    {
        var request = new AppendLogRequest
        {
            TaskId = 1,
            AgentSessionId = "11111111111111111111111111111111",
            AgentSessionFencingToken = 1,
            Level = TaskLogLevel.Info,
            Message = "test"
        };

        var result = await _sut.AppendLog(request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        await _serviceMock.Received(1).AppendLogAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendLogs_ReturnsOk()
    {
        var requests = new List<AppendLogRequest>
        {
            new() { TaskId = 1, AgentSessionId = "11111111111111111111111111111111", AgentSessionFencingToken = 1, Level = TaskLogLevel.Info, Message = "a" },
            new() { TaskId = 1, AgentSessionId = "11111111111111111111111111111111", AgentSessionFencingToken = 1, Level = TaskLogLevel.Debug, Message = "b" }
        };

        var result = await _sut.AppendLogs(requests, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        await _serviceMock.Received(1).AppendLogsAsync(requests, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendLog_StaleLease_ReturnsConflict()
    {
        _taskServiceMock.HasCurrentAgentLeaseAsync(
                Arg.Any<int>(), Arg.Any<int>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(false);
        _taskServiceMock.AllowsLegacyUnfencedTaskProtocolAsync(
                1, Arg.Any<CancellationToken>())
            .Returns(true);
        var request = new AppendLogRequest
        {
            TaskId = 1,
            AgentSessionId = "11111111111111111111111111111111",
            AgentSessionFencingToken = 1,
            Message = "stale"
        };

        var result = await _sut.AppendLog(request, TestContext.Current.CancellationToken);

        Assert.IsType<ConflictResult>(result);
        await _serviceMock.DidNotReceive().AppendLogAsync(
            Arg.Any<AppendLogRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendLog_LegacyAgentWithoutLease_IsAcceptedForOwnedTask()
    {
        _taskServiceMock.AllowsLegacyUnfencedTaskProtocolAsync(
                1, Arg.Any<CancellationToken>())
            .Returns(true);
        var request = new AppendLogRequest
        {
            TaskId = 1,
            Level = TaskLogLevel.Info,
            Message = "legacy"
        };

        var result = await _sut.AppendLog(request, TestContext.Current.CancellationToken);

        Assert.IsType<OkResult>(result);
        await _serviceMock.Received(1).AppendLogAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AppendLog_LegacyAgentCannotWriteAnotherServersTask()
    {
        _taskServiceMock.GetTaskServerIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _taskServiceMock.AllowsLegacyUnfencedTaskProtocolAsync(
                1, Arg.Any<CancellationToken>())
            .Returns(true);
        var request = new AppendLogRequest { TaskId = 1, Message = "wrong server" };

        var result = await _sut.AppendLog(request, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result);
        await _serviceMock.DidNotReceive().AppendLogAsync(
            Arg.Any<AppendLogRequest>(), Arg.Any<CancellationToken>());
    }

    private const string Session = "11111111111111111111111111111111";

    private static AppendLogRequest Line(int taskId, string? session = Session, long? token = 1) =>
        new() { TaskId = taskId, AgentSessionId = session, AgentSessionFencingToken = token, Level = TaskLogLevel.Info, Message = "line" };

    [Fact]
    public async Task AppendLogs_AnEmptyBatch_IsAcceptedWithoutWriting()
    {
        Assert.IsType<OkResult>(await _sut.AppendLogs([], TestContext.Current.CancellationToken));
        await _serviceMock.DidNotReceiveWithAnyArgs().AppendLogsAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendLogs_MoreThanFiveHundredLines_IsRefused()
    {
        var result = await _sut.AppendLogs([.. Enumerable.Range(0, 501).Select(_ => Line(1))], TestContext.Current.CancellationToken);

        Assert.Equal("Batch size exceeds maximum of 500 entries.", Assert.IsType<ApiError>(Assert.IsType<BadRequestObjectResult>(result).Value).Message);
        await _serviceMock.DidNotReceiveWithAnyArgs().AppendLogsAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendLogs_WithoutAServerClaim_IsForbidden()
    {
        _sut.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ServerId", "not-a-number")], "test"));

        Assert.IsType<ForbidResult>(await _sut.AppendLogs([Line(1)], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AppendLogs_ALineOfAnotherServersTask_ForbidsTheWholeBatch()
    {
        _taskServiceMock.GetServerIdsForTasksAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, int> { [1] = 1, [2] = 2 });

        Assert.IsType<ForbidResult>(await _sut.AppendLogs([Line(1), Line(2), Line(3)], TestContext.Current.CancellationToken));
        await _serviceMock.DidNotReceiveWithAnyArgs().AppendLogsAsync(default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AppendLogs_AStaleLease_IsAConflict()
    {
        _taskServiceMock.HaveCurrentAgentLeasesAsync(
                Arg.Any<IReadOnlyCollection<int>>(), 1, Session, 1, Arg.Any<CancellationToken>())
            .Returns(false);

        Assert.IsType<ConflictResult>(await _sut.AppendLogs([Line(1), Line(2)], TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(true, typeof(OkResult))]
    [InlineData(false, typeof(ConflictResult))]
    public async Task AppendLogs_MixedOrMissingLeases_DependOnTheLegacyProtocol(bool legacyAllowed, Type expected)
    {
        _taskServiceMock.AllowsLegacyUnfencedTaskProtocolAsync(1, Arg.Any<CancellationToken>()).Returns(legacyAllowed);

        var result = await _sut.AppendLogs([Line(1), Line(1, "22222222222222222222222222222222")], TestContext.Current.CancellationToken);

        Assert.IsType(expected, result);
        await _taskServiceMock.DidNotReceiveWithAnyArgs().HaveCurrentAgentLeasesAsync(default!, default, default!, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(Session, 1L, true)]
    [InlineData("not-a-guid", 1L, false)]
    [InlineData(Session, 0L, false)]
    [InlineData(null, 1L, false)]
    public void SharesOneValidLease_RequiresAValidSessionAndToken(string? session, long token, bool expected) =>
        Assert.Equal(expected, TaskLogsController.SharesOneValidLease([Line(1, session, token), Line(2, session, token)]));

    [Fact]
    public void SharesOneValidLease_TwoFencingTokens_AreNotOneLease() =>
        Assert.False(TaskLogsController.SharesOneValidLease([Line(1, Session, 1), Line(2, Session, 2)]));
}
