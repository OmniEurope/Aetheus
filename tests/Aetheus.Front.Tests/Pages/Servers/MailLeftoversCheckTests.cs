// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R3-23 / R3-26: one check says whether a mail stack left anything on the host
/// (packages, directories, marker, ports), under OE's veil while it runs.</summary>
public sealed class MailLeftoversCheckTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public MailLeftoversCheckTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    private static ServerDetailDto Server(bool canCheck = true) =>
        ServerTestData.MakeMailServer(installed: false) with
        {
            AgentCapabilities = canCheck ? [AgentCapabilities.MailLeftoversCheck, AgentCapabilities.ShellExecution] : [AgentCapabilities.ShellExecution]
        };

    private IRenderedComponent<MailLeftoversCheck> RenderCheck(ServerDetailDto? server = null) =>
        Render<MailLeftoversCheck>(p => p.Add(x => x.Server, server ?? Server()).Add(x => x.ServerId, 10));

    private void StubQueue(int taskId) =>
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/10/mail/leftovers-check", new MailTaskQueuedDto { TaskId = taskId });

    private void StubOutput(int taskId, params string[] lines) =>
        _handler.SetJsonResponse($"api/logs/task/{taskId}",
            lines.Select((line, index) => new TaskLogDto { Id = index + 1, TaskId = taskId, Message = line }).ToList());

    private async Task CompleteAsync(int taskId, TaskExecutionStatus status) =>
        await InvokeAsync(() => Tracker.HandleTerminal(new TaskCompletedNotification
        {
            TaskId = taskId,
            ServerId = 10,
            TaskName = MailTaskNames.LeftoversCheck,
            Status = status
        }));

    private Task InvokeAsync(Action action) => Renderer.Dispatcher.InvokeAsync(action);

    [Fact]
    public async Task R3_23_TheCheck_RunsUnderTheVeil_ThenListsEachLeftoverByKind()
    {
        StubQueue(300);
        StubOutput(300,
            "LEFTOVER\tpackage\topendkim\tii ",
            "LEFTOVER\tpackage\tdovecot-core\trc ",
            "LEFTOVER\tpath\t/etc/dovecot\t",
            "LEFTOVER\tmarker\t/etc/aetheus/mail-stack.managed\t",
            "LEFTOVER\tport\t11332\trspamd",
            "CLEAN\tno");
        var cut = RenderCheck();

        cut.Find("button.mail-leftovers-check").Click();

        cut.WaitForAssertion(() => Assert.True(cut.Instance.Busy));
        var card = cut.Find(".mail-leftovers");
        Assert.Contains("omni-busy", card.ClassList);
        Assert.Equal("true", card.GetAttribute("aria-busy"));
        Assert.Contains("/tasks/300", cut.Markup, StringComparison.Ordinal);

        await CompleteAsync(300, TaskExecutionStatus.Success);

        cut.WaitForAssertion(() => Assert.False(cut.Instance.Busy));
        Assert.DoesNotContain("omni-busy", cut.Find(".mail-leftovers").ClassList);
        Assert.Equal(["package", "path", "marker", "port"],
            cut.FindAll(".mail-leftovers-group").Select(group => group.GetAttribute("data-kind")).ToArray());
        var found = cut.Find(".mail-leftovers-found").TextContent;
        Assert.Contains("MailLeftoverPackageConfig", found, StringComparison.Ordinal);
        Assert.Contains("/etc/dovecot", found, StringComparison.Ordinal);
        Assert.Contains("/etc/aetheus/mail-stack.managed", found, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".mail-leftovers-clean"));
    }

    [Fact]
    public async Task R3_23_NothingLeft_SaysTheServerIsClean()
    {
        StubQueue(301);
        StubOutput(301, "CLEAN\tyes");
        var cut = RenderCheck();
        cut.Find("button.mail-leftovers-check").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Busy));

        await CompleteAsync(301, TaskExecutionStatus.Success);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-leftovers-clean")));
        Assert.True(cut.Instance.Report!.IsClean);
    }

    [Fact]
    public async Task R3_23_ACheckThatDidNotFinish_IsNeverShownAsClean()
    {
        StubQueue(302);
        StubOutput(302, "dpkg-query could not list the mail packages");
        var cut = RenderCheck();
        cut.Find("button.mail-leftovers-check").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.Busy));

        await CompleteAsync(302, TaskExecutionStatus.Failed);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-leftovers-failed")));
        Assert.Contains("dpkg-query could not list the mail packages", cut.Find(".mail-leftovers-failed").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".mail-leftovers-clean"));
    }

    [Fact]
    public void R3_23_BackOnThePage_ARunningCheckIsFollowedAgain()
    {
        Tracker.Upsert(new ServerTaskDto { Id = 303, ServerId = 10, Name = MailTaskNames.LeftoversCheck, Status = TaskExecutionStatus.Running });

        var cut = RenderCheck();

        Assert.True(cut.Instance.Busy);
        Assert.Equal(303, cut.Instance.TaskId);
    }

    [Fact]
    public void R3_23_AnAgentTooOld_SaysSo_AndOffersNoButton()
    {
        var cut = RenderCheck(Server(canCheck: false));

        Assert.NotEmpty(cut.FindAll(".mail-leftovers-agent-old"));
        Assert.Empty(cut.FindAll("button.mail-leftovers-check"));
    }
}
