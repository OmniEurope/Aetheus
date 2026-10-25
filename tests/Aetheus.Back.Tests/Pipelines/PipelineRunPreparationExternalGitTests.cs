// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
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
}
