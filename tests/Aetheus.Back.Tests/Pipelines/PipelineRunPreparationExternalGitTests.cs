// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

public class PipelineRunPreparationExternalGitTests
{
    [Theory]
    [InlineData(null, "develop", false)]
    [InlineData("feature/package", "feature/package", false)]
    [InlineData("develop", "develop", true)]
    public async Task PrepareRunAsync_PublicProjectUrl_PinsTheSelectedCommitAndKeepsTheDbDefinition(
        string? configuredBranch, string resolvedBranch, bool externalDefinition)
    {
        const string url = "https://github.com/example/OmniEurope.Blazor.git";
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var repo = Substitute.For<IPipelineRepository>();
        var pipelineGit = Substitute.For<IPipelineGitService>();
        var gitCli = Substitute.For<IGitCliService>();
        var parameterResolver = Substitute.For<IPipelineRunParameterResolver>();
        var pipeline = new Pipeline
        {
            Id = 3,
            ProjectId = 7,
            Project = new Project { Id = 7, RepositoryUrl = url, DefaultBranch = configuredBranch },
            Name = "Package CI",
            YamlDefinition = "name: Package CI\nstages:\n  - name: Build\n    steps:\n      - name: Compile\n        shell: dotnet build"
        };
        repo.FindPipelineAsync(3, Arg.Any<CancellationToken>()).Returns(pipeline);
        repo.GetPipelineOrganizationIdAsync(3, Arg.Any<CancellationToken>()).Returns(1);
        gitCli.ResolveBranchCommitAsync(url, configuredBranch, Arg.Any<CancellationToken>())
            .Returns(new GitRemoteBranch(resolvedBranch, commit));
        gitCli.ReadPipelineYamlAsync(url, commit, pipeline.Name, Arg.Any<CancellationToken>())
            .Returns(externalDefinition ? pipeline.YamlDefinition.Replace("dotnet build", "dotnet test", StringComparison.Ordinal) : null);
        parameterResolver.ResolveCandidateTargetServerIdsAsync(
            Arg.Any<PipelineYamlDefinition>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns([]);
        var sut = new PipelineRunPreparationService(
            repo, pipelineGit, new PipelineTemplateResolver(repo), parameterResolver, gitCli,
            Substitute.For<IPipelineWorkspaceSourceResolver>(),
            new ConfigurationBuilder().Build(), NullLogger<PipelineRunPreparationService>.Instance);

        var result = await sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(url, result.RepositoryUrl);
        Assert.Equal(resolvedBranch, result.BranchName);
        Assert.Equal(commit, result.CommitHash);
        Assert.Contains(externalDefinition ? "dotnet test" : "dotnet build", result.YamlSnapshot, StringComparison.Ordinal);
        await gitCli.Received(1).ReadPipelineYamlAsync(url, commit, pipeline.Name, Arg.Any<CancellationToken>());
        await gitCli.Received(1).ResolveBranchCommitAsync(url, configuredBranch, Arg.Any<CancellationToken>());
    }

    /// <summary>Recette R-49: the real preparation, the real template resolver and the real parser. A YAML
    /// with an unquoted " : " is refused with a BadRequestException (HTTP 400 through the middleware, and the
    /// controller does not catch it) whose message names the line and the column, not only the scanner's
    /// sentence.</summary>
    [Fact]
    public async Task PrepareRunAsync_YamlThatIsNotYaml_IsRefusedWithTheLineAndColumn()
    {
        const string url = "https://github.com/example/OmniEurope.Blazor.git";
        const string commit = "0123456789abcdef0123456789abcdef01234567";
        var repo = Substitute.For<IPipelineRepository>();
        var gitCli = Substitute.For<IGitCliService>();
        var pipeline = new Pipeline
        {
            Id = 3,
            ProjectId = 7,
            Project = new Project { Id = 7, RepositoryUrl = url, DefaultBranch = "develop" },
            Name = "aetheus-deploy-prod",
            YamlDefinition = "name: aetheus-deploy-prod\nstages: []\n"
        };
        repo.FindPipelineAsync(3, Arg.Any<CancellationToken>()).Returns(pipeline);
        gitCli.ResolveBranchCommitAsync(url, "develop", Arg.Any<CancellationToken>())
            .Returns(new GitRemoteBranch("develop", commit));
        gitCli.ReadPipelineYamlAsync(url, commit, pipeline.Name, Arg.Any<CancellationToken>())
            .Returns("name: aetheus-deploy-prod\ntrigger: manual\nparameters:\n  - name: scope\n    description: Diagnostic : liste\nstages: []\n");
        var sut = new PipelineRunPreparationService(
            repo, Substitute.For<IPipelineGitService>(), new PipelineTemplateResolver(repo),
            Substitute.For<IPipelineRunParameterResolver>(), gitCli, Substitute.For<IPipelineWorkspaceSourceResolver>(),
            new ConfigurationBuilder().Build(), NullLogger<PipelineRunPreparationService>.Instance);

        var refusal = await Assert.ThrowsAsync<BadRequestException>(
            () => sut.PrepareRunAsync(3, null, null, null, TestContext.Current.CancellationToken));

        Assert.Contains("line 5, column", refusal.Message, StringComparison.Ordinal);
        Assert.Contains("found invalid mapping", refusal.Message, StringComparison.Ordinal);
    }
}
