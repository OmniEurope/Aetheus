// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-534: a pipeline definition may take its workspace from another repository of its project
/// (<c>source:</c> block) while the definition keeps its own repository and revision.
/// </summary>
public sealed class PipelineSourceBlockTests
{
    private const string DefinitionCommit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string WorkspaceCommit = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    private const string Yaml = """
        name: nightly-public
        trigger: manual
        source_branch: develop
        source:
          repository: aetheus-public
          branch: main
          must_match_definition: true
          match_exclude_file: .pipeline/configs/public-source/exclude.txt
        stages:
          - name: Build
            steps:
              - name: Compile
                shell: dotnet build
        """;

    [Fact]
    public void TheSourceBlock_IsReadFromTheYaml()
    {
        var definition = YamlParsingHelper.ParseAndValidate(Yaml, NullLogger.Instance);

        Assert.NotNull(definition?.Source);
        Assert.Equal(("aetheus-public", "main", true, ".pipeline/configs/public-source/exclude.txt"),
            (definition.Source.Repository, definition.Source.Branch, definition.Source.MustMatchDefinition, definition.Source.MatchExcludeFile));
        // The definition's own branch stays what it was.
        Assert.Equal("develop", definition.SourceBranch);
    }

    /// <summary>
    /// Recette R2-041 turned this test around deliberately: the reader no longer refuses a key it does
    /// not know (a YAML written for a newer backend must still run). The key is skipped and named in a
    /// warning, which is what the editor and the run show.
    /// </summary>
    [Fact]
    public void AnUnknownKeyInsideTheSourceBlock_IsSkipped_AndNamedInAWarning()
    {
        var yaml = Yaml.Replace("branch: main", "branche: main", StringComparison.Ordinal);

        var definition = YamlParsingHelper.ParseAndValidate(yaml, NullLogger.Instance);

        Assert.NotNull(definition?.Source);
        Assert.Null(definition.Source.Branch);
        Assert.Equal(
            ["Unknown top-level > source property 'branche' will be ignored."],
            PipelineYamlDiagnostics.UnknownPropertyWarnings(yaml, NullLogger.Instance));
    }

