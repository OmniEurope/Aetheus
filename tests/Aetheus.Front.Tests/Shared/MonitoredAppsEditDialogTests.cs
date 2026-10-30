// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Shared;

/// <summary>The monitored applications list opens one form dialog for a new application and for an existing
/// one; only a dialog that saved reloads the list.</summary>
public sealed class MonitoredAppsEditDialogTests : BunitContext
{
    private const string AppsUrl = "api/appmonitoring/projects/1/apps";

    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialogs;

    public MonitoredAppsEditDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse(AppsUrl, new List<MonitoredAppDto>
        {
            new() { Id = 1, ProjectId = 1, Name = "toto-qa", CurrentStatus = AppHealthStatus.Up, ProbeUrl = "https://qa.example.com" }
        });
        _handler.SetJsonResponse("api/appmonitoring/apps/1/samples", new List<AppHealthSampleDto>());
    }

    private IRenderedComponent<MonitoredAppsList> RenderLoaded()
    {
        var cut = Render<MonitoredAppsList>(parameters => parameters.Add(list => list.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("toto-qa"));
        return cut;
    }

    private int AppReads() => _handler.Requests.Count(request => request.Url.Contains(AppsUrl, StringComparison.Ordinal));

    [Fact]
    public async Task ASavedNewApplication_ReloadsTheList()
    {
        // Add sits in the project header (R-109), so the list is rendered next to the header outlet the project layout gives it.
        var cut = this.RenderWithPageHeader<MonitoredAppsList>(parameters => parameters.Add(list => list.ProjectId, 1));
        cut.WaitForState(() => cut.Markup.Contains("toto-qa"));
        var before = AppReads();
        _dialogs.OpenResult = true;

        await cut.Find(".test-outlet").QuerySelectorAll("button").First(button => button.TextContent.Contains("AddMonitoredApp", StringComparison.Ordinal)).ClickAsync(new());

        Assert.Equal(typeof(MonitoredAppFormDialog), _dialogs.LastComponent);
        Assert.Equal("AddMonitoredApp", _dialogs.LastTitle);
        Assert.Equal(1, _dialogs.LastParameters!["ProjectId"]);
        Assert.Null(_dialogs.LastParameters["App"]);
        Assert.Equal(before + 1, AppReads());
    }

    [Fact]
    public async Task EditingAnApplication_OpensItsForm_AndADismissedFormKeepsTheList()
    {
        var cut = RenderLoaded();
        var before = AppReads();
        _dialogs.OpenResult = null;

        await cut.Find("button[title='Edit']").ClickAsync(new());

        Assert.Equal("Edit", _dialogs.LastTitle);
        Assert.Equal(1, Assert.IsType<MonitoredAppDto>(_dialogs.LastParameters!["App"]).Id);
        Assert.Equal(before, AppReads());
    }
}
