// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-11: a run of a pipeline imported from an external mirror executes its stored, reviewed definition, never
/// the mirror's current file, and the import policy is applied again at every preparation. Real preparation, real
/// template resolver and parser; the Git reads are doubles.</summary>
public sealed class PipelineRunPreparationExternalImportTests
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string StoredYaml = "name: oe1-ci\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        shell: dotnet build";
    private const string MirrorYaml = "name: oe1-ci\nvaults: [prod]\nstages:\n  - name: Build\n    steps:\n      - name: Leak\n        shell: env";

    private const string MovedHead = "89abcdef0123456789abcdef0123456789abcdef";

    private static (PipelineRunPreparationService Sut, IPipelineGitService Git) Build(Pipeline pipeline, string head = Commit)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var pipelineGit = Substitute.For<IPipelineGitService>();
        var parameterResolver = Substitute.For<IPipelineRunParameterResolver>();
        repo.FindPipelineAsync(pipeline.Id, Arg.Any<CancellationToken>()).Returns(pipeline);
        repo.GetPipelineOrganizationIdAsync(pipeline.Id, Arg.Any<CancellationToken>()).Returns(1);
        pipelineGit.GetPipelineSourceAsync(7, pipeline.Name, Arg.Any<CancellationToken>(), Arg.Any<string?>(), 3)
            .Returns(new PipelineSourceDto { RepositoryId = 3, CloneUrl = "http://localhost/git/7/oe1.git", Branch = "main", CommitHash = head });
        pipelineGit.ReadProjectPipelineYamlAtRevisionAsync(7, pipeline.Name, Commit, Arg.Any<CancellationToken>(), 3).Returns(MirrorYaml);
        pipelineGit.ReadProjectPipelineYamlAsync(7, pipeline.Name, Arg.Any<CancellationToken>(), Arg.Any<string?>(), 3).Returns(MirrorYaml);
        parameterResolver.ResolveCandidateTargetServerIdsAsync(
                Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var sut = new PipelineRunPreparationService(
            repo, pipelineGit, new PipelineTemplateResolver(repo), parameterResolver, Substitute.For<IPipelineWorkspaceSourceResolver>(), new ConfigurationBuilder().Build(),
            NullLogger<PipelineRunPreparationService>.Instance);
        return (sut, pipelineGit);
    }

    private static Pipeline Imported(string yaml) => new()
    {
        Id = 5,
        ProjectId = 7,
        Project = new Project { Id = 7, DefaultBranch = "main" },
        Name = "oe1-ci",
        SourceRepositoryId = 3,
        SourceBranch = "main",
        YamlDefinition = yaml,
        ExternalImportPath = ".pipeline/oe1-ci.yaml",
        ExternalImportCommit = Commit,
        ExternalImportSha256 = PipelineRepositoryDefinitionGuard.Sha256(yaml)
    };

    [Fact]
    public async Task PrepareRun_ImportedPipeline_RunsTheStoredDefinitionNotTheMirrorFile()
    {
        var (sut, git) = Build(Imported(StoredYaml));

        var result = await sut.PrepareRunAsync(5, null, null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("dotnet build", result.YamlSnapshot, StringComparison.Ordinal);
        Assert.DoesNotContain("vaults", result.YamlSnapshot, StringComparison.Ordinal);
        Assert.Equal(Commit, result.CommitHash);
        await git.DidNotReceiveWithAnyArgs().ReadProjectPipelineYamlAtRevisionAsync(default, default!, default!, TestContext.Current.CancellationToken, default);
        await git.DidNotReceiveWithAnyArgs().ReadProjectPipelineYamlAsync(default, default!, TestContext.Current.CancellationToken, default, default);
    }

    [Fact]
    public async Task PrepareRun_ImportedPipelineWhoseDefinitionAsksForAVault_IsRefused()
    {
        var (sut, _) = Build(Imported(MirrorYaml));

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => sut.PrepareRunAsync(5, null, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("vaults", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetRunParameters_ImportedPipeline_ReadsNoMirrorFile()
    {
        var (sut, git) = Build(Imported(StoredYaml));

        await sut.GetRunParametersAsync(5, null, TestContext.Current.CancellationToken);

        await git.DidNotReceiveWithAnyArgs().ReadProjectPipelineYamlAsync(default, default!, TestContext.Current.CancellationToken, default, default);
    }

    [Fact]
    public async Task PrepareRun_ImportedPipeline_RunsTheImportedCommitWhenTheMirrorMoved()
    {
        // Security review, finding 2: the workspace is the code of the imported commit, not the mirror's new head.
        var (sut, _) = Build(Imported(StoredYaml), head: MovedHead);

        var result = await sut.PrepareRunAsync(5, "feature/other", null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(Commit, result.CommitHash);
        Assert.Equal("main", result.BranchName);
        Assert.Equal("http://localhost/git/7/oe1.git", result.RepositoryUrl);
    }

    [Fact]
    public async Task PrepareRun_ImportedPipelineAskedForAnotherCommit_IsRefused()
    {
        var (sut, _) = Build(Imported(StoredYaml), head: MovedHead);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => sut.PrepareRunAsync(5, null, null, MovedHead, TestContext.Current.CancellationToken));

        Assert.Contains(Commit, refusal.Message, StringComparison.Ordinal);
        var same = await sut.PrepareRunAsync(5, null, null, Commit, TestContext.Current.CancellationToken);
        Assert.Equal(Commit, same!.CommitHash);
    }

    [Fact]
    public async Task PrepareRun_ImportedPipelineWhoseMirrorIsGone_IsRefused()
    {
        var pipeline = Imported(StoredYaml);
        pipeline.SourceRepositoryId = 9; // the mirror row was removed: no source resolves for it
        var (sut, _) = Build(pipeline);

        await Assert.ThrowsAsync<BadRequestException>(
            () => sut.PrepareRunAsync(5, null, null, null, TestContext.Current.CancellationToken));
    }
}
