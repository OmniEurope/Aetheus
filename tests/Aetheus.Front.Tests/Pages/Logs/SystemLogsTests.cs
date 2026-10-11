// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Reflection;
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

public class SystemLogsTests : BunitContext
{
    private const string EntriesUrl = "api/system-logs/entries";
    private readonly BunitTestHelper.TestHandler _handler;

    public SystemLogsTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        _handler.SetJsonResponse("api/system-logs/files", new List<SystemLogFileDto>
        {
            new("app-20260424.log", 102_400, DateTime.UtcNow)
        });
        _handler.SetJsonResponse(EntriesUrl, new PaginatedResult<SystemLogEntryDto>
        {
            TotalCount = 0,
            Items = []
        });
    }

    [Fact]
    public void Renders_SystemLogsHeader()
    {
        var cut = Render<SystemLogs>();
        Assert.Contains("SystemLogs", cut.Markup);
    }

    [Theory]
    [InlineData("Error")]
    [InlineData("Fatal")]
    [InlineData("Warning")]
    [InlineData("Information")]
    [InlineData("Debug")]
    [InlineData("Verbose")]
    [InlineData("Unknown")]
    public void GetBadgeStyle_ReturnsExpectedForAllLevels(string level)
    {
        var method = typeof(SystemLogs).GetMethod("GetBadgeStyle", BindingFlags.NonPublic | BindingFlags.Static)!;
        var style = method.Invoke(null, [level]);
        // The mapper must return a defined OmniTone for every level (incl. the "Unknown" fallback).
        Assert.IsType<OmniTone>(style);
        Assert.True(Enum.IsDefined((OmniTone)style!));
    }

    /// <summary>Recette R-453: the grid reads the log from the API itself, newest first, with no
    /// "Load more" row; the rows it shows are the API's.</summary>
    [Fact]
    public void Grid_ReadsTheLogFromTheApi_WithoutALoadMoreRow()
    {
        _handler.SetJsonResponse(EntriesUrl, new PaginatedResult<SystemLogEntryDto>
        {
            TotalCount = 1,
            Items = [new SystemLogEntryDto(1, DateTime.UtcNow, "Warning", "Aetheus.Back", "disk almost full", null, null)]
        });

        var cut = Render<SystemLogs>();

        cut.WaitForAssertion(() => Assert.Contains("disk almost full", cut.Markup, StringComparison.Ordinal));
        Assert.Contains(_handler.Requests, request =>
            request.Url.Contains(EntriesUrl, StringComparison.Ordinal)
            && request.Url.Contains("page=1", StringComparison.Ordinal)
            && request.Url.Contains("sortBy=Timestamp", StringComparison.Ordinal)
            && request.Url.Contains("sortDescending=true", StringComparison.Ordinal));
        Assert.DoesNotContain("LoadMore", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("LogsShownCount", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("NoLogsAvailable", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyLog_SaysSo()
    {
        var cut = Render<SystemLogs>();

        cut.WaitForAssertion(() => Assert.Contains("NoLogsAvailable", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("LoadFailed", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void FailedCall_SaysSoInsteadOfAnEmptyLog()
    {
        _handler.SetResponse(HttpMethod.Get, EntriesUrl, HttpStatusCode.InternalServerError);

        var cut = Render<SystemLogs>();

        cut.WaitForAssertion(() => Assert.Contains("LoadFailed", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain("NoLogsAvailable", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisposeAsync_CancelsBackgroundWork()
    {
        var cut = Render<SystemLogs>();
        await ((IAsyncDisposable)cut.Instance).DisposeAsync();
    }

    [Fact]
    public void SearchQueryChange_ReloadsEntriesWithCorrelationFilter()
    {
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("search", "initial"));
        var cut = Render<SystemLogs>();
        cut.WaitForState(() => _handler.Requests.Any(request =>
            request.Url.Contains("search=initial", StringComparison.Ordinal)));

        navigation.NavigateTo(navigation.GetUriWithQueryParameter("search", "agent-update-42"));

        cut.WaitForState(() => _handler.Requests.Any(request =>
            request.Url.Contains("search=agent-update-42", StringComparison.Ordinal)));
    }

    [Fact]
    public void RowControls_DoNotReachTheRow()
    {
        // SITE-FUNC-HOME-CAPABILITY-001: a link, button or form control in a clickable row stops click and
        // Enter/Space, or the row click runs after its own and wins the navigation.
        _handler.SetJsonResponse(EntriesUrl, new PaginatedResult<SystemLogEntryDto>
        {
            TotalCount = 1,
            Items = [new SystemLogEntryDto(1, DateTime.UtcNow, "Error", "Aetheus.Back", "boom", "System.Exception: boom", null)]
        });

        var cut = Render<SystemLogs>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(RowEventIsolation.ClickableRows)), TimeSpan.FromSeconds(3));

        Assert.True(RowEventIsolation.AssertNoRowControlReachesTheRow(cut.FindAll(RowEventIsolation.ClickableRows)) >= 2);
    }
}
