// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Journal read through the mail helper, displayed from the output of its task.</summary>
public sealed class ServerMailLogsTabTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerMailLogsTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    [Fact]
    public async Task FetchingLogs_QueuesATaskAndShowsItsOutput()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/logs", new MailTaskQueuedDto { TaskId = 61 });
        _handler.SetJsonResponse("api/logs/task/61", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 61, Message = "2026-09-14T15:03:56 postfix/smtp[1]: 732B856E74: status=sent" }
        });
        var cut = Render<ServerMailLogsTab>(p => p.Add(x => x.ServerId, 10));

        cut.Find("input[placeholder='MailLogFilter']").Input("732B856E74");
        cut.FindAll("button").First(b => b.TextContent.Contains("FetchLogs", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Method == "POST" && r.Url.EndsWith("mail/logs", StringComparison.Ordinal)
            && r.Body!.Contains("732B856E74", StringComparison.Ordinal) && r.Body.Contains("postfix", StringComparison.Ordinal)));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 61, ServerId = 10, Status = TaskExecutionStatus.Success }));

        Assert.Contains("status=sent", cut.Instance.Content, StringComparison.Ordinal);
        Assert.Contains("status=sent", cut.Find(".docker-logs pre").TextContent, StringComparison.Ordinal);
    }

    /// <summary>Recette R-116: while the journal is read, OE's busy veil lies over the tab; no loader sits beside the button.</summary>
    [Fact]
    public async Task WhileTheJournalIsRead_TheTabIsVeiled_AndNoLoaderIsAdded()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/logs", new MailTaskQueuedDto { TaskId = 62 });
        _handler.SetJsonResponse("api/logs/task/62", new List<TaskLogDto> { new() { Id = 1, TaskId = 62, Message = "line" } });
        var cut = Render<ServerMailLogsTab>(p => p.Add(x => x.ServerId, 10));
        Assert.Empty(cut.FindAll(".omni-busy"));

        cut.FindAll("button").First(b => b.TextContent.Contains("FetchLogs", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal("true", cut.Find(".omni-busy").GetAttribute("aria-busy")));
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));
        Assert.NotNull(cut.Find("input[placeholder='MailLogFilter']"));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 62, ServerId = 10, Status = TaskExecutionStatus.Success }));
        Assert.Empty(cut.FindAll(".omni-busy"));
    }

    [Fact]
    public void AnInvalidFilter_IsNotSent()
    {
        var cut = Render<ServerMailLogsTab>(p => p.Add(x => x.ServerId, 10));

        cut.Find("input[placeholder='MailLogFilter']").Input("$(id)");
        cut.FindAll("button").First(b => b.TextContent.Contains("FetchLogs", StringComparison.Ordinal)).Click();

        Assert.DoesNotContain(_handler.Requests, r => r.Url.EndsWith("mail/logs", StringComparison.Ordinal));
    }
}
