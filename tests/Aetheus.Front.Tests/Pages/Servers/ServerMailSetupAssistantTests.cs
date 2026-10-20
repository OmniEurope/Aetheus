// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The setup assistant validates prerequisites before Configure and supplies field
/// help.</summary>
public sealed class ServerMailSetupAssistantTests : BunitContext
{
    private const string ServerIp = "203.0.113.25";
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerMailSetupAssistantTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static ServerDetailDto Server(bool canProbe = true, bool setupAvailable = true) =>
        ServerTestData.MakeMailServer(installed: false) with
        {
            IpAddress = ServerIp,
            MailSetupAvailable = setupAvailable,
            AgentCapabilities = canProbe ? [AgentCapabilities.MailOutboundProbe, AgentCapabilities.ShellExecution] : [AgentCapabilities.ShellExecution]
        };

    private IRenderedComponent<ServerMailSetupAssistant> RenderAssistant(ServerDetailDto? server = null) =>
        Render<ServerMailSetupAssistant>(p => p.Add(x => x.Server, server ?? Server()).Add(x => x.ServerId, 10));

    private void StubDns(MailCheckVerdict a, MailCheckVerdict ptr, string host = "mx.example.com") =>
        _handler.SetJsonResponse($"api/servers/10/mail/prerequisites?hostname={host}", new MailDiagnosticsDto
        {
            Items =
            [
                new() { Key = "hostname-dns", Verdict = a, Detail = host, RecordName = host, RecordType = "A", RecordValue = ServerIp },
                new() { Key = "reverse-dns", Verdict = ptr, Detail = ServerIp, RecordName = ServerIp, RecordType = "PTR", RecordValue = host }
            ]
        });

