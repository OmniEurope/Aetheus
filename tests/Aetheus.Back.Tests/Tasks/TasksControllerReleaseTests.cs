// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>An agent hands a claimed task back only for its own server and with a fenced lease; the
/// service decides whether the task is still its to release.</summary>
public sealed class TasksControllerReleaseTests
{
    private static readonly AgentTaskLeaseRequest Lease = new() { AgentSessionId = Guid.NewGuid().ToString("N"), AgentSessionFencingToken = 3 };

    private readonly ITaskService _tasks = Substitute.For<ITaskService>();
    private readonly TasksController _sut;

    public TasksControllerReleaseTests()
    {
        _tasks.GetTaskServerIdAsync(11, Arg.Any<CancellationToken>()).Returns(4);
        _sut = new TasksController(_tasks, Substitute.For<IResourceAuthorizationService>())
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("ServerId", "4")], "agent")) },
            },
        };
    }

    [Theory]
    [InlineData(true, typeof(OkResult))]
    [InlineData(false, typeof(ConflictResult))]
    public async Task AFencedReleaseOfItsOwnTask_IsDecidedByTheService(bool released, Type expected)
    {
        _tasks.ReleaseAssignedTaskAsync(11, Lease, Arg.Any<CancellationToken>()).Returns(released);

        var result = await _sut.ReleaseTask(11, Lease, TestContext.Current.CancellationToken);

        Assert.IsType(expected, result);
    }

    [Fact]
    public async Task AnUnknownTask_IsNotFound() =>
        Assert.IsType<NotFoundResult>(await _sut.ReleaseTask(12, Lease, TestContext.Current.CancellationToken));

    [Fact]
    public async Task AnotherServersTask_IsForbidden()
    {
        _tasks.GetTaskServerIdAsync(11, Arg.Any<CancellationToken>()).Returns(5);

        Assert.IsType<ForbidResult>(await _sut.ReleaseTask(11, Lease, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData("not-a-guid", 3)]
    [InlineData(null, 0)]
    public async Task AnUnfencedLease_IsRefused(string? session, long token)
    {
        var lease = new AgentTaskLeaseRequest { AgentSessionId = session ?? Guid.NewGuid().ToString("N"), AgentSessionFencingToken = token };

        Assert.IsType<BadRequestResult>(await _sut.ReleaseTask(11, lease, TestContext.Current.CancellationToken));
        await _tasks.DidNotReceiveWithAnyArgs().ReleaseAssignedTaskAsync(default, default!, TestContext.Current.CancellationToken);
    }
}
