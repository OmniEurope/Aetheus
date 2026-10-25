// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The fleet's template filter is scoped to what the caller can read (an administrator sees the
/// whole fleet, anyone else their organizations), and the setup wizard's pipeline readiness and
/// provisioning answer only a caller holding every right they need.</summary>
public sealed class PipelineSetupAndFleetControllerTests
{
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineFleetService _fleet = Substitute.For<IPipelineFleetService>();
    private readonly IPipelineSetupReadinessService _readiness = Substitute.For<IPipelineSetupReadinessService>();
    private readonly IPipelineRequirementsProvisioner _provisioner = Substitute.For<IPipelineRequirementsProvisioner>();
    private readonly CancellationToken _ct = TestContext.Current.CancellationToken;

    private static ControllerContext As(bool admin) => new()
    {
        HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                admin ? [new Claim(ClaimTypes.Role, "Admin")] : [new Claim(ClaimTypes.Name, "user")], "test"))
        }
    };

    private PipelineFleetController Fleet(bool admin) => new(_fleet, _authz) { ControllerContext = As(admin) };

    private PipelineSetupController Setup(bool admin = false) => new(
        _readiness, _provisioner, Substitute.For<IPipelineProductionSetupService>(), Substitute.For<IPipelineProjectCopyService>(),
        Substitute.For<IPipelineOwnerAuthorization>(), _authz)
    { ControllerContext = As(admin) };

    private void Allow(ResourceType type, int? id, Permission permission, bool allowed) =>
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), type, id, permission, Arg.Any<CancellationToken>()).Returns(allowed);

    private void AccessibleIds(ResourceType type, List<int>? ids) =>
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), type, Permission.Read, Arg.Any<CancellationToken>()).Returns(ids);

    [Fact]
    public async Task FleetFilterValues_ForACallerWhoCanReadNoPipeline_AreEmptyWithoutAskingTheFleet()
    {
        AccessibleIds(ResourceType.Pipeline, []);

        var result = await Fleet(admin: false).GetFleetFilterValues(_ct);

        Assert.Empty(Assert.IsType<PipelineFleetFilterValuesDto>(Assert.IsType<OkObjectResult>(result.Result).Value).Templates);
        await _fleet.DidNotReceiveWithAnyArgs().GetFilterValuesAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task FleetFilterValues_ForAnAdministrator_AreNotNarrowedToOrganizations()
    {
        AccessibleIds(ResourceType.Pipeline, null);
        var values = new PipelineFleetFilterValuesDto { Templates = ["application-candidate"] };
        _fleet.GetFilterValuesAsync(null, null, Arg.Any<CancellationToken>()).Returns(values);

        var result = await Fleet(admin: true).GetFleetFilterValues(_ct);

        Assert.Same(values, Assert.IsType<OkObjectResult>(result.Result).Value);
        await _authz.DidNotReceive().GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task FleetFilterValues_ForAnotherCaller_AreNarrowedToTheirOrganizationsAndPipelines()
    {
        var pipelines = new List<int> { 4, 5 };
        var organizations = new List<int> { 9 };
        AccessibleIds(ResourceType.Pipeline, pipelines);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(organizations);
        var values = new PipelineFleetFilterValuesDto { Templates = ["t"] };
        _fleet.GetFilterValuesAsync(organizations, pipelines, Arg.Any<CancellationToken>()).Returns(values);

        var result = await Fleet(admin: false).GetFleetFilterValues(_ct);

        Assert.Same(values, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Fact]
    public async Task PipelineReadiness_WithoutReadOnThePipeline_IsForbidden()
    {
        Allow(ResourceType.Pipeline, 3, Permission.Read, false);

        var result = await Setup().CheckPipelineReadiness(3, _ct);

        Assert.IsType<ForbidResult>(result.Result);
        await _readiness.DidNotReceiveWithAnyArgs().FindPipelineProjectIdAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PipelineReadiness_OfAPipelineWithNoProject_IsAnEmptyVerdict()
    {
        Allow(ResourceType.Pipeline, 3, Permission.Read, true);
        _readiness.FindPipelineProjectIdAsync(3, Arg.Any<CancellationToken>()).Returns((int?)null);

        var result = await Setup().CheckPipelineReadiness(3, _ct);

        Assert.NotNull(Assert.IsType<PipelineSetupReadinessDto>(Assert.IsType<OkObjectResult>(result.Result).Value));
        await _readiness.DidNotReceiveWithAnyArgs().CheckProjectAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task PipelineReadiness_WithoutReadOnTheProject_IsForbidden()
    {
        Allow(ResourceType.Pipeline, 3, Permission.Read, true);
        _readiness.FindPipelineProjectIdAsync(3, Arg.Any<CancellationToken>()).Returns(8);
        Allow(ResourceType.Project, 8, Permission.Read, false);

        var result = await Setup().CheckPipelineReadiness(3, _ct);

        Assert.IsType<ForbidResult>(result.Result);
        await _readiness.DidNotReceiveWithAnyArgs().CheckProjectAsync(default, default, default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(false, 5, 5)]
    [InlineData(false, 0, null)]
    [InlineData(true, 5, null)]
    public async Task PipelineReadiness_IsAnsweredWithinTheCallersOrganization_OrTheWholeFleetForAnAdministrator(
        bool admin, int firstOrganization, int? expectedOrganization)
    {
        Allow(ResourceType.Pipeline, 3, Permission.Read, true);
        Allow(ResourceType.Project, 8, Permission.Read, true);
        _readiness.FindPipelineProjectIdAsync(3, Arg.Any<CancellationToken>()).Returns(8);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([firstOrganization]);
        var verdict = new PipelineSetupReadinessDto();
        _readiness.CheckProjectAsync(8, 3, expectedOrganization, Arg.Any<CancellationToken>()).Returns(verdict);

        var result = await Setup(admin).CheckPipelineReadiness(3, _ct);

        Assert.Same(verdict, Assert.IsType<OkObjectResult>(result.Result).Value);
    }

    [Theory]
    [InlineData(ResourceType.Project, 6)]
    [InlineData(ResourceType.VariableLibrary, null)]
    [InlineData(ResourceType.Vault, null)]
    public async Task Provisioning_WithoutOneOfTheThreeWrites_IsForbiddenAndCreatesNothing(ResourceType missing, int? id)
    {
        Allow(ResourceType.Project, 6, Permission.Write, true);
        Allow(ResourceType.VariableLibrary, null, Permission.Write, true);
        Allow(ResourceType.Vault, null, Permission.Write, true);
        Allow(missing, id, Permission.Write, false);
        var request = new PipelineSetupReadinessRequest { ProjectId = 6, TemplateNames = ["t"] };

        var wizard = await Setup().ProvisionRequirements(request, _ct);
        var overview = await Setup().ProvisionUnmetRequirements(6, _ct);

        Assert.IsType<ForbidResult>(wizard.Result);
        Assert.IsType<ForbidResult>(overview.Result);
        await _provisioner.DidNotReceiveWithAnyArgs().ProvisionAsync(default, default!, default, default, default, TestContext.Current.CancellationToken);
        await _provisioner.DidNotReceiveWithAnyArgs().ProvisionUnmetAsync(default, default, default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Provisioning_WithAllThreeWrites_ReadsKeyNamesOnlyFromWhatTheCallerCanRead()
    {
        Allow(ResourceType.Project, 6, Permission.Write, true);
        Allow(ResourceType.VariableLibrary, null, Permission.Write, true);
        Allow(ResourceType.Vault, null, Permission.Write, true);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([2]);
        var libraries = new List<int> { 11 };
        var vaults = new List<int> { 12, 13 };
        AccessibleIds(ResourceType.VariableLibrary, libraries);
        AccessibleIds(ResourceType.Vault, vaults);
        var provisioned = new PipelineRequirementsProvisionResultDto();
        _provisioner.ProvisionUnmetAsync(6, 2, libraries, vaults, Arg.Any<CancellationToken>()).Returns(provisioned);
        _provisioner.ProvisionAsync(6, Arg.Is<IReadOnlyList<string>>(names => names.Single() == "t"), 2, libraries, vaults, Arg.Any<CancellationToken>())
            .Returns(provisioned);

        var overview = await Setup().ProvisionUnmetRequirements(6, _ct);
        var wizard = await Setup().ProvisionRequirements(new PipelineSetupReadinessRequest { ProjectId = 6, TemplateNames = ["t"] }, _ct);

        Assert.Same(provisioned, Assert.IsType<OkObjectResult>(overview.Result).Value);
        Assert.Same(provisioned, Assert.IsType<OkObjectResult>(wizard.Result).Value);
    }
}
