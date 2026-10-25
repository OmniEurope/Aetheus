// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-41: the lines of a tool (gitleaks, trivy...) reach the screen with their ANSI colours
/// drawn, never as the raw <c>[90m</c>, while the stored line and the export keep the escape sequences.</summary>
public sealed class AnsiLogDisplayTests : BunitContext
{
    // gitleaks' own line from the recette: grey time, green INF, bold message.
    private const string GitleaksLine = "\u001b[90m3:27PM\u001b[0m \u001b[32mINF\u001b[0m \u001b[1mno leaks found\u001b[0m";

    private readonly BunitTestHelper.TestHandler _handler;

    public AnsiLogDisplayTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static void AssertDrawnNotRaw(string markup)
    {
        Assert.DoesNotContain("[90m", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[32m", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("[0m", markup, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', markup);
    }

    [Fact]
    public void R41_ARunStepLog_DrawsTheAnsiColours_AndKeepsTheStoredLine()
    {
        var log = new TaskLogDto { Id = 1, Message = GitleaksLine, Level = TaskLogLevel.Error };

        var cut = Render<PipelineRunLogLines>(p => p.Add(c => c.Logs, [log]));

        AssertDrawnNotRaw(cut.Markup);
        Assert.Contains("3:27PM INF no leaks found", cut.Find(".run-log-line").TextContent, StringComparison.Ordinal);
        Assert.Equal("3:27PM", cut.Find(".omni-ansi-fg-8").TextContent);
        Assert.Equal("INF", cut.Find(".omni-ansi-fg-2").TextContent);
        Assert.Equal("no leaks found", cut.Find(".omni-ansi-bold").TextContent);
        // The display changes, the data does not: the stored line still holds its sequences.
        Assert.Equal(GitleaksLine, log.Message);
    }

    [Fact]
    public void R41_APlainRunStepLog_IsShownAsWritten()
    {
        var cut = Render<PipelineRunLogLines>(p => p
            .Add(c => c.Logs, [new TaskLogDto { Id = 1, Message = "array[90] is [32m wide" }]));

        // Without the escape character a bracketed word is ordinary text and nothing is lost from the line.
        Assert.Contains("array[90] is [32m wide", cut.Find(".run-log-line").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R41_TheRunSearch_RunsOnTheDisplayedText_NotOnTheSequences()
    {
        List<TaskLogDto> logs = [new() { Id = 1, Message = GitleaksLine }, new() { Id = 2, Message = "other" }];

        Assert.Single(PipelineRunLogView.FilterLogs(logs, "no leaks"));
        Assert.Empty(PipelineRunLogView.FilterLogs(logs, "90m"));
        // Line numbers stay those of the whole log.
        Assert.Equal(2, Assert.Single(PipelineRunLogView.FilterLogs(logs, "other")).Num);
    }

    [Theory]
    [InlineData(GitleaksLine, true)]
    [InlineData("plain line", false)]
    [InlineData("[90m without the escape character", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void R41_AnsiLine_TellsALineWithSequences(string? line, bool expected) =>
        Assert.Equal(expected, AnsiLine.HasSequences(line));

    [Fact]
    public async Task R41_TheServerLogGrid_DrawsTheColours_AndItsMarkdownExportKeepsTheRawLine()
    {
        var module = JSInterop.SetupModule("./_content/OmniEurope.Blazor/omni-document-editor.js");
        module.SetupVoid("download", _ => true).SetVoidResult();
        _handler.SetJsonResponse("api/servers/1/logs", new PaginatedResult<TaskLogDto>
        {
            Items = [new TaskLogDto { Id = 1, Message = GitleaksLine, Level = TaskLogLevel.Error, Timestamp = DateTime.UtcNow }],
            TotalCount = 1
        });

        var cut = Render<ServerLogsSection>(p => p.Add(c => c.ServerId, 1));
        cut.WaitForAssertion(() => Assert.Contains("no leaks found", cut.Markup, StringComparison.Ordinal));

        AssertDrawnNotRaw(cut.Markup);
        Assert.Equal("INF", cut.Find(".omni-ansi-fg-2").TextContent);

        await cut.FindAll(".omni-data-grid__export-button")[0].ClickAsync(new());
        cut.WaitForAssertion(() => Assert.Single(module.Invocations["download"]));
        var markdown = Encoding.UTF8.GetString(Assert.IsType<byte[]>(module.Invocations["download"].Single().Arguments[2]));
        Assert.Contains(GitleaksLine, markdown, StringComparison.Ordinal);
    }
}
