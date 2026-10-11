// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerMailSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerMailSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
    }

    private static ServerDetailDto MakeMailServer(bool installed = true, bool postfixRunning = true, bool dovecotRunning = true) =>
        ServerTestData.MakeMailServer(installed, postfixRunning, dovecotRunning);

    private static ServerDetailDto MakeNotInstalledServer() => MakeMailServer(installed: false);

    private void SetupMailApiResponses()
    {
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items =
            [
                new() { Id = 1, Name = "example.com", IsActive = true, DkimSelector = "default", HasSpf = true, HasDkim = true },
                new() { Id = 2, Name = "test.org", IsActive = false, DkimSelector = "mail" }
            ],
            TotalCount = 2
        });
        _handler.SetJsonResponse("api/servers/10/mail/accounts", new PaginatedResult<MailAccountDto>
        {
            Items = [new() { Id = 1, Email = "admin@example.com", IsActive = true, QuotaMb = 1024 }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/10/mail/aliases", new PaginatedResult<MailAliasDto>());
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto());
    }

    private IRenderedComponent<ServerMailSection> RenderSection(ServerDetailDto? server = null)
    {
        SetupMailApiResponses();
        var s = server ?? MakeMailServer();
        return Render<ServerMailSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, s.Id));
    }

    // --- Rendering tests ---

    [Fact]
    public void Renders_Installed_ShowsTabsAndDomainGrid()
    {
        var cut = RenderSection();

        var markup = cut.Markup;
        Assert.Contains("MailDomains", markup);
        Assert.Contains("MailAccounts", markup);
        Assert.Contains("MailQueue", markup);
        Assert.Contains("Logs", markup);
    }

    [Fact]
    public void Renders_DomainList_ShowsDomainNames()
    {
        var cut = RenderSection();

        cut.WaitForAssertion(() =>
        {
            var markup = cut.Markup;
            Assert.Contains("example.com", markup);
            Assert.Contains("test.org", markup);
        });
    }

    // --- State manipulation via reflection ---

    [Fact]
    public void HandleTaskCompleted_RefreshesData()
    {
        var cut = RenderSection();

        // The installed-server path re-runs LoadDataAsync, which re-GETs the mail domains list.
        // Clear the initial-render requests so we observe only the refresh-triggered fetch.
        _handler.Requests.Clear();
        cut.InvokeAsync(() => cut.Instance.HandleTaskCompleted(new TaskCompletedNotification { TaskId = 1, ServerId = 10 }));

        cut.WaitForAssertion(() =>
            Assert.Contains(_handler.Requests, r => r.Method == "GET" && r.Url.Contains("api/servers/10/mail/domains")),
            TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ShowConfirm_OpensNativeConfirmation()
    {
        var cut = RenderSection();

        var method = typeof(ServerMailSection).GetMethod("ShowConfirm", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["Title", "Message", (Func<Task>)(() => Task.CompletedTask)])!);
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

        Assert.Equal("Title", dialog.LastTitle);
        Assert.Equal("Message", dialog.LastConfirmMessage);
    }

    [Fact]
    public async Task ShowConfirm_Accepted_ExecutesAction()
    {
        var cut = RenderSection();
        var executed = false;

        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        dialog.ConfirmResult = true;
        var method = typeof(ServerMailSection).GetMethod("ShowConfirm", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["Title", "Message", (Func<Task>)(() => { executed = true; return Task.CompletedTask; })])!);

        Assert.True(executed);
    }

    [Fact]
    public async Task ShowConfirm_Cancelled_DoesNotExecuteAction()
    {
        var cut = RenderSection();

        var executed = false;
        var method = typeof(ServerMailSection).GetMethod("ShowConfirm", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await cut.InvokeAsync(() => (Task)method.Invoke(cut.Instance, ["Title", "Message", (Func<Task>)(() => { executed = true; return Task.CompletedTask; })])!);

        Assert.False(executed);
    }

    [Fact]
    public async Task ShowDnsRecordsAsync_SetsDnsRecordsAndShowsDialog()
    {
        var dns = new MailDnsRecordsDto
        {
            Domain = "example.com",
            MxRecord = "10 mail.example.com.",
            SpfRecord = "v=spf1 mx a ~all",
            DkimSelector = "default",
            DmarcRecord = "v=DMARC1; p=quarantine"
        };
        _handler.SetJsonResponse("api/servers/10/mail/domains/1/dns", dns);
        var cut = RenderSection();

        var method = typeof(ServerMailSection).GetMethod("ShowDnsRecordsAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await (Task)method.Invoke(cut.Instance, [new MailDomainDto { Id = 1, Name = "example.com" }])!;

        var dnsField = typeof(ServerMailSection).GetField("_dnsRecords", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.NotNull(dnsField.GetValue(cut.Instance));
        var dialog = (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        Assert.Equal(typeof(MailOperationDialog), dialog.LastComponent);
        Assert.Equal(MailDialogMode.DnsRecords, dialog.LastParameters!["Mode"]);
        Assert.Same(dnsField.GetValue(cut.Instance), dialog.LastParameters["DnsRecords"]);
    }

    // --- Recette R2-080 / R2-090: what the page opens on ---

    [Fact]
    public void R2_080_AStackNotInstalled_OpensOnTheSetupAssistant_WithoutTabs()
    {
        var cut = RenderSection(MakeNotInstalledServer());

        Assert.Single(cut.FindComponents<ServerMailSetupAssistant>());
        Assert.Empty(cut.FindComponents<ServerMailStatusList>());
        Assert.DoesNotContain("MailDomains", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Requests, r => r.Url.Contains("api/servers/10/mail/domains", StringComparison.Ordinal));
    }

    [Fact]
    public void R2_090_AConfiguredStack_OpensOnItsStatus_AboveTheTabs_AndHasNoDiagnosticsTab()
    {
        var cut = RenderSection();

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<ServerMailStatusList>()));
        Assert.Empty(cut.FindComponents<ServerMailSetupAssistant>());
        var markup = cut.Markup;
        Assert.True(markup.IndexOf("mail-status", StringComparison.Ordinal) < markup.IndexOf("MailDomains", StringComparison.Ordinal));
        Assert.DoesNotContain(cut.FindComponents<OmniTabsItem>(), tab => tab.Instance.Key == "diagnostics");
        Assert.Equal(["domains", "accounts", "aliases", "spam", "queue", "logs", "relations"],
            cut.FindComponents<OmniTabsItem>().Select(tab => tab.Instance.Key).Distinct().ToArray());
    }

    [Fact]
    public void R2_080_PostfixInstalledWithoutAnyDomain_StillOpensOnTheAssistant()
    {
        SetupMailApiResponses();
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>());
        _handler.SetJsonResponse("api/servers/10/mail/accounts", new PaginatedResult<MailAccountDto>());
        var server = MakeMailServer();
        var cut = Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));

        cut.WaitForAssertion(() => Assert.True(Assert.Single(cut.FindComponents<ServerMailSetupAssistant>()).Instance.StackPresent));
        Assert.Empty(cut.FindComponents<ServerMailStatusList>());
        Assert.Contains("MailInstalledNotConfigured", cut.Markup, StringComparison.Ordinal);
    }

    // SITE-FUNC-METRICS-001: a failed count read was shown as a loader that never ended. It is now said, with a
    // retry that reads the counts again.
    [Fact]
    public async Task CountsReadFailure_ShowsTheFailureWithARetry()
    {
        SetupMailApiResponses();
        _handler.SetResponse("api/servers/10/mail/domains", System.Net.HttpStatusCode.InternalServerError);
        var server = MakeMailServer();
        var cut = Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".mail-counts-failed")));
        Assert.Empty(cut.FindAll(".mail-counts-loading"));
        Assert.Contains("LoadFailed", cut.Find(".mail-counts-failed").TextContent, StringComparison.Ordinal);

        SetupMailApiResponses();
        var retry = cut.FindAll(".mail-counts-failed button").Single(b => b.TextContent.Contains("Retry", StringComparison.Ordinal));
        await cut.InvokeAsync(() => retry.Click());

        cut.WaitForAssertion(() => Assert.Single(cut.FindComponents<ServerMailStatusList>()));
        Assert.Empty(cut.FindAll(".mail-counts-failed"));
    }
}
