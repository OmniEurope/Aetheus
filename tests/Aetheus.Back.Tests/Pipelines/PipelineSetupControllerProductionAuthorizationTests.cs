// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Reading what the production deployment needs takes write access to the project (it reads the
/// repository); creating the variables, secrets and ports also takes library and vault writes, since it writes
/// to all three.</summary>
public sealed class PipelineSetupControllerProductionAuthorizationTests
{
    private static readonly PipelineProductionAnalysisRequest Analysis = new() { ProjectId = 7, ComposeFile = "deploy/compose.yml" };
    private static readonly PipelineProductionApplyRequest Apply = new() { ProjectId = 7, ComposeFile = "deploy/compose.yml" };

    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineProductionSetupService _production = Substitute.For<IPipelineProductionSetupService>();
    private readonly PipelineSetupController _sut;

    public PipelineSetupControllerProductionAuthorizationTests()
    {
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _authz.CanCreateInProjectAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<ResourceType>(), 7, Arg.Any<CancellationToken>())
            .Returns(true);
        _authz.GetUserOrganizationIdsAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns([3]);
        _authz.GetAccessibleResourceIdsAsync(Arg.Any<ClaimsPrincipal>(), ResourceType.Server, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns([4]);
        _production.AnalyzeAsync(7, "deploy/compose.yml", Arg.Any<ProductionServerScope>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineProductionAnalysisDto());
        _production.ApplyAsync(Apply, Arg.Any<ProductionServerScope>(), Arg.Any<CancellationToken>()).Returns(new PipelineProductionApplyResultDto());
        _sut = new PipelineSetupController(
            Substitute.For<IPipelineSetupReadinessService>(), Substitute.For<IPipelineRequirementsProvisioner>(),
            _production, Substitute.For<IPipelineProjectCopyService>(), Substitute.For<IPipelineOwnerAuthorization>(), _authz)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity("test")) } },
        };
    }

    /// <summary>Recette R-77: the servers a project with none may be offered are those the user administers.</summary>
    private static ProductionServerScope Administered =>
        Arg.Is<ProductionServerScope>(scope => scope.AdministeredServerIds != null && scope.AdministeredServerIds.SequenceEqual(new[] { 4 }));

    // A library or vault right (no id) is read in project 7's organization (RBAC audit, finding 9), so
    // denying it there denies it whatever another organization grants.
    private void Deny(ResourceType type, int? id, Permission permission)
    {
        if (id is null)
        {
            _authz.CanCreateInProjectAsync(Arg.Any<ClaimsPrincipal>(), type, 7, Arg.Any<CancellationToken>()).Returns(false);
            return;
        }
        _authz.HasPermissionAsync(Arg.Any<ClaimsPrincipal>(), type, id, permission, Arg.Any<CancellationToken>()).Returns(false);
    }

    [Fact]
    public async Task TheAnalysis_WithWriteOnTheProject_IsWorkedOut()
    {
        var result = await _sut.AnalyzeProduction(Analysis, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _production.Received(1).AnalyzeAsync(7, "deploy/compose.yml", Administered, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheAnalysis_WithoutWriteOnTheProject_IsForbidden_AndReadsNothing()
    {
        Deny(ResourceType.Project, 7, Permission.Write);

        var result = await _sut.AnalyzeProduction(Analysis, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _production.DidNotReceiveWithAnyArgs().AnalyzeAsync(default, default, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheApply_WithEveryRight_CreatesWhatTheComposeFileAsksFor()
    {
        var result = await _sut.ApplyProduction(Apply, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _production.Received(1).ApplyAsync(Apply, Administered, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheSampleRepair_WithWriteOnTheProjectAndItsLibraries_MendsTheSample()
    {
        _production.RepairEnvSampleAsync(7, Arg.Any<CancellationToken>()).Returns(new PipelineProductionRepairResultDto { Repaired = true });

        var result = await _sut.RepairProductionEnvSample(new PipelineProductionRepairRequest { ProjectId = 7 }, TestContext.Current.CancellationToken);

        Assert.IsType<OkObjectResult>(result.Result);
        await _production.Received(1).RepairEnvSampleAsync(7, Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(ResourceType.Project, 7)]
    [InlineData(ResourceType.VariableLibrary, null)]
    public async Task TheSampleRepair_WithoutOneOfTheTwoWrites_IsForbidden_AndWritesNothing(ResourceType type, int? id)
    {
        Deny(type, id, Permission.Write);

        var result = await _sut.RepairProductionEnvSample(new PipelineProductionRepairRequest { ProjectId = 7 }, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _production.DidNotReceiveWithAnyArgs().RepairEnvSampleAsync(default, TestContext.Current.CancellationToken);
    }

    [Theory]
    [InlineData(ResourceType.Project, 7)]
    [InlineData(ResourceType.VariableLibrary, null)]
    [InlineData(ResourceType.Vault, null)]
    public async Task TheApply_WithoutOneOfTheThreeWrites_IsForbidden_AndWritesNothing(ResourceType type, int? id)
    {
        Deny(type, id, Permission.Write);

        var result = await _sut.ApplyProduction(Apply, TestContext.Current.CancellationToken);

        Assert.IsType<ForbidResult>(result.Result);
        await _production.DidNotReceiveWithAnyArgs().ApplyAsync(default!, default!, TestContext.Current.CancellationToken);
    }
}
