// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Git;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Git;

public class CommitDetailTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public CommitDetailTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static GitCommitDto Sample(int id = 3, string? repoUrl = "https://localhost:5302/git/1/demo.git") => new()
    {
        Id = id,
        ProjectId = 1,
        ProjectName = "Demo",
        Sha = "abcdef1234567890",
        RepositoryUrl = repoUrl
    };

    private void SetRepositories(params GitLightRepoDto[] repositories) =>
        _handler.SetJsonResponse("api/git/repos", new PaginatedResult<GitLightRepoDto>
        {
            Items = repositories.ToList(),
            TotalCount = repositories.Length,
            Page = 1,
            PageSize = 100
        });

    [Fact]
    public void ExistingCommit_RedirectsToCanonicalRepositoryCommit()
    {
        _handler.SetJsonResponse("api/gitgraph/commits/3", Sample());
        SetRepositories(new GitLightRepoDto
        {
            Id = 7,
            ProjectId = 1,
            Name = "Demo",
            CloneUrl = "https://localhost:5302/git/1/demo.git"
        });

        Render<CommitDetail>(parameters => parameters.Add(component => component.CommitId, 3));

        Assert.EndsWith("/git-repositories/7/commits/abcdef1234567890",
            Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void DifferentLauncherHost_StillMatchesRepositoryByPath()
    {
        _handler.SetJsonResponse("api/gitgraph/commits/4", Sample(4,
            "https://host.docker.internal:5303/git/1/demo.git"));
        SetRepositories(
            new GitLightRepoDto { Id = 7, ProjectId = 1, Name = "Demo", CloneUrl = "https://localhost:5302/git/1/demo.git" },
            new GitLightRepoDto { Id = 8, ProjectId = 1, Name = "Other", CloneUrl = "https://localhost:5302/git/1/other.git" });

        Render<CommitDetail>(parameters => parameters.Add(component => component.CommitId, 4));

        Assert.EndsWith("/git-repositories/7/commits/abcdef1234567890",
            Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Fact]
    public void CommitFromAnotherRepository_ExplainsAndLinksToTheProvider_InsteadOfTheOnlyInternalRepository()
    {
        // The project's only internal repository does not hold a commit its runs took from an
        // external repository; the page says so and links to the provider, it never opens a 404.
        _handler.SetJsonResponse("api/gitgraph/commits/5", Sample(5, "https://github.com/acme/shop.git"));
        SetRepositories(new GitLightRepoDto { Id = 2, ProjectId = 1, Name = "Internal", CloneUrl = "https://localhost:5302/git/1/internal.git" });

        var cut = Render<CommitDetail>(parameters => parameters.Add(component => component.CommitId, 5));

        cut.WaitForAssertion(() => Assert.Contains("CommitOutsideInternalRepositories", cut.Markup, StringComparison.Ordinal));
        Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri);
        Assert.Equal("https://github.com/acme/shop/commit/abcdef1234567890", cut.Find("a.commit-external-link").GetAttribute("href"));
    }

    [Fact]
    public void OnlyRepository_IsStillChosen_WhenTheCommitNamesNoRepository()
    {
        _handler.SetJsonResponse("api/gitgraph/commits/6", Sample(6, repoUrl: null));
        SetRepositories(new GitLightRepoDto { Id = 2, ProjectId = 1, Name = "Internal", CloneUrl = "https://localhost:5302/git/1/internal.git" });

        Render<CommitDetail>(parameters => parameters.Add(component => component.CommitId, 6));

        Assert.EndsWith("/git-repositories/2/commits/abcdef1234567890", Services.GetRequiredService<NavigationManager>().Uri);
    }

    [Theory]
    [InlineData("https://github.com/acme/shop.git", "https://github.com/acme/shop/commit/abc123")]
    [InlineData("https://gitlab.com/group/sub/app", "https://gitlab.com/group/sub/app/-/commit/abc123")]
    [InlineData("https://bitbucket.org/team/app.git", "https://bitbucket.org/team/app/commits/abc123")]
    [InlineData("git@github.com:acme/shop.git", null)]
    [InlineData(null, null)]
    public void ExternalCommitLink_FollowsTheProviderConvention(string? repositoryUrl, string? expected) =>
        Assert.Equal(expected, ExternalCommitLink.For(repositoryUrl, "abc123"));

    [Fact]
    public void MissingCommit_DoesNotRedirect()
    {
        _handler.SetResponse("api/gitgraph/commits/9", HttpStatusCode.NotFound);

        var cut = Render<CommitDetail>(parameters => parameters.Add(component => component.CommitId, 9));

        cut.WaitForAssertion(() => Assert.Contains("NotFound", cut.Markup));
        Assert.Equal("http://localhost/", Services.GetRequiredService<NavigationManager>().Uri);
    }
}
