// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// FRONT-002 / FRONT-003: what the repository page shows belongs to the repository, ref and file on screen,
/// never to an answer that arrived after the user moved on.
/// </summary>
public class GitRepositoryDetailLateResponseTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public GitRepositoryDetailLateResponseTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private void SetupRepository(int id, string name, IEnumerable<GitLightTreeEntryDto>? tree = null)
    {
        _handler.SetJsonResponse($"api/git/repos/{id}", new GitLightRepoDto
        {
            Id = id,
            ProjectId = 1,
            ProjectName = "MyProject",
            Name = name,
            DefaultBranch = "main",
            CloneUrl = $"http://localhost/git/{name}.git",
            CreatedAt = DateTime.UtcNow
        });
        _handler.SetPaginatedJsonResponse($"api/git/repos/{id}/branches", new List<GitLightBranchDto>
        {
            new() { Name = "main", IsDefault = true },
            new() { Name = "develop" }
        });
        _handler.SetPaginatedJsonResponse($"api/git/repos/{id}/tags", new List<GitLightTagDto>());
        _handler.SetJsonResponse($"api/git/repos/{id}/commits", new PaginatedResult<GitLightCommitDto>());
        _handler.SetPaginatedJsonResponse($"api/git/repos/{id}/tree", tree ?? []);
        _handler.SetJsonResponse($"api/git/repos/{id}/pull-requests", new PaginatedResult<InternalPullRequestDto>());
        _handler.SetPaginatedJsonResponse($"api/git/repos/{id}/branch-protection", new List<BranchProtectionRuleDto>());
        _handler.SetJsonResponse($"api/git/repos/{id}/graph-data", new List<GitLightCommitDto>());
        _handler.SetJsonResponse($"api/git/repos/{id}/filter-values", new GitRepositoryDetailFilterValuesDto());
    }

    [Fact]
    public async Task Front002_BlameOfTheFirstFile_IsNotShownUnderTheNextFileAfterABranchChange()
    {
        SetupRepository(1, "my-repo",
        [
            new() { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob, Size = 10 },
            new() { Name = "Other.cs", Path = "Other.cs", Type = GitTreeEntryType.Blob, Size = 10 }
        ]);
        _handler.SetJsonResponse("api/git/repos/1/blob", new GitLightBlobDto
        {
            Path = "README.md",
            Content = "# My Repo",
            Size = 10,
            IsBinary = false
        });
        _handler.SetJsonResponse("api/git/repos/1/blame", new List<GitLightBlameLine>
        {
            new() { LineNumber = 1, Sha = "aaaa111122223333", ShortSha = "aaaa111", AuthorName = "Blamed author", Line = "# My Repo" }
        });
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo("/git-repositories/1?tab=files&path=README.md");
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains("README.md", cut.Markup));

        cut.FindAll("button").First(button => button.GetAttribute("title") == "Blame").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".blame-view")));

        // Another branch, then another file: the annotations of README.md on main must not follow.
        var branchPicker = cut.FindComponents<OmniDropDown<string>>()
            .Single(dropdown => dropdown.Instance.Options.Any(option => option.Text == "develop"));
        await cut.InvokeAsync(() => branchPicker.Instance.ValueChanged.InvokeAsync("develop"));
        _handler.SetJsonResponse("api/git/repos/1/blob", new GitLightBlobDto
        {
            Path = "Other.cs",
            Content = "class Other {}",
            Size = 14,
            IsBinary = false
        });
        cut.WaitForAssertion(() => Assert.Contains(cut.FindAll("a.omni-link"), link => link.TextContent == "Other.cs"));
        cut.FindAll("a.omni-link").First(link => link.TextContent == "Other.cs").Click();

        cut.WaitForAssertion(() => Assert.Contains("Other.cs", cut.Markup));
        Assert.Empty(cut.FindAll(".blame-view"));
        Assert.DoesNotContain("Blamed author", cut.Markup);
    }

    [Fact]
    public void Front003_ALateTreeAnswerOfThePreviousRepository_DoesNotReplaceTheFilesOfTheCurrentOne()
    {
        SetupRepository(1, "first-repo");
        SetupRepository(2, "second-repo", [new() { Name = "second-file.txt", Path = "second-file.txt", Type = GitTreeEntryType.Blob }]);
        var firstTree = new TaskCompletionSource<PaginatedResult<GitLightTreeEntryDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos/1/tree", _ => firstTree.Task);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/git-repositories/1?tab=files");
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("api/git/repos/1/tree", StringComparison.Ordinal)));

        cut.Render(p => p.Add(x => x.Id, 2));
        // The second repository's tree is answered first; the first repository's answer arrives after it.
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("api/git/repos/2/tree", StringComparison.Ordinal)));
        firstTree.SetResult(new PaginatedResult<GitLightTreeEntryDto>
        {
            Items = [new() { Name = "first-file.txt", Path = "first-file.txt", Type = GitTreeEntryType.Blob }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });

        // Loaded-machine ceiling: since the files grid follows the repository, a failure left here was only
        // a wait whose first check never ran (bUnit "Check count: 0") in a run several times slower.
        cut.WaitForAssertion(() => Assert.Contains("second-file.txt", cut.Markup));
        Assert.DoesNotContain("first-file.txt", cut.Markup);
    }

    [Fact]
    public void ATreeRequestOfThePreviousRepositoryThatNeverAnswers_DoesNotHoldBackTheFilesOfTheCurrentOne()
    {
        SetupRepository(1, "first-repo");
        SetupRepository(2, "second-repo", [new() { Name = "second-file.txt", Path = "second-file.txt", Type = GitTreeEntryType.Blob }]);
        var never = new TaskCompletionSource<PaginatedResult<GitLightTreeEntryDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos/1/tree", _ => never.Task);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/git-repositories/1?tab=files");
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("api/git/repos/1/tree", StringComparison.Ordinal)));

        cut.Render(p => p.Add(x => x.Id, 2));

        // The file grid of the first repository still waits for its own page of rows; the second
        // repository's rows must not wait behind it.
        cut.WaitForAssertion(() => Assert.Contains("second-file.txt", cut.Markup));
    }

    [Fact]
    public void ALateLoadOfThePreviousRepository_DoesNotOpenASecondLiveConnection()
    {
        // The superseded load resumes once its tree answers; it must stop there instead of connecting
        // the live channel again over the one the current repository already opened (that first
        // subscription would be overwritten and never disposed).
        BunitTestHelper.BlockingHubConnectionFactory? hubs = null;
        Services.AddSingleton<HubConnectionFactory>(sp => hubs = new BunitTestHelper.BlockingHubConnectionFactory(
            sp.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>(),
            sp.GetRequiredService<AuthStateProvider>()));
        SetupRepository(1, "first-repo");
        SetupRepository(2, "second-repo", [new() { Name = "second-file.txt", Path = "second-file.txt", Type = GitTreeEntryType.Blob }]);
        var firstTree = new TaskCompletionSource<PaginatedResult<GitLightTreeEntryDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos/1/tree", _ => firstTree.Task);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/git-repositories/1?tab=files");
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("api/git/repos/1/tree", StringComparison.Ordinal)));
        cut.Render(p => p.Add(x => x.Id, 2));
        // The current repository's connection never answers, so its own load never ends. The superseded
        // load ends with a render of the page itself (its title is re-rendered only then), or it connects.
        cut.WaitForAssertion(() => Assert.Equal(1, hubs?.Created));
        var title = cut.FindComponent<Microsoft.AspNetCore.Components.Web.PageTitle>();
        var pageRendersBefore = title.RenderCount;

        firstTree.SetResult(new PaginatedResult<GitLightTreeEntryDto>());

        cut.WaitForAssertion(() => Assert.True(title.RenderCount > pageRendersBefore || hubs!.Created > 1));
        Assert.Equal(1, hubs!.Created);
    }

    [Fact]
    public void Front003_ALateGraphAnswerOfThePreviousRepository_IsDropped()
    {
        SetupRepository(1, "first-repo");
        // The README of the second repository is the sync point: the first load reads it once after the
        // graph answer arrives, so a second read proves the late answer has been processed.
        SetupRepository(2, "second-repo", [new() { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob }]);
        _handler.SetJsonResponse(HttpMethod.Get, "api/git/repos/2/blob", new GitLightBlobDto { Path = "README.md", Content = "# Second" });
        int ReadmeReads() => _handler.Requests.Count(request => request.Url.Contains("api/git/repos/2/blob", StringComparison.Ordinal));
        var firstGraph = new TaskCompletionSource<List<GitLightCommitDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/git/repos/1/graph-data", _ => firstGraph.Task);
        Services.GetRequiredService<NavigationManager>().NavigateTo("/git-repositories/1?tab=graph");
        var cut = Render<GitRepositoryDetail>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request => request.Url.Contains("api/git/repos/1/graph-data", StringComparison.Ordinal)));

        cut.Render(p => p.Add(x => x.Id, 2));
        cut.WaitForAssertion(() => Assert.Equal(1, ReadmeReads()));
        firstGraph.SetResult([new GitLightCommitDto
        {
            Sha = "f1f1f1f1f1f1", ShortSha = "f1f1f1f", Message = "Commit of the first repository",
            AuthorName = "Dev", AuthorEmail = "dev@test.com", AuthorDate = DateTime.UtcNow
        }]);

        // Loaded-machine ceiling: the late answer is processed off the renderer; a failure left here was a
        // wait whose first check never ran (bUnit "Check count: 0").
        cut.WaitForAssertion(() => Assert.Equal(2, ReadmeReads()));
        cut.Render();
        Assert.Contains("second-repo", cut.Markup);
        Assert.DoesNotContain("Commit of the first repository", cut.Markup);
    }

    [Fact]
    public async Task Front003_PagedList_ClearRetiresTheRequestInFlight()
    {
        var list = new GitPagedList<string>(static _ => true);
        var late = new TaskCompletionSource<PaginatedResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = list.LoadAsync(() => late.Task);

        list.Clear();
        await list.LoadAsync(() => Task.FromResult(new PaginatedResult<string> { Items = ["current"], TotalCount = 1 }));
        late.SetResult(new PaginatedResult<string> { Items = ["previous repository"], TotalCount = 1 });
        await pending;

        Assert.Equal(["current"], list.Items);
        Assert.False(list.Loading);
    }

    [Fact]
    public async Task Front003_PagedList_ALateFailureOfAnOlderRequest_DoesNotRaiseTheErrorState()
    {
        var list = new GitPagedList<string>(static _ => true);
        var late = new TaskCompletionSource<PaginatedResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = list.LoadAsync(() => late.Task);

        await list.LoadAsync(() => Task.FromResult(new PaginatedResult<string> { Items = ["current"], TotalCount = 1 }));
        late.SetException(new HttpRequestException("late"));
        await pending;

        Assert.Equal(["current"], list.Items);
        Assert.False(list.Error);
    }
}
