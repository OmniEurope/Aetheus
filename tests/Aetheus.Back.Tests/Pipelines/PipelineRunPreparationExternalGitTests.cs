// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R-4 (decision of 2026-11-10, option B): a project that names an external address is read only through its
/// project mirror. Without one, the launch and its dialog are refused with a message that says to create the mirror; with
/// one, the definition, the pinned commit and the workspace all come from the mirror. The real preparation, template
/// resolver and parser; the Git reads are doubles. The preparation no longer has any client of a raw address at all
/// (<see cref="IGitCliService"/> lost its branch resolution and pipeline fetch): the refusal is the only outcome.
/// </summary>
public class PipelineRunPreparationExternalGitTests
{
    private const string Url = "https://github.com/example/OmniEurope.Blazor.git";
    private const string MirrorUrl = "http://localhost:5300/git/7/omnieurope-blazor.git";
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string StoredYaml =
        "name: Package CI\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        shell: dotnet build";

    private static (PipelineRunPreparationService Sut, IPipelineGitService Git) Build(Pipeline pipeline)
    {
        var repo = Substitute.For<IPipelineRepository>();
        var pipelineGit = Substitute.For<IPipelineGitService>();
        var parameterResolver = Substitute.For<IPipelineRunParameterResolver>();
        repo.FindPipelineAsync(pipeline.Id, Arg.Any<CancellationToken>()).Returns(pipeline);
        repo.GetPipelineOrganizationIdAsync(pipeline.Id, Arg.Any<CancellationToken>()).Returns(1);
        parameterResolver.ResolveCandidateTargetServerIdsAsync(
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var sut = new PipelineRunPreparationService(
            repo, pipelineGit, new PipelineTemplateResolver(repo), parameterResolver,
            Substitute.For<IPipelineWorkspaceSourceResolver>(),
            new ConfigurationBuilder().Build(), NullLogger<PipelineRunPreparationService>.Instance);
        return (sut, pipelineGit);
    }

    private static Pipeline PipelineOf(string? repositoryUrl) => new()
    {
        Id = 3,
        ProjectId = 7,
        Project = new Project { Id = 7, RepositoryUrl = repositoryUrl, DefaultBranch = "develop" },
        Name = "Package CI",
        YamlDefinition = StoredYaml
    };

    [Fact]
    public async Task PrepareRunAsync_PublicAddressWithoutMirror_IsRefusedWithTheMirrorMessage()
    {
        var (sut, _) = Build(PipelineOf(Url));

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken));

        Assert.Equal(ExternalRepositoryMirrorRequirement.Message, refusal.Message);
        Assert.DoesNotContain(Url, refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareRunAsync_PublicAddressWithoutMirror_IsRefusedEvenWithACommitOverride()
    {
        var (sut, _) = Build(PipelineOf(Url));

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => sut.PrepareRunAsync(3, null, null, Commit, TestContext.Current.CancellationToken));

        Assert.Equal(ExternalRepositoryMirrorRequirement.Message, refusal.Message);
    }

    [Fact]
    public async Task GetRunParametersAsync_PublicAddressWithoutMirror_IsRefusedWithTheMirrorMessage()
    {
        var (sut, _) = Build(PipelineOf(Url));

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => sut.GetRunParametersAsync(3, null, TestContext.Current.CancellationToken));

        Assert.Equal(ExternalRepositoryMirrorRequirement.Message, refusal.Message);
    }

    /// <summary>Once the mirror exists the project's address is the mirror's clone URL: the commit, the definition and the
    /// workspace the agent clones are the mirror's, never the external address.</summary>
    [Fact]
    public async Task PrepareRunAsync_MirroredProject_PinsAndChecksOutTheMirror()
    {
        var pipeline = PipelineOf(MirrorUrl);
        var (sut, pipelineGit) = Build(pipeline);
        pipelineGit.GetPipelineSourceAsync(7, pipeline.Name, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(new PipelineSourceDto { RepositoryId = 4, CloneUrl = MirrorUrl, Branch = "develop", CommitHash = Commit });
        pipelineGit.ReadProjectPipelineYamlAtRevisionAsync(7, pipeline.Name, Commit, Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns((string?)null);

        var result = await sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(MirrorUrl, result.RepositoryUrl);
        Assert.Equal(Commit, result.CommitHash);
        Assert.Equal("develop", result.BranchName);
        Assert.Contains("dotnet build", result.YamlSnapshot, StringComparison.Ordinal);
    }

    /// <summary>Recette R-49: the real preparation, the real template resolver and the real parser. A YAML
    /// with an unquoted " : " is refused with a BadRequestException (HTTP 400 through the middleware, and the
    /// controller does not catch it) whose message names the line and the column, not only the scanner's
    /// sentence.</summary>
    [Fact]
    public async Task PrepareRunAsync_YamlThatIsNotYaml_IsRefusedWithTheLineAndColumn()
    {
        var pipeline = PipelineOf(MirrorUrl);
        pipeline.Name = "aetheus-deploy-prod";
        pipeline.YamlDefinition = "name: aetheus-deploy-prod\nstages: []\n";
        var (sut, pipelineGit) = Build(pipeline);
        pipelineGit.GetPipelineSourceAsync(7, pipeline.Name, Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(new PipelineSourceDto { RepositoryId = 4, CloneUrl = MirrorUrl, Branch = "develop", CommitHash = Commit });
        pipelineGit.ReadProjectPipelineYamlAtRevisionAsync(7, pipeline.Name, Commit, Arg.Any<CancellationToken>(), Arg.Any<int?>())
            .Returns("name: aetheus-deploy-prod\ntrigger: manual\nparameters:\n  - name: scope\n    description: Diagnostic : liste\nstages: []\n");

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("line 5, column", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("found invalid mapping", refusal.Message, StringComparison.Ordinal);
    }
}
