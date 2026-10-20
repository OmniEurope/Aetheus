// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// A release created while the list is open, pushed by the releases hub: added at the top of the
/// whole list (or replacing the row a status event already put there), and on a server's list read
/// again from the server, which alone knows that scope.
/// </summary>
public sealed class ReleasesListLiveTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ReleasesListLiveTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 7, Name = "Scoped", Status = ProjectStatus.Active }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 1, ProjectId = 7, ProjectName = "Scoped", Version = "1.0.0", Status = ReleaseStatus.Published }],
            TotalCount = 1
        });
    }

    private InMemoryHubServer Releases => this.Hub("releases");

    private int Reads(string path) =>
        _handler.Requests.ToList().Count(request => request.Method == "GET" && request.Url.Contains(path, StringComparison.Ordinal));

    private async Task<IRenderedComponent<ReleasesList>> RenderJoinedAsync(Action<ComponentParameterCollectionBuilder<ReleasesList>> parameters, string firstVersion)
    {
        var cut = Render(parameters);
        await Releases.WhenInvokedAsync("JoinAllReleases");
        Assert.Equal(1, Releases.Count("JoinAllReleases"));
        await cut.WhenRenderedAsync(() => Assert.Contains(firstVersion, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task ANewRelease_IsAddedAtTheTop_ThenReplacedByItsNewerState()
    {
        var cut = await RenderJoinedAsync(_ => { }, "1.0.0");

        await Releases.PushAsync("ReleaseCreated", new ReleaseDto { Id = 9, ProjectId = 7, ProjectName = "Scoped", Version = "2.0.0-rc", Status = ReleaseStatus.Detected });
        await cut.WhenRenderedAsync(() => Assert.Contains("2.0.0-rc", cut.Markup, StringComparison.Ordinal));
        Assert.True(cut.Markup.IndexOf("2.0.0-rc", StringComparison.Ordinal) < cut.Markup.IndexOf("1.0.0", StringComparison.Ordinal));

        await Releases.PushAsync("ReleaseCreated", new ReleaseDto { Id = 9, ProjectId = 7, ProjectName = "Scoped", Version = "2.0.0", Status = ReleaseStatus.Published });
        await cut.WhenRenderedAsync(() => Assert.DoesNotContain("2.0.0-rc", cut.Markup, StringComparison.Ordinal));
        Assert.Contains("2.0.0", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OnAServersList_ANewRelease_IsReadAgainFromTheServer()
    {
        _handler.SetJsonResponse("api/servers/3/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new ReleaseDto { Id = 4, ProjectId = 7, ProjectName = "Scoped", Version = "3.0.0", Status = ReleaseStatus.Published }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/3/releases/filter-values", new ReleaseFilterValuesDto());
        var cut = await RenderJoinedAsync(parameters => parameters.Add(list => list.ServerId, 3), "3.0.0");
        var reads = Reads("api/servers/3/releases?");

        await Releases.PushAsync("ReleaseCreated", new ReleaseDto { Id = 12, ProjectId = 7, ProjectName = "Scoped", Version = "3.1.0" });

        await _handler.WhenGetAsync("api/servers/3/releases?", reads + 1);
        Assert.True(Reads("api/servers/3/releases?") > reads);
    }
}
