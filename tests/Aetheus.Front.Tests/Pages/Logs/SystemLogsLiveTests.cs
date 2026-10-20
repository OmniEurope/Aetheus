// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Logs;

/// <summary>
/// The system logs page follows the admin hub: a change of the system log reads the files and the grid's
/// entries again (the files staying as they were when they cannot be read), a change of another entity
/// reads nothing.
/// </summary>
public sealed class SystemLogsLiveTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public SystemLogsLiveTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        this.UseInMemoryHubs();
        _handler.SetJsonResponse(HttpMethod.Get, "api/system-logs/files", new List<SystemLogFileDto>
        {
            new("app-20260424.log", 102_400, DateTime.UtcNow)
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/system-logs/entries", new PaginatedResult<SystemLogEntryDto>());
    }

    private InMemoryHubServer Admin => this.Hub("admin");

    private int Reads(string path) =>
        _handler.Requests.Count(request => request.Method == "GET" && request.Url.Contains(path, StringComparison.Ordinal));

    private async Task<IRenderedComponent<SystemLogs>> RenderConnectedAsync()
    {
        var cut = Render<SystemLogs>();
        await Admin.WhenAsync(() => Admin.Connections >= 1, "the admin hub connection");
        await _handler.WhenGetAsync("api/system-logs/entries");
        Assert.Equal(1, Admin.Connections);
        return cut;
    }

    // Each push completes once the page's handler ran to its end (the hub's acknowledged push): what it
    // reads, and what it does not, is checked right after it.

    [Fact]
    public async Task AChangeOfTheSystemLog_ReadsTheFilesAndEntriesAgain()
    {
        await RenderConnectedAsync();
        var files = Reads("api/system-logs/files");
        var entries = Reads("api/system-logs/entries");

        await Admin.PushAsync("AdminEntityChanged", AdminEntities.SystemLog, 0, "Updated");

        Assert.Equal(files + 1, Reads("api/system-logs/files"));
        Assert.True(Reads("api/system-logs/entries") > entries);
    }

    [Fact]
    public async Task FilesThatCannotBeRead_StillLetTheEntriesRefresh()
    {
        var cut = await RenderConnectedAsync();
        _handler.SetResponse(HttpMethod.Get, "api/system-logs/files", System.Net.HttpStatusCode.ServiceUnavailable);
        var entries = Reads("api/system-logs/entries");

        await Admin.PushAsync("AdminEntityChanged", AdminEntities.SystemLog, 0, "Updated");

        Assert.True(Reads("api/system-logs/entries") > entries);
        Assert.Contains("app-20260424.log", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AChangeOfAnotherEntity_ReadsNothing()
    {
        await RenderConnectedAsync();
        var before = _handler.Requests.Count;

        await Admin.PushAsync("AdminEntityChanged", "User", 3, "Updated");

        Assert.Equal(before, _handler.Requests.Count);
    }
}
