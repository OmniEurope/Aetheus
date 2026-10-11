// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using AngleSharp.Dom;
using Bunit;
using Bunit.Rendering;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R2-090: the mail page opens on a status list, each line green or red, the first red line
/// carrying its fix; PLAN-005 lots 6 and 7: each server-side check reads the output of the very task it queued.</summary>
public sealed class ServerMailStatusListTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    private static readonly MailDiagnosticItemDto[] AllGreen =
    [
        new() { Key = "installed", Verdict = MailCheckVerdict.Ok },
        new() { Key = "services", Verdict = MailCheckVerdict.Ok },
        new() { Key = "tls", Verdict = MailCheckVerdict.Ok },
        new() { Key = "queue", Verdict = MailCheckVerdict.Mismatch, Detail = "4" },
        new() { Key = "hostname-dns", Verdict = MailCheckVerdict.Ok, Detail = "mail.example.com" },
        new() { Key = "reverse-dns", Verdict = MailCheckVerdict.Ok, Detail = "203.0.113.25" },
        new() { Key = "mx:example.com", Verdict = MailCheckVerdict.Ok, Detail = "mail.example.com" },
        new() { Key = "spf:example.com", Verdict = MailCheckVerdict.Ok },
        new() { Key = "dkim:example.com", Verdict = MailCheckVerdict.Ok },
        new() { Key = "dmarc:example.com", Verdict = MailCheckVerdict.Ok }
    ];

    public ServerMailStatusListTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        StubDiagnostics(AllGreen);
    }

    private void StubDiagnostics(IEnumerable<MailDiagnosticItemDto> items) =>
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto
        {
            CheckedAt = new DateTime(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc),
            Items = [.. items]
        });

    /// <summary>The all-green list with the lines named in <paramref name="red"/> replaced.</summary>
    private void StubWithRed(params MailDiagnosticItemDto[] red) =>
        StubDiagnostics(AllGreen.Select(item => red.FirstOrDefault(r => r.Key == item.Key) ?? item));

    private static MailDataDto Mail(int helperVersion = ServerMailStatusList.CurrentHelperVersion) => new()
    {
        IsInstalled = true,
        IsManagedByAetheus = true,
        Hostname = "mail.example.com",
        HelperVersion = helperVersion,
        PostfixVersion = "3.7.11",
        DovecotVersion = "2.3.19"
    };

    private IRenderedComponent<ContainerFragment>? _host;
    private IRenderedComponent<ContainerFragment> Host => _host ?? throw new InvalidOperationException("Render the list first.");

    /// <summary>Recette R1-12: the list is rendered next to the header outlet its "..." menu goes to.</summary>
    private IRenderedComponent<ServerMailStatusList> RenderList(MailDataDto? mail = null, bool canManage = true)
    {
        _host = this.RenderWithOutlets<ServerMailStatusList>(new Dictionary<string, object?>
        {
            [nameof(ServerMailStatusList.ServerId)] = 10,
            [nameof(ServerMailStatusList.Mail)] = mail ?? Mail(),
            [nameof(ServerMailStatusList.CanManage)] = canManage,
            [nameof(ServerMailStatusList.DefaultSender)] = "admin@example.com"
        }, ServerMailReinstall.HeaderMenuSection);
        return Host.FindComponent<ServerMailStatusList>();
    }

    /// <summary>Opens the header's "..." menu of mail actions and returns its items.</summary>
    private IReadOnlyList<IElement> OpenMenu()
    {
        Host.Find(".mail-actions-menu .omni-overflow-menu__trigger").Click();
        return Host.FindAll(".omni-overflow-menu__popup .omni-menu__item");
    }

    private void ChooseMenuItem(string key) =>
        OpenMenu().First(item => item.TextContent.Contains(key, StringComparison.Ordinal)).Click();

    private int DiagnosticsReads => _handler.Requests.Count(r => r.Url.Contains("mail/diagnostics", StringComparison.Ordinal));

    private static IElement FixButton(IRenderedComponent<ServerMailStatusList> cut, string key) =>
        cut.FindAll(".mail-status-fix button").First(b => b.TextContent.Contains(key, StringComparison.Ordinal));

    private void StubTaskOutput(int taskId, params string[] lines) =>
        _handler.SetJsonResponse($"api/logs/task/{taskId}",
            lines.Select((line, index) => new TaskLogDto { Id = index + 1, TaskId = taskId, Message = line }).ToList());

    private static string[] LineStates(IRenderedComponent<ServerMailStatusList> cut) =>
        [.. cut.FindAll(".mail-status-lines .mail-check-line").Select(line => line.GetAttribute("data-state")!)];

    [Fact]
    public void R2_090_TheList_IsTheChecksThatMakeMailWork_InFixOrder_EachGreenOrRed()
    {
        StubWithRed(new MailDiagnosticItemDto { Key = "dkim:example.com", Verdict = MailCheckVerdict.Mismatch, Detail = "key-mismatch" });
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.Equal(8, LineStates(cut).Length));
        Assert.Equal(["services", "hostname-dns", "reverse-dns", "tls", "mx:example.com", "spf:example.com", "dkim:example.com", "dmarc:example.com"],
            cut.Instance.StatusItems.Select(item => item.Key).ToArray());
        // A mismatch is red like a missing record: there is no third colour.
        Assert.Equal(["ok", "ok", "ok", "ok", "ok", "ok", "fail", "ok"], LineStates(cut));
        Assert.Contains("MailDiagDkim", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Postfix 3.7.11", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_9_EveryRedLine_ShowsItsFix_UnderItself_WithItsRecordToCopy()
    {
        StubWithRed(
            new MailDiagnosticItemDto
            {
                Key = "spf:example.com",
                Verdict = MailCheckVerdict.Missing,
                RecordName = "example.com",
                RecordType = "TXT",
                RecordValue = "v=spf1 mx a ~all"
            },
            new MailDiagnosticItemDto
            {
                Key = "dmarc:example.com",
                Verdict = MailCheckVerdict.Missing,
                RecordName = "_dmarc.example.com",
                RecordType = "TXT",
                RecordValue = "v=DMARC1; p=quarantine"
            });
        var cut = RenderList();

        // Recette R1-9: not only the first red line; DMARC, after SPF, gets its card too.
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindComponents<MailStatusFix>().Count));
        var fixes = cut.FindComponents<MailStatusFix>();
        Assert.Equal(["spf:example.com", "dmarc:example.com"], fixes.Select(f => f.Instance.Item.Key).ToArray());
        var spfCode = fixes[0].FindComponent<OmniCodeBlock>().Instance;
        Assert.Equal(("TXT example.com", "v=spf1 mx a ~all"), (spfCode.Title, spfCode.Code));
        Assert.Contains("MailFixTxt", fixes[0].Markup, StringComparison.Ordinal);
        Assert.Contains("MailFixWhatSpf", fixes[0].Markup, StringComparison.Ordinal);
        Assert.Contains("MailFixEffectSpf", fixes[0].Markup, StringComparison.Ordinal);
        Assert.Equal("v=DMARC1; p=quarantine", fixes[1].FindComponent<OmniCodeBlock>().Instance.Code);
        Assert.Contains("MailFixEffectDmarc", fixes[1].Markup, StringComparison.Ordinal);
        // Each fix sits right under its own line.
        var lines = cut.FindAll(".mail-status-lines > *").ToList();
        var fixIndexes = lines.Select((element, index) => (element, index))
            .Where(pair => pair.element.ClassList.Contains("mail-status-fix")).Select(pair => pair.index).ToList();
        Assert.Contains("MailDiagSpf", lines[fixIndexes[0] - 1].TextContent, StringComparison.Ordinal);
        Assert.Contains("MailDiagDmarc", lines[fixIndexes[1] - 1].TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_11_TheDnsHelp_IsOnThePage_Folded_ForTheCheckedDomain()
    {
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<MailDnsHelp>()));
        Assert.Equal("example.com", cut.FindComponent<MailDnsHelp>().Instance.Domain);
        Assert.False(cut.Find(".mail-dns-help").HasAttribute("open"));
    }

    [Fact]
    public void R1_11_WithoutMailDomain_NoDnsHelp()
    {
        StubDiagnostics(AllGreen.Where(item => !item.Key.Contains(':', StringComparison.Ordinal)));
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-check-line")));
        Assert.Empty(cut.FindComponents<MailDnsHelp>());
    }

    [Fact]
    public void R2_090_TheARecord_ComesBeforeTheCertificateThatNeedsIt()
    {
        StubWithRed(
            new MailDiagnosticItemDto { Key = "tls", Verdict = MailCheckVerdict.Missing },
            new MailDiagnosticItemDto
            {
                Key = "hostname-dns",
                Verdict = MailCheckVerdict.Missing,
                Detail = "mail.example.com",
                RecordName = "mail.example.com",
                RecordType = "A",
                RecordValue = "203.0.113.25"
            });
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.Equal("hostname-dns", cut.FindComponent<MailStatusFix>().Instance.Item.Key));
        Assert.Equal("A mail.example.com", cut.FindComponent<OmniCodeBlock>().Instance.Title);
    }

    [Fact]
    public void R2_090_StoppedServices_AreStartedByTheirFix()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/action", new MailTaskQueuedDto { TaskId = 50 });
        StubWithRed(new MailDiagnosticItemDto { Key = "services", Verdict = MailCheckVerdict.Mismatch, Detail = "postfix,opendkim" });
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.Contains("MailStartServices", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".mail-status-fix button").Single(b => b.TextContent.Contains("MailStartServices", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Equal(2, _handler.RequestDetails.Count(r => r.Method == "POST" && r.Url.EndsWith("mail/action", StringComparison.Ordinal))));
        var bodies = _handler.RequestDetails.Where(r => r.Url.EndsWith("mail/action", StringComparison.Ordinal)).Select(r => r.Body!).ToList();
        Assert.Contains(bodies, b => b.Contains($"\"action\":{(int)MailAction.StartPostfix}", StringComparison.Ordinal));
        Assert.Contains(bodies, b => b.Contains($"\"action\":{(int)MailAction.RestartOpenDkim}", StringComparison.Ordinal));
    }

    [Fact]
    public void R2_090_AMissingCertificate_IsObtainedFromItsFix_WithTheAcmeEmail()
    {
        _dialog.OpenResult = new MailDialogModel { Mode = MailDialogMode.RequestCertificate, Email = "ops@example.com" };
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/certificate", new MailTaskQueuedDto { TaskId = 71 });
        StubWithRed(new MailDiagnosticItemDto { Key = "tls", Verdict = MailCheckVerdict.Mismatch, Detail = "self-signed" });
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.Contains("MailDetailSelfSigned", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".mail-status-fix button").First(b => b.TextContent.Contains("MailObtainCertificate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Method == "POST" && r.Url.EndsWith("mail/certificate", StringComparison.Ordinal)
            && r.Body!.Contains("ops@example.com", StringComparison.Ordinal)));
    }

    [Fact]
    public void R1_6_ABlankAcmeEmail_RequestsTheCertificateWithoutAContact()
    {
        _dialog.OpenResult = new MailDialogModel { Mode = MailDialogMode.RequestCertificate, Email = "   " };
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/certificate", new MailTaskQueuedDto { TaskId = 72 });
        StubWithRed(new MailDiagnosticItemDto { Key = "tls", Verdict = MailCheckVerdict.Mismatch, Detail = "self-signed" });
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.Contains("MailDetailSelfSigned", cut.Markup, StringComparison.Ordinal));
        cut.FindAll(".mail-status-fix button").First(b => b.TextContent.Contains("MailObtainCertificate", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Method == "POST" && r.Url.EndsWith("mail/certificate", StringComparison.Ordinal)
            && r.Body!.Contains("\"email\":null", StringComparison.Ordinal)));
    }

    [Fact]
    public void R2_090_AllGreen_OffersTheEndToEndTest_AsTheMainAction()
    {
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-status-all-green")));
        Assert.Empty(cut.FindComponents<MailStatusFix>());
        var test = cut.FindComponents<OmniButton>().Single(b => b.Find("button").TextContent.Contains("MailSendTest", StringComparison.Ordinal));
        Assert.Equal(OmniButtonVariant.Primary, test.Instance.Variant);
    }

    [Fact]
    public void AdoptedStack_ShowsOwnershipHelperWarningAndFindings()
    {
        var cut = RenderList(Mail(helperVersion: 1) with { IsManagedByAetheus = false, Diagnostics = ["aliases-skipped|2"] });

        Assert.Contains("MailAdoptedStack", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("MailHelperOutdated", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("MailFindingAliasesSkipped", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ReadOnlyUsers_GetNoActions()
    {
        StubWithRed(new MailDiagnosticItemDto { Key = "services", Verdict = MailCheckVerdict.Mismatch, Detail = "postfix" });
        var cut = RenderList(Mail(helperVersion: 1), canManage: false);

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<MailStatusFix>()));
        Assert.Empty(cut.FindAll("button"));
        Assert.DoesNotContain("MailHelperOutdated", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServerCheck_ReadsTheCheckLinesOfItsOwnTask()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/action", new MailTaskQueuedDto { TaskId = 41 });
        StubTaskOutput(41, "CHECK\tpostfix\tok\t", "CHECK\tport-587\tfail\tnot listening");
        var cut = RenderList();

        ChooseMenuItem("MailRunServerCheck");
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Method == "POST" && r.Url.EndsWith("mail/action", StringComparison.Ordinal)
            && r.Body!.Contains($"\"action\":{(int)MailAction.TestConfig}", StringComparison.Ordinal)));

        // A completion of another task must be ignored.
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(new TaskCompletedNotification { TaskId = 7, ServerId = 10 }));
        Assert.Empty(cut.Instance.Checks);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 41, ServerId = 10, Status = TaskExecutionStatus.Failed }));

        Assert.Equal(2, cut.Instance.Checks.Count);
        Assert.False(cut.Instance.Checks.Single(c => c.Component == "port-587").IsOk);
    }

    [Fact]
    public async Task DeliveryTest_ShowsTheDeliveryLineOfTheHelper()
    {
        _dialog.OpenResult = new MailDialogModel { Mode = MailDialogMode.TestDelivery, Source = "admin@example.com", Destination = "bob@example.org" };
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/test-delivery", new MailTaskQueuedDto { TaskId = 42 });
        StubTaskOutput(42, "some chatter", "DELIVERY\tsent\t68436587DA\taetheus-test-1-2\tstatus=sent (250 2.0.0 Saved)");
        var cut = RenderList();

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-status-all-green")));
        cut.FindAll("button").First(b => b.TextContent.Contains("MailSendTest", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, r =>
            r.Url.EndsWith("mail/test-delivery", StringComparison.Ordinal) && r.Body!.Contains("bob@example.org", StringComparison.Ordinal)));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 42, ServerId = 10, Status = TaskExecutionStatus.Success }));

        Assert.NotNull(cut.Instance.Delivery);
        Assert.True(cut.Instance.Delivery.IsSent);
        Assert.Contains("MailDeliverySent", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UsageMeasurement_IngestsTheCompletedReport()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/quota/refresh", new MailTaskQueuedDto { TaskId = 43 });
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/quota/ingest/43", 2);
        var cut = RenderList();

        ChooseMenuItem("MailMeasureUsage");
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Url.EndsWith("mail/quota/refresh", StringComparison.Ordinal)));
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 43, ServerId = 10, Status = TaskExecutionStatus.Success }));

        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.EndsWith("mail/quota/ingest/43", StringComparison.Ordinal));
    }

    [Fact]
    public async Task R181_NoRefreshButton_TheStatusFollowsTheServersPushes()
    {
        StubWithRed(new MailDiagnosticItemDto { Key = "tls", Verdict = MailCheckVerdict.Mismatch, Detail = "self-signed" });
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.Contains("MailDetailSelfSigned", cut.Markup, StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindAll("button"), b => b.TextContent.Contains("Refresh", StringComparison.Ordinal));
        StubDiagnostics(AllGreen);

        // Reading the status queues no task, so a completed task may reload it as a heartbeat does.
        await cut.Instance.LiveFeed!.OnHeartbeatAsync(10);
        cut.WaitForAssertion(() => Assert.DoesNotContain("MailDetailSelfSigned", cut.Markup, StringComparison.Ordinal));
        var afterHeartbeat = _handler.Requests.Count(r => r.Url.Contains("mail/diagnostics", StringComparison.Ordinal));

        await cut.Instance.LiveFeed.OnTaskCompletedAsync(new TaskCompletedNotification { TaskId = 5, ServerId = 10 });
        await cut.Instance.LiveFeed.OnHeartbeatAsync(11);

        Assert.Equal(afterHeartbeat + 1,
            _handler.Requests.Count(r => r.Url.Contains("mail/diagnostics", StringComparison.Ordinal)));
    }

    // ── Recette R1-7 / R1-8 / R1-13: every task the page queues is followed until it completes ──────

    [Fact]
    public async Task R1_7_ObtainTls_KeepsItsTaskId_StaysBusyUntilItCompletes_ThenTheStateIsReadAtOnce()
    {
        _dialog.OpenResult = new MailDialogModel { Mode = MailDialogMode.RequestCertificate, Email = "ops@example.com" };
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/certificate", new MailTaskQueuedDto { TaskId = 71 });
        StubWithRed(new MailDiagnosticItemDto { Key = "tls", Verdict = MailCheckVerdict.Mismatch, Detail = "self-signed" });
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.Contains("MailDetailSelfSigned", cut.Markup, StringComparison.Ordinal));

        FixButton(cut, "MailObtainCertificate").Click();

        cut.WaitForAssertion(() => Assert.True(cut.Instance.RunningTasks.ContainsKey(71)));
        Assert.Equal(ServerMailStatusList.MailTaskKind.ObtainCertificate, cut.Instance.RunningTasks[71].Kind);
        // Busy, not disabled: the button keeps its look and refuses a second click.
        Assert.Equal("true", FixButton(cut, "MailObtainCertificate").GetAttribute("aria-busy"));
        Assert.False(FixButton(cut, "MailObtainCertificate").HasAttribute("disabled"));
        Assert.True(FixButton(cut, "MailInstallCertificate").HasAttribute("disabled"));
        FixButton(cut, "MailObtainCertificate").Click();
        Assert.Equal(1, _handler.Requests.Count(r => r.Method == "POST" && r.Url.EndsWith("mail/certificate", StringComparison.Ordinal)));
        // Recette R1-13: one banner at the top names the running task.
        Assert.Contains("MailTaskRunning", Host.Find(".mail-task-running").TextContent, StringComparison.Ordinal);
        Assert.Single(Host.FindAll(".mail-task-running .mail-task-running__indicator"));
        Assert.DoesNotContain("MailWaitingForTask", Host.Markup, StringComparison.Ordinal);

        // Another task's completion changes nothing.
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(new TaskCompletedNotification { TaskId = 9, ServerId = 10 }));
        Assert.True(cut.Instance.RunningTasks.ContainsKey(71));

        StubDiagnostics(AllGreen);
        var readsBefore = DiagnosticsReads;
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 71, ServerId = 10, Status = TaskExecutionStatus.Success }));

        // R1-8: the state is read again at the completion, no heartbeat awaited.
        Assert.Equal(readsBefore + 1, DiagnosticsReads);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindComponents<MailStatusFix>()));
        Assert.Empty(cut.Instance.RunningTasks);
        Assert.Empty(Host.FindAll(".mail-task-running"));
        Assert.Null(cut.Instance.Failure);
    }

    [Fact]
    public async Task R1_7_AFailedInstallTls_ShowsItsOutcome_AndFreesItsButton()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/certificate/install", new MailTaskQueuedDto { TaskId = 73 });
        StubTaskOutput(73, "Installing", "postfix: certificate file not found");
        StubWithRed(new MailDiagnosticItemDto { Key = "tls", Verdict = MailCheckVerdict.Mismatch, Detail = "self-signed" });
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.Contains("MailDetailSelfSigned", cut.Markup, StringComparison.Ordinal));

        FixButton(cut, "MailInstallCertificate").Click();
        cut.WaitForAssertion(() => Assert.Equal("true", FixButton(cut, "MailInstallCertificate").GetAttribute("aria-busy")));
        Assert.True(cut.Instance.IsBusy(ServerMailStatusList.MailTaskKind.InstallCertificate));

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 73, ServerId = 10, Status = TaskExecutionStatus.Failed }));

        cut.WaitForAssertion(() => Assert.Contains("MailTaskFailed", cut.Find(".mail-task-failed").TextContent, StringComparison.Ordinal));
        // The last log line is read for the message.
        Assert.Contains(_handler.Requests, r => r.Url.EndsWith("api/logs/task/73", StringComparison.Ordinal));
        Assert.Null(FixButton(cut, "MailInstallCertificate").GetAttribute("aria-busy"));
        Assert.Empty(Host.FindAll(".mail-task-running"));
    }

    [Fact]
    public async Task R1_7_OpenDkimRestart_FromTheMenu_IsFollowedLikeTheOtherTasks()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/action", new MailTaskQueuedDto { TaskId = 60 });
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-status-all-green")));

        ChooseMenuItem("MailRestartOpenDkim");

        cut.WaitForAssertion(() => Assert.Equal(ServerMailStatusList.MailTaskKind.OpenDkim, cut.Instance.RunningTasks[60].Kind));
        Assert.Contains("MailTaskRunning", Host.Find(".mail-task-running").TextContent, StringComparison.Ordinal);
        var item = OpenMenu().First(i => i.TextContent.Contains("MailRestartOpenDkim", StringComparison.Ordinal));
        Assert.True(item.HasAttribute("disabled") || item.GetAttribute("aria-disabled") == "true");

        var readsBefore = DiagnosticsReads;
        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 60, ServerId = 10, Status = TaskExecutionStatus.Success }));

        Assert.Equal(readsBefore + 1, DiagnosticsReads);
        Assert.Empty(cut.Instance.RunningTasks);
        Assert.Empty(Host.FindAll(".mail-task-running"));
    }

    [Fact]
    public async Task R1_7_APostfixAction_KeepsItsSplitButtonBusyUntilTheTaskCompletes()
    {
        _handler.SetJsonResponse(System.Net.Http.HttpMethod.Post, "api/servers/10/mail/action", new MailTaskQueuedDto { TaskId = 61 });
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-status-all-green")));
        var postfix = cut.FindComponents<OmniSplitButton>().Single(b => b.Instance.Text == "Postfix");

        postfix.Find("button").Click();

        cut.WaitForAssertion(() => Assert.True(postfix.Instance.Busy));
        Assert.Equal(ServerMailStatusList.MailTaskKind.Postfix, cut.Instance.RunningTasks[61].Kind);
        Assert.False(cut.FindComponents<OmniSplitButton>().Single(b => b.Instance.Text == "Dovecot").Instance.Busy);

        await cut.InvokeAsync(() => cut.Instance.HandleTaskCompletedAsync(
            new TaskCompletedNotification { TaskId = 61, ServerId = 10, Status = TaskExecutionStatus.Success }));
        Assert.False(postfix.Instance.Busy);
    }

    [Fact]
    public void R1_13_NoTaskRunning_NoBannerAndNoWaitingIndicator()
    {
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".mail-status-all-green")));

        Assert.Empty(Host.FindAll(".mail-task-running"));
        Assert.DoesNotContain("MailWaitingForTask", Host.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_12_TheFourActions_AreTheHeaderMenu_PostfixAndDovecotStayInThePage()
    {
        StubWithRed(new MailDiagnosticItemDto { Key = "dkim:example.com", Verdict = MailCheckVerdict.Mismatch, Detail = "key-mismatch" });
        var cut = RenderList();
        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<MailStatusFix>()));

        // The menu is sent to the header outlet, not drawn in the card.
        Assert.Empty(cut.FindAll(".mail-status .mail-actions-menu"));
        Assert.Single(Host.FindAll("[data-outlet=\"mail-header-menu\"] .mail-actions-menu"));
        var trigger = Host.Find(".mail-actions-menu .omni-overflow-menu__trigger");
        Assert.Equal("MoreActions", trigger.GetAttribute("aria-label"));
        var items = OpenMenu().Select(i => i.TextContent.Trim()).ToList();
        Assert.Equal(["MailRestartOpenDkim", "MailRunServerCheck", "MailSendTest", "MailMeasureUsage"], items);

        var tools = cut.Find(".mail-status-tools");
        foreach (var key in new[] { "MailRestartOpenDkim", "MailRunServerCheck", "MailSendTest", "MailMeasureUsage" })
            Assert.DoesNotContain(key, tools.TextContent, StringComparison.Ordinal);
        Assert.Equal(["Postfix", "Dovecot"], cut.FindComponents<OmniSplitButton>().Select(b => b.Instance.Text).ToArray());
    }

    [Fact]
    public void R1_12_ReadOnlyUsers_GetNoMenu()
    {
        RenderList(canManage: false);

        Assert.Empty(Host.FindAll(".mail-actions-menu"));
    }
}