    [Theory]
    [InlineData("", "main", true, ".pipeline/configs/x.txt", "source.repository is required")]
    [InlineData("https://github.com/a/b", "main", false, null, "must be a repository slug")]
    [InlineData("aetheus-public", "bad branch", false, null, "source.branch is invalid")]
    [InlineData("aetheus-public", "main", true, "deploy/exclude.txt", "under .pipeline/configs/")]
    [InlineData("aetheus-public", "main", true, ".pipeline/configs/../secrets.txt", "under .pipeline/configs/")]
    [InlineData("aetheus-public", "main", false, ".pipeline/configs/x.txt", "only read when source.must_match_definition")]
    public void AnInvalidSourceBlock_IsAnError(
        string repository, string branch, bool mustMatch, string? excludeFile, string expected)
    {
        var errors = new List<string>();
        var definition = new PipelineYamlDefinition
        {
            Stages = [new PipelineStageDefinition { Name = "Build" }],
            Source = new PipelineSourceDefinition
            {
                Repository = repository,
                Branch = branch,
                MustMatchDefinition = mustMatch,
                MatchExcludeFile = excludeFile
            }
        };

        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, []);

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false, "public-distribution", "source.match_branch is only read when")]
    [InlineData(true, "bad branch", "source.match_branch is invalid")]
    public void AnInvalidMatchBranch_IsAnError(bool mustMatch, string matchBranch, string expected)
    {
        var errors = new List<string>();
        var definition = new PipelineYamlDefinition
        {
            Stages = [new PipelineStageDefinition { Name = "Build" }],
            Source = new PipelineSourceDefinition { Repository = "aetheus-public", MustMatchDefinition = mustMatch, MatchBranch = matchBranch }
        };

        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, []);

        Assert.Contains(errors, error => error.Contains(expected, StringComparison.Ordinal));
    }

    [Fact]
    public void AValidSourceBlock_RaisesNoError()
    {
        var errors = new List<string>();
        var definition = YamlParsingHelper.ParseAndValidate(Yaml, NullLogger.Instance)!;

        PipelineDefinitionValidator.ValidateDefinitionBasics(definition, errors, []);

        Assert.Empty(errors);
    }

    [Fact]
    public async Task PrepareRunAsync_WithASourceBlock_ChecksOutTheOtherRepository_AndKeepsTheDefinitionRevision()
    {
        var ct = TestContext.Current.CancellationToken;
        var (sut, workspaceSources, _) = BuildPreparation();
        workspaceSources.ResolveAsync(
                7, Arg.Is<PipelineSourceDefinition>(source => source.Repository == "aetheus-public" && source.MustMatchDefinition),
                10, DefinitionCommit, Arg.Any<CancellationToken>())
            .Returns(new PipelineWorkspaceSource(20, "https://api.example/git/7/aetheus-public.git", "main", WorkspaceCommit));

        var preparation = await sut.PrepareRunAsync(3, null, null, null, ct);

        Assert.NotNull(preparation);
        // What the runner clones: the other repository, at its own pinned commit.
        Assert.Equal(("main", WorkspaceCommit, "https://api.example/git/7/aetheus-public.git"),
            (preparation.BranchName, preparation.CommitHash, preparation.RepositoryUrl));
        // What the definition was read at: the pipeline's own repository.
        Assert.Equal((DefinitionCommit, "develop"), (preparation.DefinitionCommitHash, preparation.DefinitionBranchName));
    }

    [Fact]
    public async Task PrepareRunAsync_WhenTheSourceIsRefused_StartsNothing()
    {
        var (sut, workspaceSources, _) = BuildPreparation();
        workspaceSources.ResolveAsync(
                Arg.Any<int>(), Arg.Any<PipelineSourceDefinition>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<PipelineWorkspaceSource>(_ => throw new BadRequestException("Source repository 'aetheus-public' at bbbbbbbb is not the tree"));

        var error = await Assert.ThrowsAsync<BadRequestException>(() =>
            sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("is not the tree", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareRunAsync_WithoutASourceBlock_NeverAsksForAnotherRepository()
    {
        var (sut, workspaceSources, pipeline) = BuildPreparation();
        pipeline.YamlDefinition = Yaml[..Yaml.IndexOf("source:", StringComparison.Ordinal)]
            + Yaml[Yaml.IndexOf("stages:", StringComparison.Ordinal)..];

        var preparation = await sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(preparation);
        Assert.Equal(("develop", DefinitionCommit), (preparation.BranchName, preparation.CommitHash));
        Assert.Null(preparation.DefinitionCommitHash);
        await workspaceSources.DidNotReceiveWithAnyArgs().ResolveAsync(0, null!, null, null, TestContext.Current.CancellationToken);
    }

    [Fact]
    public void ARunWithASourceBlock_HandsItsDefinitionRevisionToTheChildrenItTriggers()
    {
        var parent = new PipelineRun
        {
            CommitHash = WorkspaceCommit,
            BranchName = "main",
            AdditionalVariablesJson =
                $$"""{"AETHEUS_DEFINITION_COMMIT":"{{DefinitionCommit}}","AETHEUS_DEFINITION_BRANCH":"develop"}"""
        };
        var childVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var error = PipelineTriggerStepCoordinator.ApplyTriggerSourceContext(
            new PipelineStepDefinition { Type = "trigger", InheritSource = true }, parent, [], childVariables);

        // The child is a pipeline of the same project: it exists at the definition's revision, not at
        // a commit of the other repository.
        Assert.Null(error);
        Assert.Equal(DefinitionCommit, childVariables["AETHEUS_SOURCE_COMMIT"]);
        Assert.Equal("develop", childVariables["AETHEUS_RUN_BRANCH"]);
    }

    [Fact]
    public void ARunWithoutASourceBlock_StillHandsDownItsOwnCommit()
    {
        var parent = new PipelineRun { CommitHash = WorkspaceCommit, BranchName = "main", AdditionalVariablesJson = "{}" };
        var childVariables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        PipelineTriggerStepCoordinator.ApplyTriggerSourceContext(
            new PipelineStepDefinition { Type = "trigger", InheritSource = true }, parent, [], childVariables);

        Assert.Equal(WorkspaceCommit, childVariables["AETHEUS_SOURCE_COMMIT"]);
        Assert.Equal("main", childVariables["AETHEUS_RUN_BRANCH"]);
        Assert.Equal(WorkspaceCommit, PipelineRunService.ResolveDefinitionCommit(parent));
    }

    private static (PipelineRunPreparationService Sut, IPipelineWorkspaceSourceResolver WorkspaceSources, Pipeline Pipeline) BuildPreparation()
    {
        var repo = Substitute.For<IPipelineRepository>();
        var pipelineGit = Substitute.For<IPipelineGitService>();
        var parameterResolver = Substitute.For<IPipelineRunParameterResolver>();
        var workspaceSources = Substitute.For<IPipelineWorkspaceSourceResolver>();
        var pipeline = new Pipeline
        {
            Id = 3,
            ProjectId = 7,
            SourceRepositoryId = 10,
            Project = new Project { Id = 7, DefaultBranch = "develop" },
            Name = "nightly-public",
            YamlDefinition = Yaml
        };
        repo.FindPipelineAsync(3, Arg.Any<CancellationToken>()).Returns(pipeline);
        repo.GetPipelineOrganizationIdAsync(3, Arg.Any<CancellationToken>()).Returns(1);
        pipelineGit.GetPipelineSourceAsync(7, pipeline.Name, Arg.Any<CancellationToken>(), "develop", 10)
            .Returns(new PipelineSourceDto
            {
                RepositoryId = 10,
                CloneUrl = "https://api.example/git/7/aetheus.git",
                Branch = "develop",
                CommitHash = DefinitionCommit
            });
        parameterResolver.ResolveCandidateTargetServerIdsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var sut = new PipelineRunPreparationService(
            repo, pipelineGit, new PipelineTemplateResolver(repo), parameterResolver,
            Substitute.For<IGitCliService>(), workspaceSources,
            new ConfigurationBuilder().Build(), NullLogger<PipelineRunPreparationService>.Instance);
        return (sut, workspaceSources, pipeline);
    }
}
