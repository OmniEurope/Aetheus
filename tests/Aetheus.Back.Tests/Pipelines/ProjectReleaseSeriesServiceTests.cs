// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The administrator's « fix the next version »: validated, audited, and refused with a 409
/// when a run of the series already took the number.</summary>
public sealed class ProjectReleaseSeriesServiceTests
{
    private readonly IProjectReleaseSeriesRepository _repository = Substitute.For<IProjectReleaseSeriesRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IResourceAuthorizationService _authorization = Substitute.For<IResourceAuthorizationService>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private ProjectReleaseSeriesService Service() => new(_repository, _audit);

    private ProjectReleaseSeriesController Controller() => new(Service(), _authorization)
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
    };

    [Fact]
    public async Task SettingTheNextVersion_SetsTheNumberOfTheSeriesAndAuditsIt()
    {
        _repository.SetNextAsync(3, "1.4", 0, Ct).Returns(SetNextReleaseNumberOutcome.Set);

        var result = await Service().SetNextAsync(
            3, new SetNextReleaseNumberRequest { Series = " 1.4 ", NextNumber = 0 }, Ct);

        Assert.Equal("1.4", result!.Series);
        Assert.Equal(0, result.NextNumber);
        await _audit.Received(1).LogAsync(
            "NextReleaseNumberSet", "Project", 3, Arg.Is<string?>(details => details!.Contains("1.4.0")), Ct);
    }

    [Fact]
    public async Task ANumberAlreadyTaken_IsAConflictNamingTheVersion_AndIsNotAudited()
    {
        _repository.SetNextAsync(3, "1.4", 0, Ct).Returns(SetNextReleaseNumberOutcome.NumberAlreadyUsed);

        var error = await Assert.ThrowsAsync<ConflictException>(() => Service().SetNextAsync(
            3, new SetNextReleaseNumberRequest { Series = "1.4", NextNumber = 0 }, Ct));

        Assert.Contains("1.4.0", error.Message, StringComparison.Ordinal);
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData("1.4.0", 0)]
    [InlineData("", 0)]
    [InlineData("one.two", 0)]
    [InlineData("1.4", -1)]
    public async Task ASeriesThatIsNotMajorMinorOrANegativeNumber_IsRefusedBeforeTheCounterIsTouched(string series, int number)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => Service().SetNextAsync(
            3, new SetNextReleaseNumberRequest { Series = series, NextNumber = number }, Ct));

        await _repository.DidNotReceiveWithAnyArgs().SetNextAsync(default, default!, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnUnknownProject_IsNull()
    {
        _repository.SetNextAsync(404, "1.4", 0, Ct).Returns(SetNextReleaseNumberOutcome.ProjectNotFound);

        Assert.Null(await Service().SetNextAsync(404, new SetNextReleaseNumberRequest { Series = "1.4" }, Ct));
    }

    [Fact]
    public async Task TheController_SettingRequiresProjectAdmin_ReadingRequiresProjectRead()
    {
        _authorization.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 3, Permission.Admin, Ct).Returns(false);
        _authorization.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 3, Permission.Read, Ct).Returns(true);
        _repository.GetSeriesAsync(3, Ct).Returns([new ProjectReleaseSeriesDto { Series = "1.4", NextNumber = 2 }]);

        var set = await Controller().SetNext(3, new SetNextReleaseNumberRequest { Series = "1.4" }, Ct);
        var read = await Controller().Get(3, Ct);

        Assert.IsType<ForbidResult>(set.Result);
        await _repository.DidNotReceiveWithAnyArgs().SetNextAsync(default, default!, default, TestContext.Current.CancellationToken);
        Assert.Equal("1.4", Assert.IsType<List<ProjectReleaseSeriesDto>>(Assert.IsType<OkObjectResult>(read.Result).Value)[0].Series);
    }

    [Fact]
    public async Task TheController_AnUnknownProjectIsNotFound()
    {
        _authorization.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Project, 404, Arg.Any<Permission>(), Ct).Returns(true);
        _repository.GetSeriesAsync(404, Ct).Returns((List<ProjectReleaseSeriesDto>?)null);

        Assert.IsType<NotFoundResult>((await Controller().Get(404, Ct)).Result);
    }
}
