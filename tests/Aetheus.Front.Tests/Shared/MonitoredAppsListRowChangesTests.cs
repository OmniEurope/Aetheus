// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-10 on the monitored applications grid: a deleted application leaves the table at once, before the list is read
/// again; a refused deletion (a 409 included) keeps the row and says so.</summary>
public sealed class MonitoredAppsListRowChangesTests : BunitContext
{
    private const string AppsUrl = "api/appmonitoring/projects/1/apps";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public MonitoredAppsListRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialogs.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Get, AppsUrl, Apps(App(1, "toto-qa"), App(2, "toto-prod")));
        _handler.SetJsonResponse("api/appmonitoring/apps/1/samples", new List<AppHealthSampleDto>());
        _handler.SetJsonResponse("api/appmonitoring/apps/2/samples", new List<AppHealthSampleDto>());
        _handler.SetJsonResponse("api/appmonitoring/apps/3/samples", new List<AppHealthSampleDto>());
    }

    private static MonitoredAppDto App(int id, string name) => new() { Id = id, ProjectId = 1, Name = name, CurrentStatus = AppHealthStatus.Up };

    private static List<MonitoredAppDto> Apps(params MonitoredAppDto[] apps) => [.. apps];

    private IRenderedComponent<MonitoredAppsList> RenderList()
    {
        var cut = Render<MonitoredAppsList>(p => p.Add(c => c.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.Contains("toto-prod", cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    [Fact]
    public async Task ADeletedApp_LeavesTheGridAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderList();
        var gate = new TaskCompletionSource<List<MonitoredAppDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, AppsUrl, _ => gate.Task);
        _handler.SetResponse(HttpMethod.Delete, "api/appmonitoring/apps/2", HttpStatusCode.NoContent);

        var delete = cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("toto-prod", cut.Markup, StringComparison.Ordinal));
        // The read behind the row is held: the row left from the answer of the deletion alone.
        Assert.Equal(2, AppsReads());
        Assert.Contains("toto-qa", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Apps(App(1, "toto-qa"), App(3, "toto-new")));
        cut.WaitForAssertion(() => Assert.Contains("toto-new", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("toto-prod", cut.Markup, StringComparison.Ordinal);
        await delete;
    }

    [Fact]
    public async Task ARefusedDeletion_KeepsTheRow_ReportsTheError_AndReadsNothingAgain()
    {
        var cut = RenderList();
        var readsBefore = AppsReads();
        _handler.SetResponse(HttpMethod.Delete, "api/appmonitoring/apps/2", HttpStatusCode.Conflict);

        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        Assert.Contains("toto-prod", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");
        Assert.Equal(readsBefore, AppsReads());
    }

    [Fact]
    public async Task ACreatedApp_ShowsAtOnce_BeforeTheReadBehindItEnds()
    {
        var cut = RenderList();
        var readsBefore = AppsReads();
        var gate = new TaskCompletionSource<List<MonitoredAppDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, AppsUrl, _ => gate.Task);
        _dialogs.OpenResult = App(3, "toto-new");

        var open = cut.FindAll("tbody tr")[0].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("toto-new", cut.Markup, StringComparison.Ordinal));
        // The read behind the row is held: the row came from the answer of the dialog alone.
        Assert.Equal(readsBefore + 1, AppsReads());
        Assert.Contains("toto-qa", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Apps(App(1, "toto-qa"), App(2, "toto-prod"), App(3, "toto-new")));
        await open;
    }

    [Fact]
    public async Task ASavedApp_ChangesInPlace_BeforeTheReadBehindItEnds()
    {
        var cut = RenderList();
        var gate = new TaskCompletionSource<List<MonitoredAppDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, AppsUrl, _ => gate.Task);
        _dialogs.OpenResult = App(2, "toto-prod-renamed");

        var open = cut.FindAll("tbody tr")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("toto-prod-renamed", cut.Markup, StringComparison.Ordinal));
        Assert.Single(cut.Markup.Split("toto-prod-renamed").Skip(1).Take(1));
        Assert.Contains("toto-qa", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Apps(App(1, "toto-qa"), App(2, "toto-prod-renamed")));
        await open;
    }

    [Fact]
    public async Task ACancelledDialog_ChangesNothing_AndReadsNothingAgain()
    {
        var cut = RenderList();
        var readsBefore = AppsReads();
        _dialogs.OpenResult = false;

        await cut.FindAll("tbody tr")[0].ClickAsync(new());

        Assert.Equal(readsBefore, AppsReads());
    }

    private int AppsReads() => _handler.RequestsSnapshot()
        .Count(request => request.Method == "GET" && request.Url.EndsWith(AppsUrl, StringComparison.Ordinal));
}
