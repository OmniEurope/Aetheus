// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Logs;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Components.Shared;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>
/// Recette R3-35, log grids: clicking a line of the server, project or application log opens that
/// exact entry in <see cref="LogEntryDialog"/>, as the system log does. Each test clicks a cell.
/// </summary>
public sealed class LogGridRowClickTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public LogGridRowClickTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private static AngleSharp.Dom.IElement RowCell<T>(IRenderedComponent<T> cut, string rowText) where T : Microsoft.AspNetCore.Components.IComponent =>
        cut.FindAll(RowEventIsolation.ClickableRows)
            .First(row => row.TextContent.Contains(rowText, StringComparison.Ordinal))
            .QuerySelector("td")!;

    private static PaginatedResult<TaskLogDto> TwoLines() => new()
    {
        Items =
        [
            new TaskLogDto { Id = 1, Message = "first line", Level = TaskLogLevel.Info, Timestamp = DateTime.UtcNow },
            new TaskLogDto { Id = 2, Message = "AH00526: Syntax error on line 7", Level = TaskLogLevel.Error, Timestamp = DateTime.UtcNow }
        ],
        TotalCount = 2
    };

    [Fact]
    public void ServerLogs_RowClick_OpensThatEntry()
    {
        _handler.SetJsonResponse("api/servers/1/logs", TwoLines());
        var cut = Render<ServerLogsSection>(p => p.Add(x => x.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains("AH00526", cut.Markup));

        RowCell(cut, "AH00526").Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(LogEntryDialog), _dialog.LastComponent));
        Assert.Equal("AH00526: Syntax error on line 7", _dialog.LastParameters![nameof(LogEntryDialog.Message)]);
    }

    [Fact]
    public void ProjectLogs_RowClick_OpensThatEntry()
    {
        _handler.SetJsonResponse("api/projects/1/logs", TwoLines());
        var cut = Render<ProjectLogsSection>(p => p.Add(x => x.ProjectId, 1));
        cut.WaitForAssertion(() => Assert.Contains("AH00526", cut.Markup));

        RowCell(cut, "first line").Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(LogEntryDialog), _dialog.LastComponent));
        Assert.Equal("first line", _dialog.LastParameters![nameof(LogEntryDialog.Message)]);
    }

    [Fact]
    public void AppLogs_RowClick_OpensThatEntry_WithItsValuesAndAttributes()
    {
        const string attributes = "{\"ElapsedMilliseconds\":\"14.64\",\"StatusCode\":\"200\"}";
        _handler.SetJsonResponse("api/appmonitoring/apps/3/logs", new PaginatedResult<AppLogEntryDto>
        {
            Items =
            [
                new AppLogEntryDto
                {
                    Id = 1,
                    Timestamp = new DateTime(2026, 9, 12, 12, 10, 40, DateTimeKind.Utc),
                    SeverityNumber = 9,
                    Body = "Received HTTP response headers after {ElapsedMilliseconds}ms - {StatusCode}",
                    AttributesJson = attributes
                }
            ],
            TotalCount = 1
        });
        var cut = Render<AppLogsView>(p => p.Add(x => x.AppId, 3));
        cut.WaitForAssertion(() => Assert.Contains("after 14.64ms - 200", cut.Markup));

        RowCell(cut, "after 14.64ms").Click();

        cut.WaitForAssertion(() => Assert.Equal(typeof(LogEntryDialog), _dialog.LastComponent));
        Assert.Equal("Received HTTP response headers after 14.64ms - 200", _dialog.LastParameters![nameof(LogEntryDialog.Message)]);
        Assert.Equal(attributes, _dialog.LastParameters[nameof(LogEntryDialog.Exception)]);
        Assert.Equal("LogAttributes", _dialog.LastParameters[nameof(LogEntryDialog.DetailsTitle)]);
    }

    [Fact]
    public void LogEntryDialog_NamesItsSecondBlockAsAsked_ExceptionDetailsByDefault()
    {
        var byDefault = Render<LogEntryDialog>(p => p.Add(x => x.Message, "m").Add(x => x.Exception, "boom"));
        Assert.Contains("ExceptionDetails", byDefault.Markup);

        var attributes = Render<LogEntryDialog>(p => p.Add(x => x.Message, "m").Add(x => x.Exception, "{}").Add(x => x.DetailsTitle, "LogAttributes"));
        Assert.Contains("LogAttributes", attributes.Markup);
        Assert.DoesNotContain("ExceptionDetails", attributes.Markup);
    }
}
