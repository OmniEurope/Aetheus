// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.PackageFeeds;
using Bunit;

namespace Aetheus.Front.Tests.Pages.PackageFeeds;

public class PackageFeedsAdminTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PackageFeedsAdminTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    [Fact]
    public void Renders_FeedsList()
    {
        _handler.SetJsonResponse("api/package-feeds", new PaginatedResult<PackageFeedDto>
        {
            Items = [new() { Id = 1, Name = "corp-nuget", FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://api.nuget.org", PackageCount = 2 }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25
        });

        var cut = Render<PackageFeedsAdmin>();

        cut.WaitForState(() => cut.Markup.Contains("corp-nuget"));
        Assert.Contains("corp-nuget", cut.Markup);
        Assert.Contains("NuGet", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoFeeds()
    {
        _handler.SetJsonResponse("api/package-feeds", new PaginatedResult<PackageFeedDto>());

        var cut = Render<PackageFeedsAdmin>();

        cut.WaitForState(() => cut.Markup.Contains("NoPackageFeeds"));
        Assert.Contains("NoPackageFeeds", cut.Markup);
    }

    [Fact]
    public async Task LoadData_RequestsRequestedServerPageSearchAndSort()
    {
        _handler.SetJsonResponse("api/package-feeds", new PaginatedResult<PackageFeedDto>
        {
            Items = [],
            TotalCount = 60,
            Page = 2,
            PageSize = 25
        });
        var cut = Render<PackageFeedsAdmin>();
        typeof(PackageFeedsAdmin).GetField("_search", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(cut.Instance, "nuget");
        var load = typeof(PackageFeedsAdmin).GetMethod("LoadDataAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

        await cut.InvokeAsync(async () => await (Task)load.Invoke(cut.Instance,
            [new GridLoadArgs { Skip = 25, Top = 25, OrderBy = "PackageCount desc" }])!);

        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("page=2", StringComparison.Ordinal)
            && request.Url.Contains("pageSize=25", StringComparison.Ordinal)
            && request.Url.Contains("search=nuget", StringComparison.Ordinal)
            && request.Url.Contains("sortBy=PackageCount", StringComparison.Ordinal)
            && request.Url.Contains("sortDescending=true", StringComparison.Ordinal));
    }
}
