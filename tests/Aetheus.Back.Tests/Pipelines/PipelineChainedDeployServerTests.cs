// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-36 (run 3119): a run chained by <c>on_success:</c> receives no parameter but <c>candidateVersion</c>, so a required
/// <c>deployServer</c> without default made every automatic deployment refuse. The engine's safety net: the project's
/// only server is used; with several (or none) nothing is guessed and the refusal names the parameter and the fix.
/// </summary>
public sealed class PipelineChainedDeployServerTests
{
    private const int ProjectId = 21;
    private const int DeployId = 190;

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineRunPreparationService _preparations = Substitute.For<IPipelineRunPreparationService>();
    private readonly IPipelineRunParameterResolver _parameters = Substitute.For<IPipelineRunParameterResolver>();
    private readonly IPipelineSetupServerRepository _servers = Substitute.For<IPipelineSetupServerRepository>();
    private readonly IPipelineChildRunLauncher _launcher = Substitute.For<IPipelineChildRunLauncher>();
    private readonly PipelineTriggerStepCoordinator _sut;
    private readonly Pipeline _deploy = new() { Id = DeployId, Name = "oe1-deploy-prod", ProjectId = ProjectId, CreatedByUsername = "admin" };
    private IReadOnlyDictionary<string, string>? _resolvedWith;

    public PipelineChainedDeployServerTests()
    {
        var authz = Substitute.For<IResourceAuthorizationService>();
        authz.HasPermissionAsync(
                Arg.Any<string>(), Arg.Any<ResourceType>(), Arg.Any<int>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _sut = new PipelineTriggerStepCoordinator(
            _repo, Substitute.For<IPipelineGitService>(), _preparations, _parameters,
            Substitute.For<IPipelineCheckpointReuseService>(), authz, Substitute.For<IAuditService>(), _servers,
            new FakeTimeProvider(), NullLogger<PipelineTriggerStepCoordinator>.Instance);
        _repo.FindPipelineAsync(DeployId, Arg.Any<CancellationToken>()).Returns(_deploy);
        _repo.GetPipelineProjectIdAsync(_deploy, Arg.Any<CancellationToken>()).Returns(ProjectId);
        _parameters.ResolveRunParametersAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _resolvedWith = call.ArgAt<IReadOnlyDictionary<string, string>?>(1);
                return call.ArgAt<PipelineRunPreparation>(0);
            });
        _launcher.TriggerPreparedRunAsync(
                Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<Dictionary<string, string>?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(new PipelineRunDto { Id = 3200 });
    }

    private void DeployDeclares(PipelineTemplateParameterDefinition deployServer) =>
        _preparations.PrepareRunAsync(
                DeployId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunPreparation
            {
                PipelineId = DeployId,
                YamlSnapshot = "name: oe1-deploy-prod",
                TargetServerIds = [],
                Definition = new PipelineYamlDefinition
                {
                    Parameters =
                    [
                        new PipelineTemplateParameterDefinition { Name = "candidateVersion" },
                        deployServer
                    ]
                }
            });

    private static PipelineTemplateParameterDefinition RequiredNoDefault(params string[] allowed) => new()
    {
        Name = "deployServer",
        Required = true,
        Type = allowed.Length > 0 ? "choice" : "string",
        AllowedValues = [.. allowed]
    };

    private void ProjectHas(params string[] servers) =>
        _servers.ListProjectServerNamesAsync(ProjectId, Arg.Any<CancellationToken>()).Returns([.. servers]);

    private static Dictionary<string, string> Upstream() => new() { ["UPSTREAM_RELEASE"] = "c-eeabbd7a" };

    [Fact]
    public async Task RequiredDeployServerWithoutDefault_ProjectWithOneServer_UsesThatServer()
    {
        DeployDeclares(RequiredNoDefault("srv-prod"));
        ProjectHas("srv-prod");

        var outcome = await _sut.TriggerChainedRunAsync(DeployId, Upstream(), _launcher, TestContext.Current.CancellationToken);

        Assert.Equal(3200, outcome.Run?.Id);
        Assert.NotNull(_resolvedWith);
        Assert.Equal("srv-prod", _resolvedWith["deployServer"]);
        Assert.Equal("c-eeabbd7a", _resolvedWith["candidateVersion"]);
    }

    [Fact]
    public async Task RequiredDeployServerWithoutDefault_ProjectWithSeveralServers_IsRefusedByNameAndNothingIsLaunched()
    {
        DeployDeclares(RequiredNoDefault("srv-a", "srv-b"));
        ProjectHas("srv-a", "srv-b");

        var outcome = await _sut.TriggerChainedRunAsync(DeployId, Upstream(), _launcher, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Run);
        Assert.Equal("ChainedDeployServerMissing", outcome.Problem?.Code);
        Assert.Equal("deployServer", outcome.Problem?.Parameters["parameter"]);
        Assert.Equal("oe1-deploy-prod", outcome.Problem?.Parameters["pipeline"]);
        Assert.Contains("set the default of 'deployServer' in pipeline 'oe1-deploy-prod'", outcome.Reason, StringComparison.Ordinal);
        await _launcher.DidNotReceiveWithAnyArgs().TriggerPreparedRunAsync(
            default!, default, default, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task RequiredDeployServerWithoutDefault_ProjectWithoutServer_IsRefusedByName()
    {
        DeployDeclares(RequiredNoDefault());
        ProjectHas();

        var outcome = await _sut.TriggerChainedRunAsync(DeployId, Upstream(), _launcher, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Run);
        Assert.Equal("ChainedDeployServerMissing", outcome.Problem?.Code);
    }

    [Fact]
    public async Task OnlyServerOutsideTheAllowedValues_IsNotGuessed_ButRefused()
    {
        DeployDeclares(RequiredNoDefault("srv-a", "srv-b"));
        ProjectHas("srv-other");

        var outcome = await _sut.TriggerChainedRunAsync(DeployId, Upstream(), _launcher, TestContext.Current.CancellationToken);

        Assert.Null(outcome.Run);
        Assert.Equal("ChainedDeployServerMissing", outcome.Problem?.Code);
    }

    [Fact]
    public async Task DeployServerWithADefault_IsLeftAlone_ServersAreNotEvenRead()
    {
        DeployDeclares(new PipelineTemplateParameterDefinition { Name = "deployServer", Required = true, Default = "srv-a" });
        ProjectHas("srv-a", "srv-b");

        var outcome = await _sut.TriggerChainedRunAsync(DeployId, Upstream(), _launcher, TestContext.Current.CancellationToken);

        Assert.Equal(3200, outcome.Run?.Id);
        Assert.False(_resolvedWith!.ContainsKey("deployServer"));
        await _servers.DidNotReceiveWithAnyArgs().ListProjectServerNamesAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task APipelineWithoutDeployServerParameter_IsLeftAlone()
    {
        _preparations.PrepareRunAsync(
                DeployId, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineRunPreparation
            {
                PipelineId = DeployId,
                YamlSnapshot = "name: x",
                TargetServerIds = [],
                Definition = new PipelineYamlDefinition { Parameters = [new PipelineTemplateParameterDefinition { Name = "candidateVersion" }] }
            });

        var outcome = await _sut.TriggerChainedRunAsync(DeployId, Upstream(), _launcher, TestContext.Current.CancellationToken);

        Assert.Equal(3200, outcome.Run?.Id);
        await _servers.DidNotReceiveWithAnyArgs().ListProjectServerNamesAsync(default, TestContext.Current.CancellationToken);
    }
}
