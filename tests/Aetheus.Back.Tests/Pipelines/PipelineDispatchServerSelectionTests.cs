// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>The runner a non-deployment stage resolves to from an agent selector: inside the
/// pipeline's organization when it has one, never across it, and never broadened to another runner.</summary>
public class PipelineDispatchServerSelectionTests
{
    private readonly IPipelineRunnerTargetReader _reader = Substitute.For<IPipelineRunnerTargetReader>();
    private static readonly Dictionary<string, string> NoVars = new(StringComparer.Ordinal);

    private Task<Server?> ResolveAsync(PipelineStageDefinition stage, int? organizationId) =>
        new PipelineDispatchServerResolver(_reader).ResolveServerForTargetAsync(
            stage, NoVars, organizationId, deploymentStage: false, projectId: 4, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AnAgentSelector_InAnOrganization_IsLookedUpThere()
    {
        var runner = new Server { Id = 2, Name = "linux-02", OrganizationId = 9 };
        _reader.FindOnlineServerByAgentInOrganizationAsync("linux-02", OsType.Linux, 9, Arg.Any<CancellationToken>()).Returns(runner);

        Assert.Same(runner, await ResolveAsync(new PipelineStageDefinition { Name = "Build", Agent = "linux-02", Os = "linux" }, 9));
        await _reader.DidNotReceiveWithAnyArgs().FindAnyOnlineRunnerAsync(default, default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnAgentSelector_WithoutOrganization_UsesTheGlobalLookup()
    {
        var runner = new Server { Id = 3, Name = "linux-03" };
        _reader.FindOnlineServerByAgentAsync("linux-03", Arg.Any<OsType>(), Arg.Any<CancellationToken>()).Returns(runner);

        Assert.Same(runner, await ResolveAsync(new PipelineStageDefinition { Name = "Build", Agent = "linux-03" }, null));
    }

    [Fact]
    public async Task ARunnerOfAnotherOrganization_IsRefused_WithoutFallingBack()
    {
        _reader.FindOnlineServerByAgentInOrganizationAsync("shared-name", Arg.Any<OsType>(), 9, Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 5, Name = "shared-name", OrganizationId = 10 });
        _reader.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 6, Name = "fallback", OrganizationId = 9 });

        Assert.Null(await ResolveAsync(new PipelineStageDefinition { Name = "Build", Agent = "shared-name" }, 9));
    }

    [Fact]
    public async Task TheDefaultAgent_FallsBackToAnyRunnerOfTheOrganization()
    {
        var fallback = new Server { Id = 6, Name = "fallback", OrganizationId = 9 };
        _reader.FindAnyOnlineRunnerAsync(9, Arg.Any<OsType>(), Arg.Any<CancellationToken>()).Returns(fallback);

        Assert.Same(fallback, await ResolveAsync(new PipelineStageDefinition { Name = "Build", Agent = "Default" }, 9));
        await _reader.DidNotReceiveWithAnyArgs().FindOnlineServerByAgentInOrganizationAsync(default!, default, default, TestContext.Current.CancellationToken);
    }
}