    private void StubProbe(int taskId, string checkLine)
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/prerequisites/outbound-smtp", new MailTaskQueuedDto { TaskId = taskId });
        _handler.SetJsonResponse($"api/logs/task/{taskId}", new List<TaskLogDto> { new() { Id = 1, TaskId = taskId, Message = checkLine } });
    }

    private static void Click(IRenderedComponent<ServerMailSetupAssistant> cut, string text) =>
        cut.FindAll("button").First(b => b.TextContent.Contains(text, StringComparison.Ordinal) && !b.HasAttribute("disabled")).Click();

    private static bool IsDisabled(IRenderedComponent<ServerMailSetupAssistant> cut, string cssClass) =>
        cut.Find($"button.{cssClass}").HasAttribute("disabled");

    [Fact]
    public void R2_080_TheSteps_AreTheExplainedOrder_DomainARecordReverseDnsPort25ThenConfiguration()
    {
        var cut = RenderAssistant();

        Assert.Equal(["MailStepDomain", "MailStepARecord", "MailStepReverseDns", "MailStepOutbound", "MailStepConfigure"],
            cut.FindComponents<OmniWizardStep>().Select(step => step.Instance.Title).ToArray());
        Assert.Equal("MailConfigure", cut.FindComponent<OmniWizard>().Instance.FinishText);
        Assert.Contains("MailStepDomainText", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_080_TheDomain_PrefillsTheHostAndTheAdminAddress_UntilTheyAreChanged()
    {
        var cut = RenderAssistant();

        cut.Find("#mail-setup-domain").Input("example.com");

        Assert.Equal("mx.example.com", cut.Find("#mail-setup-hostname").GetAttribute("value"));
        Assert.Equal("admin@example.com", cut.Instance.Model.Email);

        cut.Find("#mail-setup-hostname").Input("smtp.example.com");
        cut.Find("#mail-setup-domain").Input("example.org");

        Assert.Equal("smtp.example.com", cut.Find("#mail-setup-hostname").GetAttribute("value"));
        Assert.Equal("admin@example.org", cut.Instance.Model.Email);
    }

    [Fact]
    public void R_512_EveryFieldExplainsItself_AndTheNoDnsChangeNoteIsGone()
    {
        var cut = RenderAssistant();

        Assert.Equal(["MailDomainHelp", "MailHostnameHelp", "MailDkimSelectorHelp"],
            cut.FindComponents<OmniFormField>().Select(field => field.Instance.Help).ToArray());
        Assert.DoesNotContain("MailSetupDoesNotMoveMail", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_060_Next_RefusesABareHostName_AndStaysOnTheStep()
    {
        var cut = RenderAssistant();
        cut.Find("#mail-setup-domain").Input("example.com");
        cut.Find("#mail-setup-hostname").Input("vps2577917");

        cut.Find("button.omni-wizard__next").Click();

        Assert.Equal(ServerMailSetupAssistant.StepDomain, cut.Instance.Step);
        Assert.Contains("MailHostnameInvalid", cut.Find(".mail-setup-step-errors").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R2_080_TheARecordStep_ChecksTheHostAtOnce_AndBlocksNextWhileItIsRed()
    {
        StubDns(MailCheckVerdict.Mismatch, MailCheckVerdict.Ok);
        var cut = RenderAssistant();
        cut.Find("#mail-setup-domain").Input("example.com");

        cut.Find("button.omni-wizard__next").Click();

        cut.WaitForAssertion(() => Assert.Equal("fail", cut.Find(".mail-prereq-a").GetAttribute("data-state")));
        Assert.Equal(ServerMailSetupAssistant.StepARecord, cut.Instance.Step);
        Assert.Contains(_handler.Requests, r => r.Url.Contains("mail/prerequisites?hostname=mx.example.com", StringComparison.Ordinal));
        Assert.Equal(("A mx.example.com", ServerIp),
            (cut.FindComponent<OmniCodeBlock>().Instance.Title, cut.FindComponent<OmniCodeBlock>().Instance.Code));
        Assert.True(IsDisabled(cut, "omni-wizard__next"));

        StubDns(MailCheckVerdict.Ok, MailCheckVerdict.Ok);
        Click(cut, "MailCheckPrerequisite");

        cut.WaitForAssertion(() => Assert.Equal("ok", cut.Find(".mail-prereq-a").GetAttribute("data-state")));
        Assert.False(IsDisabled(cut, "omni-wizard__next"));
    }

    [Fact]
    public async Task R2_080_Configure_StaysUnavailable_UntilTheAgentReachesPort25()
    {
        StubDns(MailCheckVerdict.Ok, MailCheckVerdict.Ok);
        StubProbe(61, "CHECK\toutbound-25\tfail\tconnection failed: TimedOut");
        var cut = RenderAssistant();
        cut.Find("#mail-setup-domain").Input("example.com");
        cut.Find("button.omni-wizard__next").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.ARecordOk));
        cut.Find("button.omni-wizard__next").Click();
        cut.Find("button.omni-wizard__next").Click();
        Assert.Equal(ServerMailSetupAssistant.StepOutbound, cut.Instance.Step);

        Click(cut, "MailCheckPrerequisite");
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r =>
            r.Method == "POST" && r.Url.EndsWith("mail/prerequisites/outbound-smtp", StringComparison.Ordinal)));
        // Another task's completion is not the probe's.
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(new TaskCompletedNotification { TaskId = 3, ServerId = 10 }));
        Assert.Equal("unchecked", cut.Find(".mail-prereq-outbound").GetAttribute("data-state"));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 61, ServerId = 10, Status = TaskExecutionStatus.Failed }));

        Assert.Equal("fail", cut.Find(".mail-prereq-outbound").GetAttribute("data-state"));
        Assert.Contains("TimedOut", cut.Markup, StringComparison.Ordinal);
        Assert.True(IsDisabled(cut, "omni-wizard__next"));
        Assert.False(cut.Instance.CanConfigure);
    }

    [Fact]
    public async Task R2_080_R2_085_AllThreeGreen_ConfigurePostsTheSetup_OnceEvenOnADoubleClick()
    {
        StubDns(MailCheckVerdict.Ok, MailCheckVerdict.Ok);
        StubProbe(62, "CHECK\toutbound-25\tok\t220 mx.google.com ESMTP");
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/setup", new MailTaskQueuedDto { TaskId = 90 });
        var cut = RenderAssistant();
        cut.Find("#mail-setup-domain").Input("example.com");
        cut.Find("button.omni-wizard__next").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.ARecordOk));
        cut.Find("button.omni-wizard__next").Click();
        cut.Find("button.omni-wizard__next").Click();
        Click(cut, "MailCheckPrerequisite");
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.EndsWith("outbound-smtp", StringComparison.Ordinal)));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 62, ServerId = 10, Status = TaskExecutionStatus.Success }));
        cut.Find("button.omni-wizard__next").Click();
        Assert.Equal(ServerMailSetupAssistant.StepConfigure, cut.Instance.Step);
        cut.Find("#mail-setup-password").Input("a-long-enough-Passw0rd");

        cut.Find("button.omni-wizard__finish").Click();
        cut.Find("button.omni-wizard__finish").Click();

        cut.WaitForAssertion(() => Assert.Single(_handler.RequestDetails, r => r.Method == "POST" && r.Url.EndsWith("mail/setup", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Single(r => r.Url.EndsWith("mail/setup", StringComparison.Ordinal)).Body!;
        Assert.Contains("\"hostname\":\"mx.example.com\"", body, StringComparison.Ordinal);
        Assert.Contains("\"adminEmail\":\"admin@example.com\"", body, StringComparison.Ordinal);
        // While the setup runs, Configure starts nothing.
        Assert.True(cut.Instance.SetupBusy);
        Assert.True(IsDisabled(cut, "omni-wizard__finish"));
        Assert.NotEmpty(cut.FindAll(".mail-setup-running"));

        // The setup task fails: the page says so, and one click may launch a setup again.
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 90, ServerId = 10, Status = TaskExecutionStatus.Failed }));
        Assert.False(cut.Instance.SetupBusy);
        Assert.NotEmpty(cut.FindAll(".mail-setup-failed"));
        Assert.False(IsDisabled(cut, "omni-wizard__finish"));
    }

    private TaskTrackerService Tracker => Services.GetRequiredService<TaskTrackerService>();

    [Fact]
    public void R3_45_WhileTheSetupRuns_TheVeilLiesOverTheWholeAssistant_AndNoLoaderIsAdded()
    {
        Tracker.Upsert(new ServerTaskDto { Id = 91, ServerId = 10, Name = "Mail - full setup (example.com)", Status = TaskExecutionStatus.Running });

        var cut = RenderAssistant();

        var veil = cut.Find(".mail-setup-veil");
        Assert.Contains("omni-busy", veil.ClassList);
        Assert.Equal("true", veil.GetAttribute("aria-busy"));
        Assert.NotNull(veil.QuerySelector(".omni-wizard"));
        Assert.Empty(cut.FindComponents<AetheusLoader>());
    }

    [Fact]
    public void R3_45_WithoutASetup_TheAssistantIsNotVeiled()
    {
        var cut = RenderAssistant();

        var veil = cut.Find(".mail-setup-veil");
        Assert.DoesNotContain("omni-busy", veil.ClassList);
        Assert.Equal("false", veil.GetAttribute("aria-busy"));
        Assert.Empty(cut.FindAll(".mail-setup-running"));
    }

    [Fact]
    public async Task R3_46_BackOnThePage_TheOpenSetupIsFollowed_SaysItContinues_AndItsFailureIsShown()
    {
        // Another server's setup and a finished one are not this assistant's.
        Tracker.Upsert(new ServerTaskDto { Id = 80, ServerId = 11, Name = "Mail - full setup (other.org)", Status = TaskExecutionStatus.Running });
        var cut = RenderAssistant();
        Assert.False(cut.Instance.SetupBusy);

        // The live feed lists the setup queued before the user left: the assistant binds to it.
        await cut.InvokeAsync(() => Tracker.Upsert(new ServerTaskDto { Id = 92, ServerId = 10, Name = "Mail - full setup (example.com)", Status = TaskExecutionStatus.Pending }));

        cut.WaitForAssertion(() => Assert.True(cut.Instance.SetupBusy));
        Assert.True(cut.Instance.OwnsSetupTask(92));
        var running = cut.Find(".mail-setup-running");
        Assert.Contains("MailSetupContinuesInBackground", running.TextContent, StringComparison.Ordinal);
        Assert.Contains("/tasks/92", running.InnerHtml, StringComparison.Ordinal);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 92, ServerId = 10, Status = TaskExecutionStatus.Failed }));
        Assert.False(cut.Instance.SetupBusy);
        Assert.Equal(0, cut.Instance.Step);
        Assert.NotEmpty(cut.FindAll(".mail-setup-failed"));
    }

    [Fact]
    public void R2_080_AnAgentTooOldForThePort25Check_SaysSo_InsteadOfQueueing()
    {
        StubDns(MailCheckVerdict.Ok, MailCheckVerdict.Ok);
        var cut = RenderAssistant(Server(canProbe: false));
        cut.Find("#mail-setup-domain").Input("example.com");
        cut.Find("button.omni-wizard__next").Click();
        cut.WaitForAssertion(() => Assert.True(cut.Instance.ARecordOk));
        cut.Find("button.omni-wizard__next").Click();
        cut.Find("button.omni-wizard__next").Click();
        Assert.Equal(ServerMailSetupAssistant.StepOutbound, cut.Instance.Step);

        Assert.Contains("MailOutboundNeedsAgentUpdate", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("MailCheckPrerequisite", StringComparison.Ordinal));
    }
}
