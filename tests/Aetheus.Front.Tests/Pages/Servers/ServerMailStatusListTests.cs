// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
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

    private IRenderedComponent<ServerMailStatusList> RenderList(MailDataDto? mail = null, bool canManage = true) =>
        Render<ServerMailStatusList>(p => p
            .Add(x => x.ServerId, 10)
            .Add(x => x.Mail, mail ?? Mail())
            .Add(x => x.CanManage, canManage)
            .Add(x => x.DefaultSender, "admin@example.com"));

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
    public void R2_090_OnlyTheFirstRedLine_ShowsItsFix_HereTheSpfRecordToCopy()
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

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<MailStatusFix>()));
        var fix = cut.FindComponent<MailStatusFix>();
        Assert.Equal("spf:example.com", fix.Instance.Item.Key);
        var code = fix.FindComponent<OmniCodeBlock>().Instance;
        Assert.Equal(("TXT example.com", "v=spf1 mx a ~all"), (code.Title, code.Code));
        Assert.Contains("MailFixTxt", fix.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("v=DMARC1", cut.Markup, StringComparison.Ordinal);
        // The fix sits right under its own line.
        var lines = cut.FindAll(".mail-status-lines > *").ToList();
        var fixIndex = lines.FindIndex(element => element.ClassList.Contains("mail-status-fix"));
        Assert.Contains("MailDiagSpf", lines[fixIndex - 1].TextContent, StringComparison.Ordinal);
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

        cut.FindAll("button").First(b => b.TextContent.Contains("MailRunServerCheck", StringComparison.Ordinal)).Click();
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

        cut.FindAll("button").First(b => b.TextContent.Contains("MailMeasureUsage", StringComparison.Ordinal)).Click();
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
}
