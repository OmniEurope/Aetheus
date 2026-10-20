// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Git;

/// <summary>
/// The repositories page joins the git hub group of each repository it shows, on a real hub connection
/// to an in-memory hub: a live change reloads the rows, a repository no longer shown is left and a new one
/// joined; after a reconnect every shown repository is joined again.
/// </summary>
public sealed class GitRepositoriesRepositoryGroupsTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoriesRepositoryGroupsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        _handler.SetPaginatedJsonResponse("api/projects", new[] { new ProjectDto { Id = 1, Name = "MyProject" } });
        ServeRepositories(1, 2);
    }

    private InMemoryHubServer Git => this.Hub("git");

    private void ServeRepositories(params int[] ids) =>
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/git", ids.Select(id => new GitLightRepoDto
        {
            Id = id,
            Name = $"repo{id}",
            CloneUrl = $"https://example.test/repo{id}.git"
        }));

    private async Task<IRenderedComponent<GitRepositories>> RenderConnectedAsync()
    {
        var cut = Render<GitRepositories>();
        await Git.WhenInvokedAsync("JoinRepositoryGroup", 2);
        Assert.Equal([1, 2], Git.IntArguments("JoinRepositoryGroup").Order());
        return cut;
    }

    [Theory]
    [InlineData("RepositoryChanged")]
    [InlineData("CommitsChanged")]
    [InlineData("BranchesChanged")]
    public async Task ALiveChange_LeavesTheRepositoryNoLongerShown_AndJoinsTheNewOne(string message)
    {
        var cut = await RenderConnectedAsync();
        ServeRepositories(2, 3);

        await Git.PushAsync(message, 3);

        await Git.WhenAsync(
            () => Git.Count("LeaveRepositoryGroup") >= 1 && Git.Count("JoinRepositoryGroup") >= 3,
            "the left repository left and the new one joined");
        Assert.Equal([1], Git.IntArguments("LeaveRepositoryGroup"));
        Assert.Equal([1, 2, 3], Git.IntArguments("JoinRepositoryGroup").Order());
        await cut.WhenRenderedAsync(() => Assert.Contains("repo3", cut.Markup, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AfterAReconnect_EveryShownRepositoryIsJoinedAgain()
    {
        await RenderConnectedAsync();

        await Git.DropAsync();

        await Git.WhenInvokedAsync("JoinRepositoryGroup", 4);
        Assert.Equal([1, 1, 2, 2], Git.IntArguments("JoinRepositoryGroup").Order());
        Assert.Empty(Git.IntArguments("LeaveRepositoryGroup"));
    }

    [Fact]
    public async Task WithoutAConnectedHub_TheRowsShowAndNoGroupIsJoined()
    {
        Git.RefuseConnections = true;

        var cut = Render<GitRepositories>();

        await cut.WhenRenderedAsync(() => Assert.Contains("repo2", cut.Markup, StringComparison.Ordinal));
        await Git.WhenAsync(() => Git.ConnectAttempts >= 1, "the refused connection attempt");
        Assert.Equal(1, Git.ConnectAttempts);
        Assert.Empty(Git.Invocations);
    }
}
