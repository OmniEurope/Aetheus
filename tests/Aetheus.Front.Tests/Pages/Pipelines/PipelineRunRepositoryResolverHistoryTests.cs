// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// FRONT-011: a run's source tile opens the repository the run came from, not the one its pipeline
/// definition points at today.
/// </summary>
public sealed class PipelineRunRepositoryResolverHistoryTests : BunitContext
{
    private const string FirstCloneUrl = "https://localhost:5301/git/13/first.git";
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineRunRepositoryResolverHistoryTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/git/repos", new List<GitLightRepoDto>
        {
            new() { Id = 6, ProjectId = 13, Slug = "first", CloneUrl = FirstCloneUrl },
            new() { Id = 8, ProjectId = 13, Slug = "second", CloneUrl = "https://localhost:5301/git/13/second.git" }
        });
        // The definition has since moved to the second repository.
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/3/source", new PipelineSourceDto { RepositoryId = 8, Branch = "main" });
    }

    private ApiClient Api => Services.GetRequiredService<ApiClient>();

    [Fact]
    public async Task ARunMadeBeforeTheDefinitionMoved_OpensTheRepositoryItRanFrom()
    {
        var run = new PipelineRunDto { Id = 1, PipelineId = 3, ProjectId = 13, RepositoryUrl = FirstCloneUrl, CommitHash = "abc" };

        Assert.Equal(6, await PipelineRunRepositoryResolver.ResolveAsync(Api, run, Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARunWhoseRecordedRepositoryIsUnknown_IsNotAttributedToTheCurrentDefinition()
    {
        var run = new PipelineRunDto { Id = 1, PipelineId = 3, ProjectId = 13, RepositoryUrl = "https://localhost:5301/git/13/deleted.git" };

        Assert.Null(await PipelineRunRepositoryResolver.ResolveAsync(Api, run, Xunit.TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ARunThatRecordedNoRepository_FallsBackToTheDefinitionSource()
    {
        var run = new PipelineRunDto { Id = 1, PipelineId = 3, ProjectId = 13 };

        Assert.Equal(8, await PipelineRunRepositoryResolver.ResolveAsync(Api, run, Xunit.TestContext.Current.CancellationToken));
    }
}
