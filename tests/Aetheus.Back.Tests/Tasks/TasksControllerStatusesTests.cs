// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Tasks;

/// <summary>An agent reads the statuses of at most 100 tasks, always scoped to the server its token names.</summary>
public sealed class TasksControllerStatusesTests
{
    private readonly ITaskService _tasks = Substitute.For<ITaskService>();

    private TasksController Build(params Claim[] claims) => new(_tasks, Substitute.For<IResourceAuthorizationService>())
    {
        ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "agent")) },
        },
    };

    [Fact]
    public async Task TheStatuses_AreReadForTheServerOfTheToken()
    {
        var statuses = new Dictionary<int, string> { [1] = "Running", [2] = "Completed" };
        _tasks.GetTaskStatusesAsync(Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 1, 2 })), 4, Arg.Any<CancellationToken>())
            .Returns(statuses);

        var result = await Build(new Claim("ServerId", "4")).GetTaskStatuses([1, 2], TestContext.Current.CancellationToken);

        Assert.Same(statuses, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task MoreThanAHundredTasks_IsABadRequest()
    {
        var result = await Build(new Claim("ServerId", "4")).GetTaskStatuses([.. Enumerable.Range(1, 101)], TestContext.Current.CancellationToken);

        Assert.IsType<BadRequestResult>(result.Result);
        await _tasks.DidNotReceiveWithAnyArgs().GetTaskStatusesAsync(default!, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("four")]
    public async Task ATokenWithoutAServerId_IsForbidden(string? serverId)
    {
        var claims = serverId is null ? Array.Empty<Claim>() : [new Claim("ServerId", serverId)];

        var result = await Build(claims).GetTaskStatuses([1], TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _tasks.DidNotReceiveWithAnyArgs().GetTaskStatusesAsync(default!, default, TestContext.Current.CancellationToken);
    }
}
