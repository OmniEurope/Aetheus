// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using BackupsPage = Aetheus.Front.Components.AppBackups.Backups;

namespace Aetheus.Front.Tests.Pages.Backups;

/// <summary>
/// The backups page follows the entities hub: a backup change refreshes the policies, and the run
/// history when one is open; on a project's page only that project's changes count; after a reconnect
/// the page joins its updates again and refreshes.
/// </summary>
public sealed class BackupsRealtimeTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public BackupsRealtimeTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/projects", new[] { new ProjectDto { Id = 1, Name = "aetheus" } });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/servers", new[] { new ServerDto { Id = 2, Name = "vps-1" } });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups", new[]
        {
            new BackupPolicyDto { Id = 1, Name = "nightly-db", Enabled = true, ProjectId = 1, ServerId = 2, ScheduleCron = "0 3 * * *" }
        });
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups/1/runs", Array.Empty<BackupRunDto>());
    }

    private InMemoryHubServer Entities => this.Hub("entities");

    private int Reads(string path) =>
        _handler.Requests.ToList().Count(request => request.Method == "GET" && request.Url.Contains(path, StringComparison.Ordinal));

    private async Task<IRenderedComponent<BackupsPage>> RenderConnectedAsync(int? projectId = null)
    {
        var cut = Render<BackupsPage>(parameters => parameters.Add(page => page.ProjectId, projectId));
        await Entities.WhenInvokedAsync("JoinEntityUpdates");
        await cut.WhenRenderedAsync(() => Assert.Contains("nightly-db", cut.Markup, StringComparison.Ordinal));
        Assert.Equal(1, Entities.Count("JoinEntityUpdates"));
        return cut;
    }

    // Each push below completes once the page's handler ran to its end (the hub's acknowledged push), so
    // what the handler reloads, and what it leaves alone, is read directly afterwards.

    [Fact]
    public async Task ABackupChange_RefreshesThePolicies_AndTheOpenRunHistory()
    {
        var cut = await RenderConnectedAsync();
        cut.FindAll("button").First(button => button.Names().Contains("BackupRuns", StringComparison.Ordinal)).Click();
        await _handler.WhenGetAsync("api/backups/1/runs");
        Assert.Equal(1, Reads("api/backups/1/runs"));
        var policies = Reads("api/backups?");

        await Entities.PushAsync(OperationalRealtimeEvents.BackupChanged, 1);

        Assert.True(Reads("api/backups?") > policies);
        Assert.Equal(2, Reads("api/backups/1/runs"));
    }

    [Fact]
    public async Task WithoutAnOpenRunHistory_OnlyThePoliciesAreRefreshed()
    {
        await RenderConnectedAsync();
        var policies = Reads("api/backups?");

        await Entities.PushAsync(OperationalRealtimeEvents.BackupChanged, 1);

        Assert.True(Reads("api/backups?") > policies);
        Assert.Equal(0, Reads("api/backups/1/runs"));
    }

    [Fact]
    public async Task OnAProjectsPage_AnotherProjectsChangeRefreshesNothing()
    {
        await RenderConnectedAsync(projectId: 1);
        var before = Reads("api/backups?");

        await Entities.PushAsync(OperationalRealtimeEvents.BackupChanged, 4);

        Assert.Equal(before, Reads("api/backups?"));
    }

    [Fact]
    public async Task AfterAReconnect_TheUpdatesAreJoinedAgain_AndThePoliciesRefreshed()
    {
        await RenderConnectedAsync();
        var policies = Reads("api/backups?");

        await Entities.DropAsync();

        await Entities.WhenInvokedAsync("JoinEntityUpdates", 2);
        await _handler.WhenGetAsync("api/backups?", policies + 1);
        Assert.Equal(2, Entities.Count("JoinEntityUpdates"));
    }
}
