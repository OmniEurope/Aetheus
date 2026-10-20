// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The commit and branch links of the pipeline page's runs grid.</summary>
public sealed class PipelineRunRepositoryResolverTests
{
    private const string Sha = "0123456789abcdef0123456789abcdef01234567";
    private const string InternalCloneUrl = "https://host.docker.internal:5301/git/13/aetheus-self.git";

    [Fact]
    public void ARecordedCommit_LeadsToItsGitGraphPage()
    {
        var run = new PipelineRunDto
        {
            CommitHash = Sha,
            BranchName = "main",
            RepositoryUrl = InternalCloneUrl,
            RepositoryId = 42,
            Commits = [new CommitLinkDto { Id = 33, Sha = Sha }],
            Branches = [new BranchLinkDto { Id = 7, Name = "main" }]
        };

        Assert.Equal("/git/commits/33", PipelineRunRepositoryResolver.RunCommitHref(run));
        Assert.Equal("/git/branches/7", PipelineRunRepositoryResolver.RunBranchHref(run));
    }

    [Fact]
    public void AResolvedInternalRepository_LeadsToItsAetheusPages_NotToTheCloneUrl()
    {
        var run = new PipelineRunDto
        {
            CommitHash = Sha,
            BranchName = "feature/x",
            RepositoryUrl = InternalCloneUrl,
            RepositoryId = 42
        };

        Assert.Equal($"/git-repositories/42/commits/{Sha}", PipelineRunRepositoryResolver.RunCommitHref(run));
        Assert.Equal("/git-repositories/42?tab=branches&branch=feature%2Fx", PipelineRunRepositoryResolver.RunBranchHref(run));
    }

    [Fact]
    public void AnUnresolvedRepository_StaysText()
    {
        var run = new PipelineRunDto { CommitHash = Sha, BranchName = "main", RepositoryUrl = InternalCloneUrl };

        Assert.Null(PipelineRunRepositoryResolver.RunCommitHref(run));
        Assert.Null(PipelineRunRepositoryResolver.RunBranchHref(run));
    }

    [Theory]
    [InlineData("https://host.docker.internal:5301/git/13/aetheus-self.git", true)]
    [InlineData("https://localhost:5301/git/13/aetheus-self", true)]
    [InlineData("https://github.com/acme/demo.git", false)]
    [InlineData("https://git.example/git/not-a-number/demo.git", false)]
    [InlineData("git@github.com:acme/demo.git", false)]
    [InlineData(null, false)]
    public void IsInternalClone_RecognisesOnlyTheSmartHttpMirrorShape(string? url, bool expected)
        => Assert.Equal(expected, GitRepositoryUrl.IsInternalClone(url));

    private static readonly List<GitLightRepoDto> Repositories =
    [
        new() { Id = 4, ProjectId = 13, Slug = "other", CloneUrl = "https://localhost:5301/git/13/other.git" },
        new() { Id = 6, ProjectId = 13, Slug = "aetheus-self", CloneUrl = "https://localhost:5301/git/13/aetheus-self.git" },
        new() { Id = 8, ProjectId = 13, Slug = "mirror", CloneUrl = "https://github.com/acme/demo.git" }
    ];

    [Theory]
    [InlineData("https://host.docker.internal:5301/git/13/aetheus-self.git", 6)]
    [InlineData("https://host.docker.internal:5301/git/13/aetheus-self", 6)]
    [InlineData("https://host.docker.internal:5301/git/14/aetheus-self.git", null)]
    [InlineData("https://github.com/acme/demo", 8)]
    [InlineData("https://github.com/acme/unknown.git", null)]
    [InlineData(null, null)]
    public void ResolveRepositoryId_MatchesAnInternalUrlBySlug_AndNeverGuesses(string? url, int? expected)
        => Assert.Equal(expected, GitRepositoryUrl.ResolveRepositoryId(url, Repositories));

    [Fact]
    public void ResolveRepositoryId_WithoutUrl_TakesTheOnlyRepository()
        => Assert.Equal(6, GitRepositoryUrl.ResolveRepositoryId(null, [Repositories[1]]));

    [Fact]
    public void ThePortfolioCommit_LeadsToItsPage_OnlyWhenTheRepositoryIsKnown()
    {
        var row = new AnalysisPortfolioRowDto { ProjectId = 13, CommitHash = Sha };

        Assert.Null(Aetheus.Front.Components.Analysis.AnalysisPortfolio.CommitHref(row));
        Assert.Equal($"/git-repositories/6/commits/{Sha}",
            Aetheus.Front.Components.Analysis.AnalysisPortfolio.CommitHref(row with { RepositoryId = 6 }));
    }
}
