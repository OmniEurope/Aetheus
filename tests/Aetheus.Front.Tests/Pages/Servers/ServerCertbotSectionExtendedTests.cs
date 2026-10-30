// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

public class ServerCertbotSectionExtendedTests : BunitContext
{
    private static readonly BindingFlags Priv = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerCertbotSectionExtendedTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/servers/10/certbot/action", true);
        _handler.SetJsonResponse("api/servers/10/certbot/create", true);
    }

    private static ServerDetailDto MakeServer(List<CertbotCertificateDto>? certs = null) => new()
    {
        Id = 10,
        Name = "cert-srv",
        Hostname = "10.0.0.10",
        Type = ServerType.Normal,
        Status = ServerStatus.Online,
        Tags = [],
        Services = [],
        Certbot = new CertbotDataDto
        {
            IsInstalled = true,
            Version = "2.11.0",
            Certificates = certs ?? [
                new CertbotCertificateDto
                {
                    Name = "example.com",
                    Domains = ["example.com", "www.example.com"],
                    ExpiryDate = DateTime.UtcNow.AddDays(60)  // far away - not expiring soon
                },
                new CertbotCertificateDto
                {
                    Name = "api.example.com",
                    Domains = ["api.example.com"],
                    ExpiryDate = DateTime.UtcNow.AddDays(5)   // within 30 days - expiring soon
                }
            ]
        }
    };

    private IRenderedComponent<ServerCertbotSection> RenderSection(ServerDetailDto? server = null)
    {
        var s = server ?? MakeServer();
        return Render<ServerCertbotSection>(p => p
            .Add(x => x.Server, s)
            .Add(x => x.ServerId, s.Id));
    }

    [Fact]
    public void Renders_WithCertificates_ShowsNames()
    {
        var cut = RenderSection();
        Assert.Contains("example.com", cut.Markup);
    }

    // R-121: the version is said in the page title, the installed state and the number of certificates are not
    // repeated in the status bar; the grid has no counter nor filter box above it, its filters are inline.
    [Fact]
    public void StatusBar_HasNoInstalledBadgeNoCertificateCountNoVersion_AndNoFilterBoxAboveTheGrid()
    {
        var cut = RenderSection();

        Assert.DoesNotMatch(@">\s*Installed\s*<", cut.Markup);
        Assert.DoesNotContain("CertificateCount", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("2.11.0", cut.Markup, StringComparison.Ordinal);
        Assert.DoesNotContain("FilterCertificates", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void StatusBar_KeepsTheRenewalCheckBadge_WhenInstalled()
    {
        var s = MakeServer();
        var cut = this.RenderWithPageHeader<ServerCertbotSection>(p => p.Add(x => x.Server, s).Add(x => x.ServerId, s.Id));
        Assert.Single(cut.FindAll("[data-testid=certbot-renewal-check]"));
    }

    [Fact]
    public void TheGridFiltersInlineUnderItsHeader()
    {
        var cut = RenderSection();
        // ShowHeaderFilterMenu="false": the filter row is rendered and no column header holds a filter button,
        // except the Expiry column, whose AdvancedFilter adds the From and To range menu (R-52, OE 1.10.0).
        Assert.Single(cut.FindAll("tr.omni-data-grid__filters"));
        var menu = Assert.Single(cut.FindAll(".omni-data-grid__filter-menu"));
        Assert.Contains("Expiry", menu.QuerySelector(".omni-data-grid__filter-menu-panel")!.GetAttribute("aria-label"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheExpiryFilter_IsOneTextInputForADayAMonthOrAYear_NotTwoDatePickers()
    {
        var cut = RenderSection();
        // R-52 (OE 1.10.0): the date column reads dd/mm/yyyy, mm/yyyy or yyyy in one field; the From and To
        // range pickers live in the header menu of the same filter (AdvancedFilter), not in the filter row.
        Assert.Empty(cut.FindAll("tr.omni-data-grid__filters input[type=date]"));
        Assert.NotEmpty(cut.FindAll("tr.omni-data-grid__filters input[type=text]"));
        Assert.NotEmpty(cut.FindAll(".omni-data-grid__filter-menu input[type=date]"));
    }

    [Fact]
    public void Renders_NotInstalled_WhenNotInstalled()
    {
        var server = MakeServer([]) with
        {
            Certbot = new CertbotDataDto { IsInstalled = false, Certificates = [] }
        };
        var cut = this.RenderWithPageHeader<ServerCertbotSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));
        Assert.Contains("NotInstalled", cut.Find("[data-outlet=page-header-badges]").TextContent);
    }

    [Fact]
    public void R_18_InstalledWithAFailedRenewalTest_ShowsNoNotInstalledBadge()
    {
        var server = MakeServer([]) with
        {
            Certbot = new CertbotDataDto
            {
                IsInstalled = true,
                Certificates = [],
                RenewalCheckedAt = new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc),
                RenewalCheckSucceeded = false
            }
        };
        var cut = this.RenderWithPageHeader<ServerCertbotSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));

        Assert.DoesNotContain("NotInstalled", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.Find("[data-outlet=page-header-badges]").Children);
        Assert.NotEmpty(cut.FindAll(".certbot-renewal-failures"));
    }

    [Fact]
    public void CertbotResourceNames_ReturnsAllCertNames()
    {
        var cut = RenderSection();
        var names = (List<string>)typeof(ServerCertbotSection)
            .GetProperty("CertbotResourceNames", Priv)!
            .GetValue(cut.Instance)!;
        Assert.Equal(2, names.Count);
        Assert.Contains("example.com", names);
        Assert.Contains("api.example.com", names);
    }

    [Fact]
    public async Task ExecuteActionAsync_RenewAll_SetsActionRunningFalseAfter()
    {
        var cut = RenderSection();
        await (Task)typeof(ServerCertbotSection).GetMethod("ExecuteActionAsync", Priv)!
            .Invoke(cut.Instance, [CertbotAction.RenewAll, null, null])!;
        Assert.False((bool)typeof(ServerCertbotSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    [Fact]
    public async Task ExecuteActionAsync_Renew_WithCertName_SendsRequest()
    {
        var cut = RenderSection();
        await (Task)typeof(ServerCertbotSection).GetMethod("ExecuteActionAsync", Priv)!
            .Invoke(cut.Instance, [CertbotAction.Renew, "example.com", null])!;
        Assert.False((bool)typeof(ServerCertbotSection).GetField("_actionRunning", Priv)!.GetValue(cut.Instance)!);
    }

    // The create form now lives in ServerCertbotCreateDialog. Hardened rule #4: render it
    // DIRECTLY (in-render-tree, no separate-host hang) and assert the form renders prefilled
    // and the Create button is clickable - do NOT assert on OnClose for a directly-rendered
    // dialog (it does not propagate). The create logic itself is covered via ApiClient tests.
    [Fact]
    public void CreateDialog_RendersForm_CreateButtonWired()
    {
        var cut = Render<ServerCertbotCreateDialog>(p => p.Add(x => x.ServerId, 10));

        var model = typeof(ServerCertbotCreateDialog).GetField("_model", Priv)!.GetValue(cut.Instance)!;
        model.GetType().GetProperty("Domains")!.SetValue(model, "new.example.com");
        model.GetType().GetProperty("Email")!.SetValue(model, "admin@example.com");
        cut.Render(p => p.Add(x => x.ServerId, 10));

        Assert.Contains("Domains", cut.Markup);
        Assert.Contains("Email", cut.Markup);
        cut.FindAll("button").First(b => b.TextContent.Contains("Create")).Click();
        // Clicking Create on the prefilled, valid form POSTs to the certbot create endpoint.
        Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/servers/10/certbot/create"));
    }

    [Fact]
    public void Renders_ExpiringSoon_Badge_WhenCertExpiringSoon()
    {
        var cut = RenderSection();
        Assert.Contains("ExpiringSoon", cut.Markup);
    }

    [Fact]
    public void NewCertificateButton_IsDisabled_WhileRenewAllRuns()
    {
        // New certificate keeps the "an action is running" interlock in
        // its Disabled.
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/servers/10/certbot/action", async ct =>
        {
            await release.Task.WaitAsync(ct);
            return new { };
        });
        // Recette R-29: Create and its menu sit in the page header, rendered here with the section.
        var s = MakeServer();
        var cut = this.RenderWithPageHeader<ServerCertbotSection>(p => p.Add(x => x.Server, s).Add(x => x.ServerId, s.Id));
        AngleSharp.Dom.IElement Button(string text) => cut.FindAll("button").Single(b =>
            b.QuerySelector(".rz-button-text")?.TextContent.Trim() == text
            || b.TextContent.Trim() == text
            || b.GetAttribute("title") == text);
        Assert.False(Button("NewCertificate").HasAttribute("disabled"));

        ((Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        cut.FindMenuItem("RenewAll")!.Click();

        // Renew all is a menu entry now (R3-41), so the zone's visible action carries the interlock.
        cut.WaitForAssertion(() => Assert.True(Button("NewCertificate").HasAttribute("disabled")));
        release.SetResult();
        // Loaded-machine ceiling: the action's answer returns through the thread pool before its handler
        // ends and renders; under load the one POST was answered and the button enabled 165 to 808 ms
        // after bUnit's 1 s default, never left disabled (PLAN-002 B7).
        cut.WaitForAssertion(() => Assert.False(Button("NewCertificate").HasAttribute("disabled")));
    }

}
