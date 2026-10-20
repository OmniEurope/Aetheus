// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerReleasesSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerReleasesSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    [Fact]
    public void Renders_EmptyReleases_ShowsHeadingAndEmptyState()
    {
        _handler.SetJsonResponse("api/servers/1/releases", new PaginatedResult<ReleaseDto>());
        var cut = Render<ServerReleasesSection>(p => p.Add(x => x.ServerId, 1));
        // Server-scope ReleasesList always renders the "Releases" heading, and the empty
        // grid shows the NoReleasesFound empty-state (not just an empty shell).
        Assert.Contains("Releases", cut.Markup);
        cut.WaitForAssertion(() => Assert.Contains("NoReleasesFound", cut.Markup));
    }

    [Fact]
    public void Renders_WithReleases_ShowsVersionAndLink()
    {
        _handler.SetJsonResponse("api/servers/1/releases", new PaginatedResult<ReleaseDto>
        {
            Items = [new() { Id = 5, Version = "1.0.0", Status = ReleaseStatus.Published, BranchName = "main" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });
        var cut = Render<ServerReleasesSection>(p => p.Add(x => x.ServerId, 1));
        // The release version is loaded into the grid and rendered as a link to /releases/{id}.
        cut.WaitForAssertion(() => Assert.Contains("1.0.0", cut.Markup));
        Assert.Contains("/releases/5", cut.Markup);
        Assert.Contains("Published", cut.Markup);
    }
}
