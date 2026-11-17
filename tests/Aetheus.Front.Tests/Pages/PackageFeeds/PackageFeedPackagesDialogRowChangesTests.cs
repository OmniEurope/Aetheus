// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.PackageFeeds;
using Bunit;

namespace Aetheus.Front.Tests.Pages.PackageFeeds;

/// <summary>R-10 on the packages of a feed: a package added shows at once from the answer of the add, before the feed is read
/// again and without the grid's blocking bar; a refused add (a 409 included) leaves the rows and says so. A sync answers
/// counts, not rows, so it stays a read and is not covered here.</summary>
public sealed class PackageFeedPackagesDialogRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string FeedUrl = "api/package-feeds/3";
    private readonly BunitTestHelper.TestHandler _handler;

    public PackageFeedPackagesDialogRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse(HttpMethod.Get, FeedUrl, Feed(Entry(7, "Serilog")));
    }

    private static PackageEntryDto Entry(int id, string name) => new() { Id = id, PackageFeedId = 3, Name = name, LatestVersion = "1.0.0" };

    private static PackageFeedDetailDto Feed(params PackageEntryDto[] packages) => new() { Id = 3, Name = "corp-nuget", Packages = [.. packages] };

    private TaskCompletionSource<PackageFeedDetailDto> HoldTheNextFeedRead()
    {
        var gate = new TaskCompletionSource<PackageFeedDetailDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, FeedUrl, _ => gate.Task);
        return gate;
    }

    private int FeedReads() => _handler.RequestsSnapshot()
        .Count(request => request.Method == "GET" && request.Url.EndsWith(FeedUrl, StringComparison.Ordinal));

    // The text field of the add form keeps what was typed after a refusal: only the grid's rows tell what the table holds.
    private static List<string> GridRows(IRenderedComponent<PackageFeedPackagesDialog> cut) =>
        [.. cut.FindAll("tbody tr").Select(row => row.TextContent)];

    private IRenderedComponent<PackageFeedPackagesDialog> RenderDialog()
    {
        var cut = Render<PackageFeedPackagesDialog>(p => p.Add(x => x.FeedId, 3));
        cut.WaitForAssertion(() => Assert.Contains("Serilog", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    // The real add row: the name is typed in the text field, then the Add button is clicked, as a user would.
    private static Task AddAsync(IRenderedComponent<PackageFeedPackagesDialog> cut, string name)
    {
        cut.Find("input[aria-label='PackageName']").Input(name);
        return cut.FindAll("button").First(button => button.TextContent.Contains("Add", StringComparison.Ordinal)).ClickAsync(new());
    }

    [Fact]
    public async Task AnAddedPackage_ShowsTheAnsweredRowAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderDialog();
        _handler.SetJsonResponse(HttpMethod.Post, FeedUrl + "/packages", Entry(8, "Polly"));
        var gate = HoldTheNextFeedRead();

        var add = AddAsync(cut, "Polly");

        cut.WaitForAssertion(() => Assert.Contains(GridRows(cut), row => row.Contains("Polly", StringComparison.Ordinal)));
        // The read behind the row is held: the row on screen comes from the answer of the add alone.
        Assert.Equal(2, FeedReads());
        Assert.Contains("Serilog", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Feed(Entry(7, "Serilog"), Entry(8, "Polly"), Entry(9, "Dapper")));
        cut.WaitForAssertion(() => Assert.Contains("Dapper", cut.Markup, StringComparison.Ordinal));
        await add;
    }

    [Fact]
    public async Task ARefusedAdd_LeavesTheRowsAsTheyWere_ReportsTheError_AndReadsNothingAgain()
    {
        var cut = RenderDialog();
        _handler.SetResponse(HttpMethod.Post, FeedUrl + "/packages", HttpStatusCode.Conflict);
        var readsBefore = FeedReads();

        await AddAsync(cut, "Polly");

        Assert.DoesNotContain(GridRows(cut), row => row.Contains("Polly", StringComparison.Ordinal));
        Assert.Contains("Serilog", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "SaveFailed");
        Assert.Equal(readsBefore, FeedReads());
    }
}
