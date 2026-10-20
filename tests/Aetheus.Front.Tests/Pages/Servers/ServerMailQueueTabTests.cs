// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The queue tab parses the <c>postqueue -j</c> output of its own task and deletes one
/// message only after confirmation.</summary>
public sealed class ServerMailQueueTabTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerMailQueueTabTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private IRenderedComponent<ServerMailQueueTab> RenderTab() =>
        Render<ServerMailQueueTab>(p => p.Add(x => x.ServerId, 10).Add(x => x.QueueSize, 1).Add(x => x.CanManage, true));

    [Fact]
    public async Task LoadingTheQueue_ParsesTheTaskOutput()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/queue/refresh", new MailTaskQueuedDto { TaskId = 51 });
        _handler.SetJsonResponse("api/logs/task/51", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 51, Message = "{\"queue_name\": \"deferred\", \"queue_id\": \"1736261560\", \"arrival_time\": 1789401655, \"message_size\": 310, \"sender\": \"admin@example.test\", \"recipients\": [{\"address\": \"bob@second.test\", \"delay_reason\": \"Connection refused\"}]}" }
        });
        var cut = RenderTab();

        cut.FindAll("button").First(b => b.TextContent.Contains("MailQueueLoad", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.EndsWith("mail/queue/refresh", StringComparison.Ordinal)));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 51, ServerId = 10, Status = TaskExecutionStatus.Success }));

        var item = Assert.Single(cut.Instance.Items);
        Assert.Equal(("1736261560", "bob@second.test"), (item.Id, item.Recipient));
        Assert.Contains("1736261560", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeletingAQueuedMessage_RequiresConfirmation()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/queue/refresh", new MailTaskQueuedDto { TaskId = 52 });
        _handler.SetJsonResponse("api/logs/task/52", new List<TaskLogDto>
        {
            new() { Id = 1, TaskId = 52, Message = "{\"queue_name\": \"deferred\", \"queue_id\": \"4F2A1B3C9D\", \"sender\": \"a@example.com\", \"recipients\": []}" }
        });
        var cut = RenderTab();
        cut.FindAll("button").First(b => b.TextContent.Contains("MailQueueLoad", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.EndsWith("mail/queue/refresh", StringComparison.Ordinal)));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 52, ServerId = 10, Status = TaskExecutionStatus.Success }));

        _handler.SetResponse(System.Net.Http.HttpMethod.Delete, "api/servers/10/mail/queue/4F2A1B3C9D", System.Net.HttpStatusCode.OK);
        _dialog.ConfirmResult = false;
        cut.Find("button[title='Delete']").Click();
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "DELETE");

        _dialog.ConfirmResult = true;
        cut.Find("button[title='Delete']").Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r =>
            r.Method == "DELETE" && r.Url.EndsWith("mail/queue/4F2A1B3C9D", StringComparison.Ordinal)));
    }
}
