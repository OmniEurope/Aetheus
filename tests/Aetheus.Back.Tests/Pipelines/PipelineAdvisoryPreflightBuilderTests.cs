// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The launch dialog's preview follows the chain, not only the pipeline being launched.
///
/// An orchestrator like aetheus-candidate has four trigger steps and no work of its own, so a
/// preview listing only its own stages showed the user a run whose actual work was invisible.
/// </summary>
public sealed class PipelineAdvisoryPreflightBuilderTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineVariableResolver _variables = Substitute.For<IPipelineVariableResolver>();
    private readonly IPipelineTemplateResolver _templates = Substitute.For<IPipelineTemplateResolver>();

    public PipelineAdvisoryPreflightBuilderTests()
    {
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([1]);
        _repo.FindAnyOnlineRunnerAsync(Arg.Any<int?>(), Arg.Any<OsType>(), Arg.Any<CancellationToken>())
            .Returns(new Server { Id = 1, Name = "runner-01", OrganizationId = 3 });
        _variables.ResolveVariablesWithWarningsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<Dictionary<string, string>?>(),
                Arg.Any<CancellationToken>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<string?>(),
                Arg.Any<int?>(), Arg.Any<bool>())
            .Returns(_ => Task.FromResult((
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
                new List<string>(),
                new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
    }

    private PipelineAdvisoryPreflightBuilder BuildSut() => new(
        _repo,
        new PipelineDispatchServerResolver(_repo),
        new PipelineChildPipelineResolver(_templates, _variables),
        Substitute.For<ILogger<PipelineAdvisoryPreflightBuilder>>());

    private static PipelineYamlDefinition Orchestrator(string childName) => new()
    {
        Name = "candidate",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Qualify",
                Agent = string.Empty,
                Steps =
                [
                    new PipelineStepDefinition { Name = "run qa", Type = "trigger", Pipeline = childName }
                ]
            }
        ]
    };

    private static PipelineYamlDefinition Child() => new()
    {
        Name = "child",
        Stages =
        [
            new PipelineStageDefinition
            {
                Name = "Validate",
                Agent = string.Empty,
                Steps = [new PipelineStepDefinition { Name = "test", Shell = "make test" }]
            }
        ]
    };

    private void ChildExists(string name, PipelineYamlDefinition definition)
    {
        var pipeline = new Pipeline { Id = 7, ProjectId = 5, Name = name, YamlDefinition = "name: child\nstages: []" };
        _repo.FindPipelineByNameAndProjectAsync(name, 5, Arg.Any<CancellationToken>()).Returns(pipeline);
        _templates.ResolveAsync(
                pipeline.YamlDefinition, Arg.Any<int>(),
                Arg.Any<IReadOnlyDictionary<string, string>?>(), Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateResolution(definition, pipeline.YamlDefinition, null, null, null, false));
    }

    private Task<PipelinePreflightDto> PreviewAsync(PipelineYamlDefinition definition, int? projectId = 5) =>
        BuildSut().BuildAsync(
            definition,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            warnings: [],
            organizationId: 3,
            projectId: projectId,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task ThePreviewListsTheStagesOfATriggeredPipelineOneLevelDown()
    {
        ChildExists("aetheus-qa", Child());

        var preview = await PreviewAsync(Orchestrator("aetheus-qa"));

        Assert.Collection(
            preview.Stages,
            own =>
            {
                Assert.Equal("Qualify", own.StageName);
                Assert.Equal(0, own.Depth);
                Assert.Null(own.PipelineName);
            },
            child =>
            {
                Assert.Equal("Validate", child.StageName);
                Assert.Equal(1, child.Depth);
                Assert.Equal("aetheus-qa", child.PipelineName);
                Assert.True(child.Resolved);
                Assert.Equal("runner-01", child.ServerName);
            });
    }

    [Fact]
    public async Task AChildThatDoesNotExistLeavesThePreviewWithTheLaunchingPipelineAlone()
    {
        // The preview never refuses a launch, so an unresolvable child costs a missing line, not an error.
        _repo.FindPipelineByNameAndProjectAsync("aetheus-qa", 5, Arg.Any<CancellationToken>())
            .Returns((Pipeline?)null);

        var preview = await PreviewAsync(Orchestrator("aetheus-qa"));

        var only = Assert.Single(preview.Stages);
        Assert.Equal("Qualify", only.StageName);
        Assert.Equal(0, only.Depth);
    }

    [Fact]
    public async Task WithoutAProjectTheChainIsNotFollowed()
    {
        ChildExists("aetheus-qa", Child());

        var preview = await PreviewAsync(Orchestrator("aetheus-qa"), projectId: null);

        Assert.Single(preview.Stages);
    }
}
