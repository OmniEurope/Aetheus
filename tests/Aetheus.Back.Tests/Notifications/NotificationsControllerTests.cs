// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Notifications;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class NotificationsControllerTests
{
    private readonly INotificationService _serviceMock = Substitute.For<INotificationService>();
    private readonly NotificationsController _sut;

    public NotificationsControllerTests()
    {
        _sut = new NotificationsController(_serviceMock);
        _sut.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, "1"), new Claim(ClaimTypes.Role, "Admin")], "test"))
            }
        };
    }

    [Fact]
    public async Task GetChannels_ReturnsOk()
    {
        _serviceMock.GetChannelsAsync(Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<NotificationChannelDto>
            {
                Items = [new NotificationChannelDto { Id = 1, Name = "Slack" }],
                TotalCount = 1
            });

        var result = await _sut.GetChannels(new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(Assert.IsType<PaginatedResult<NotificationChannelDto>>(ok.Value).Items);
    }

    [Fact]
    public async Task GetChannel_Found_ReturnsOk()
    {
        _serviceMock.GetChannelAsync(1, Arg.Any<CancellationToken>())
            .Returns(new NotificationChannelDto { Id = 1, Name = "Slack" });

        var result = await _sut.GetChannel(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task GetChannel_NotFound_ReturnsNotFound()
    {
        _serviceMock.GetChannelAsync(999, Arg.Any<CancellationToken>())
            .Returns((NotificationChannelDto?)null);

        var result = await _sut.GetChannel(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateChannel_ReturnsCreated()
    {
        _serviceMock.CreateChannelAsync(Arg.Any<CreateNotificationChannelRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationChannelDto { Id = 1, Name = "Email" });

        var result = await _sut.CreateChannel(new CreateNotificationChannelRequest { Name = "Email", Type = NotificationChannelType.Email }, TestContext.Current.CancellationToken);

        Assert.IsType<CreatedAtActionResult>(result.Result);
    }

    [Fact]
    public async Task UpdateChannel_Found_ReturnsOk()
    {
        _serviceMock.UpdateChannelAsync(1, Arg.Any<UpdateNotificationChannelRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationChannelDto { Id = 1, Name = "Updated" });

        var result = await _sut.UpdateChannel(1, new UpdateNotificationChannelRequest { Name = "Updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateChannel_NotFound_ReturnsNotFound()
    {
        _serviceMock.UpdateChannelAsync(999, Arg.Any<UpdateNotificationChannelRequest>(), Arg.Any<CancellationToken>())
            .Returns((NotificationChannelDto?)null);

        var result = await _sut.UpdateChannel(999, new UpdateNotificationChannelRequest { Name = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteChannel_Deleted_ReturnsNoContent()
    {
        _serviceMock.DeleteChannelAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteChannel(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteChannel_NotFound_ReturnsNotFound()
    {
        _serviceMock.DeleteChannelAsync(999, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.DeleteChannel(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task GetRules_ReturnsOk()
    {
        _serviceMock.GetRulesAsync(Arg.Any<PaginationRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PaginatedResult<NotificationRuleDto>
            {
                Items = [new NotificationRuleDto { Id = 1, EventType = "alert.triggered" }],
                TotalCount = 1
            });

        var result = await _sut.GetRules(new PaginationRequest(), TestContext.Current.CancellationToken);

        var ok = Assert.IsType<OkObjectResult>(result.Result);
        Assert.Single(Assert.IsType<PaginatedResult<NotificationRuleDto>>(ok.Value).Items);
    }

    [Fact]
    public async Task TestChannel_Found_ReturnsOk()
    {
        _serviceMock.TestChannelAsync(1, Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationTestResultDto { Status = NotificationTestStatus.Sent });

        var result = await _sut.TestChannel(1, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task TestChannel_NotFound_ReturnsNotFound()
    {
        _serviceMock.TestChannelAsync(999, Arg.Any<int?>(), Arg.Any<CancellationToken>()).Returns((NotificationTestResultDto?)null);

        var result = await _sut.TestChannel(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task CreateRule_ReturnsOk()
    {
        _serviceMock.CreateRuleAsync(Arg.Any<CreateNotificationRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationRuleDto { Id = 1, EventType = "pipeline.completed" });

        var result = await _sut.CreateRule(new CreateNotificationRuleRequest { NotificationChannelId = 1, EventType = "pipeline.completed" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateRule_Found_ReturnsOk()
    {
        _serviceMock.UpdateRuleAsync(1, Arg.Any<UpdateNotificationRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns(new NotificationRuleDto { Id = 1, EventType = "updated" });

        var result = await _sut.UpdateRule(1, new UpdateNotificationRuleRequest { EventType = "updated" }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
    }

    [Fact]
    public async Task UpdateRule_NotFound_ReturnsNotFound()
    {
        _serviceMock.UpdateRuleAsync(999, Arg.Any<UpdateNotificationRuleRequest>(), Arg.Any<CancellationToken>())
            .Returns((NotificationRuleDto?)null);

        var result = await _sut.UpdateRule(999, new UpdateNotificationRuleRequest { EventType = "X" }, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result.Result);
    }

    [Fact]
    public async Task DeleteRule_Deleted_ReturnsNoContent()
    {
        _serviceMock.DeleteRuleAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteRule(1, TestContext.Current.CancellationToken);

        Assert.IsType<NoContentResult>(result);
    }

    [Fact]
    public async Task DeleteRule_NotFound_ReturnsNotFound()
    {
        _serviceMock.DeleteRuleAsync(999, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.DeleteRule(999, TestContext.Current.CancellationToken);

        Assert.IsType<NotFoundResult>(result);
    }
}
