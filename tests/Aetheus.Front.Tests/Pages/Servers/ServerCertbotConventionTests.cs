// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>The Certbot view shows renewal conventions and rehearsal results with normalization and
/// test actions.</summary>
public sealed class ServerCertbotConventionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerCertbotConventionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/servers/10/certbot/action", true);
    }

    private Aetheus.Front.Tests.TestDoubles.ImmediateDialogService Confirmation =>
        (Aetheus.Front.Tests.TestDoubles.ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    [Theory]
    [InlineData(0, true)]
    [InlineData(CertbotHelperVersion.Current, false)]
    public void ACertbotHelperOlderThanTheRelease_IsShownAsToUpdate(int helperVersion, bool expectedAlert)
    {
        var cut = Render(new CertbotDataDto { IsInstalled = true, HelperVersion = helperVersion });

        Assert.Equal(expectedAlert, cut.FindAll(".certbot-helper-outdated").Count == 1);
    }

    [Fact]
    public void WithoutCertbot_NoHelperAlertIsShown()
    {
        var cut = Render(new CertbotDataDto { IsInstalled = false });

        Assert.Empty(cut.FindAll(".certbot-helper-outdated"));
    }

    [Fact]
    public void Certificates_ShowTheirConventionVerdict()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            Certificates =
            [
                Certificate("ok.example.com", CertbotRenewalConvention.Conforming, "webroot", "/var/www/aetheus-acme"),
                Certificate("random.example.com", CertbotRenewalConvention.NotWebroot, "apache", string.Empty),
                Certificate("docs.example.com", CertbotRenewalConvention.WrongWebroot, "webroot", "/var/lib/aetheus-agent/acme-webroot"),
                Certificate("legacy.example.com", CertbotRenewalConvention.Unknown, string.Empty, string.Empty)
            ]
        });

        var badges = cut.FindAll("[data-testid=certbot-convention]");
        Assert.Equal(
            ["CertbotConventionConforming", "CertbotConventionNotWebroot", "CertbotConventionWrongWebroot", "CertbotConventionUnknown"],
            badges.Select(badge => badge.TextContent.Trim()));
        Assert.Equal("webroot: /var/lib/aetheus-agent/acme-webroot", badges[2].GetAttribute("title"));
    }

    [Fact]
    public void R3_56_AFailedRehearsal_NamesEachFailingCertificateAndItsReason()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc),
            RenewalCheckSucceeded = false,
            RenewalCheckFailures =
            [
                new() { Name = "shop.example.com", Reason = "Some challenges have failed." },
                new() { Name = "old.example.org", Reason = string.Empty }
            ]
        });

        var lines = cut.FindAll(".certbot-renewal-failure").Select(line => line.TextContent.Trim()).ToArray();
        Assert.Equal(2, lines.Length);
        Assert.Contains("CertbotRenewalFailureLine", lines[0], StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".certbot-renewal-failures-unnamed"));
    }

    [Fact]
    public void R3_56_AFailedRehearsalWithoutANamedCertificate_SaysWhereTheOutputIs()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc),
            RenewalCheckSucceeded = false
        });

        Assert.Contains("CertbotRenewalCheckNoReason", cut.Find(".certbot-renewal-failures-unnamed").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_7_AFailedRehearsalWithoutANamedCertificate_ShowsTheLastLinesCertbotPrinted()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc),
            RenewalCheckSucceeded = false,
            RenewalCheckOutputTail = ["certbot renew --dry-run ran for 612 s and exited with code 1", "Another instance of Certbot is already running."]
        });

        var output = cut.Find(".certbot-renewal-output").TextContent;
        Assert.Contains("Another instance of Certbot is already running.", output, StringComparison.Ordinal);
        Assert.Contains("exited with code 1", output, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".certbot-renewal-failures-unnamed"));
    }

    [Fact]
    public async Task R1_7_ANamedFailureWithItsReason_DoesNotRepeatTheRawOutput_ButOneWithoutReasonDoes()
    {
        var named = new CertbotRenewalFailureDto { Name = "shop.example.com", Reason = "Some challenges have failed." };
        var tail = new List<string> { "1 renew failure(s), 0 parse failure(s)" };
        var checkedAt = new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc);

        var withReason = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = checkedAt,
            RenewalCheckSucceeded = false,
            RenewalCheckFailures = [named],
            RenewalCheckOutputTail = tail
        });
        Assert.Empty(withReason.FindAll(".certbot-renewal-output"));

        // One page header per page: the first page leaves before the second renders its own actions.
        await DisposeComponentsAsync();
        var withoutReason = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = checkedAt,
            RenewalCheckSucceeded = false,
            RenewalCheckFailures = [named, new() { Name = "wiki.example.com" }],
            RenewalCheckOutputTail = tail
        });

        Assert.Single(withoutReason.FindAll(".certbot-renewal-output"));
    }

    [Fact]
    public void R2_R11_AProxyRuleTheHelperCouldNotProtect_IsSaidOncePerFile_WithItsVerdictAndTheFix()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            HelperVersion = CertbotHelperVersion.Current,
            AcmeProxyRules = ["/etc/apache2/sites-enabled/shop.conf:12", "/srv/site.inc:2", "/etc/apache2/sites-enabled/shop.conf:30"]
        });

        var alert = cut.Find(".certbot-acme-proxy-rules");
        Assert.Contains("CertbotAcmeProxyRulesBody", alert.TextContent, StringComparison.Ordinal);
        // Recette R-11: one line per file (the file in its title), the wording for one line or several.
        var lines = cut.FindAll(".certbot-acme-proxy-rules-list .certbot-acme-proxy-rule");
        Assert.Equal(["/etc/apache2/sites-enabled/shop.conf", "/srv/site.inc"], lines.Select(line => line.GetAttribute("title")));
        Assert.Equal(["CertbotAcmeProxyRuleFileLines", "CertbotAcmeProxyRuleFileLine"], lines.Select(line => line.TextContent.Trim()));
        Assert.Contains("CertbotAcmeProxyRulesFix", alert.TextContent, StringComparison.Ordinal);
        Assert.Contains(ServerCertbotSection.AcmeChallengeCondition, cut.Find(".certbot-acme-proxy-rules-fix").TextContent, StringComparison.Ordinal);
        // A current helper no longer lists the websocket-only rules: the note says they have no effect.
        Assert.Contains("CertbotAcmeProxyRulesNote", alert.TextContent, StringComparison.Ordinal);
        Assert.DoesNotContain("CertbotAcmeProxyRulesNoteOutdated", alert.TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R11_AnOutdatedHelper_IsSaidToStillListTheWebsocketRules()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            HelperVersion = CertbotHelperVersion.Current - 1,
            AcmeProxyRules = ["/etc/apache2/sites-enabled/api.conf:9"]
        });

        Assert.Contains("CertbotAcmeProxyRulesNoteOutdated", cut.Find(".certbot-acme-proxy-rules").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R11_TheRules_AreGroupedByFile_KeepingTheirLines_AndAnEntryWithoutALine()
    {
        var files = ServerCertbotSection.GroupAcmeProxyRules(
            ["/a.conf:12", "/b.conf", "/a.conf:30", "/a.conf:12", "/c:d.conf:4", "/e.conf:x"]);

        Assert.Equal(["/a.conf", "/b.conf", "/c:d.conf", "/e.conf:x"], files.Select(file => file.Path));
        Assert.Equal(["12", "30"], files[0].Lines);
        Assert.Empty(files[1].Lines);
        Assert.Equal(["4"], files[2].Lines);
        Assert.Empty(files[3].Lines);
    }

    [Fact]
    public void R12_ACertificateWithSeveralNames_OffersToRemoveEach_AndOneWithASingleNameOffersNone()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            Certificates =
            [
                Certificate("aetheus.example.com", CertbotRenewalConvention.Conforming, "webroot", "/var/www/aetheus-acme")
                    with { Domains = ["aetheus.example.com", "aetheus-api.example.com"] },
                Certificate("solo.example.com", CertbotRenewalConvention.Conforming, "webroot", "/var/www/aetheus-acme")
            ]
        });

        foreach (var trigger in cut.FindAll("tbody .omni-overflow-menu__trigger").ToList())
            trigger.Click();
        var items = cut.FindAll(".certbot-remove-domain");
        Assert.Equal(2, items.Count);
        Assert.All(items, item => Assert.Equal("CertbotRemoveDomainItem", item.TextContent.Trim()));
    }

    [Fact]
    public void R12_RemovingAName_AsksWithTheRedVerb_ThenPostsTheNameAndItsCertificate()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            Certificates =
            [
                Certificate("aetheus.example.com", CertbotRenewalConvention.Conforming, "webroot", "/var/www/aetheus-acme")
                    with { Domains = ["aetheus.example.com", "aetheus-api.example.com"] }
            ]
        });

        cut.Find("tbody .omni-overflow-menu__trigger").Click();
        cut.FindAll(".certbot-remove-domain")[1].Click();
        // Declined: nothing is sent; the question is destructive and names its verb.
        Assert.DoesNotContain(_handler.RequestDetails, request => request.Url.Contains("certbot/action"));
        Assert.Equal("CertbotRemoveDomainConfirm", Confirmation.LastConfirmMessage);
        Assert.Equal("CertbotRemoveDomainVerb", Confirmation.LastConfirmOptions!.OkButtonText);
        Assert.True(Confirmation.LastConfirmOptions.Destructive);

        Confirmation.ConfirmResult = true;
        cut.Find("tbody .omni-overflow-menu__trigger").Click();
        cut.FindAll(".certbot-remove-domain")[1].Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Url.Contains("api/servers/10/certbot/action")
            && request.Body!.Contains($"\"action\":{(int)CertbotAction.RemoveDomain}", StringComparison.Ordinal)
            && request.Body.Contains("\"certificateName\":\"aetheus.example.com\"", StringComparison.Ordinal)
            && request.Body.Contains("\"domainName\":\"aetheus-api.example.com\"", StringComparison.Ordinal)));
    }

    [Fact]
    public void R2_WithoutAProxyRule_ShowsNoProxyRuleWarning()
    {
        var cut = Render(new CertbotDataDto { IsInstalled = true, HelperVersion = CertbotHelperVersion.Current });

        Assert.Empty(cut.FindAll(".certbot-acme-proxy-rules"));
    }

    [Fact]
    public void R3_56_APassedRehearsal_ShowsNoFailureList()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = new DateTime(2026, 10, 12, 3, 0, 0, DateTimeKind.Utc),
            RenewalCheckSucceeded = true
        });

        Assert.Empty(cut.FindAll(".certbot-renewal-failures"));
    }

    [Fact]
    public void R_55_AFailedRehearsal_HasNoRedHeaderBadge_ItsDateIsInTheFailureBanner()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = new DateTime(2026, 9, 18, 12, 8, 8, DateTimeKind.Utc),
            RenewalCheckSucceeded = false
        });

        Assert.Empty(cut.FindAll(".omni-badge[data-testid=certbot-renewal-check]"));
        Assert.Equal("CertbotRenewalCheckFailed", cut.Find(".certbot-renewal-failures [data-testid=certbot-renewal-check]").TextContent.Trim());
    }

    [Fact]
    public void R_53_TheRenewButtonOfARow_IsPrimaryBlue()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            Certificates = [Certificate("ok.example.com", CertbotRenewalConvention.Conforming, "webroot", "/var/www/aetheus-acme")]
        });

        Assert.Contains("omni-button--primary", cut.Find("button[title='Renew']").ClassList);
    }

    [Fact]
    public void R_56_AConfirmedAction_DoesNotRaiseTheWholePageVeil_TheSectionFollowerVeilsAlone()
    {
        var veil = Services.GetRequiredService<BusyVeil>();
        var changes = new List<bool>();
        veil.Changed += () => changes.Add(veil.IsBusy);
        var cut = Render(new CertbotDataDto { IsInstalled = true });
        Confirmation.ConfirmResult = true;

        cut.FindMenuItem("CertbotNormalize")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Url.Contains("certbot/action")));
        Assert.DoesNotContain(true, changes);
        Assert.False(veil.IsBusy);
    }

    [Theory]
    [InlineData(null, "CertbotRenewalCheckNever")]
    [InlineData(true, "CertbotRenewalCheckPassed")]
    public void RenewalCheck_ShowsTheLastOutcome(bool? succeeded, string expected)
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            RenewalCheckedAt = succeeded is null ? null : new DateTime(2026, 9, 18, 12, 8, 8, DateTimeKind.Utc),
            RenewalCheckSucceeded = succeeded
        });

        Assert.Equal(expected, cut.Find("[data-testid=certbot-renewal-check]").TextContent.Trim());
    }

    [Fact]
    public void TestRenewal_PostsTheRenewalCheckAction()
    {
        var cut = Render(new CertbotDataDto { IsInstalled = true });

        cut.FindMenuItem("CertbotRenewalCheck")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Url.Contains("api/servers/10/certbot/action")
            && request.Body!.Contains($"\"action\":{(int)CertbotAction.RenewalCheck}", StringComparison.Ordinal)));
    }

    [Fact]
    public void Normalize_AsksForConfirmationThenPostsTheNormalizeAction()
    {
        var cut = Render(new CertbotDataDto { IsInstalled = true });

        // Declined first: nothing is sent, and the question named its verb on an ordinary (blue) button.
        cut.FindMenuItem("CertbotNormalize")!.Click();
        Assert.DoesNotContain(_handler.RequestDetails, request => request.Url.Contains("certbot/action"));
        Assert.Equal("ConfirmCertbotNormalize", Confirmation.LastConfirmMessage);
        Assert.Equal("CertbotNormalize", Confirmation.LastConfirmOptions!.OkButtonText);
        Assert.False(Confirmation.LastConfirmOptions.Destructive);

        Confirmation.ConfirmResult = true;
        cut.FindMenuItem("CertbotNormalize")!.Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request =>
            request.Method == "POST" && request.Url.Contains("api/servers/10/certbot/action")
            && request.Body!.Contains($"\"action\":{(int)CertbotAction.Normalize}", StringComparison.Ordinal)));
    }

    [Fact]
    public void NotInstalled_HidesTheRenewalCheckAndDisablesTheConventionActions()
    {
        var cut = Render(new CertbotDataDto { IsInstalled = false });

        Assert.Empty(cut.FindAll("[data-testid=certbot-renewal-check]"));
        Assert.True(cut.FindMenuItem("CertbotNormalize")!.HasAttribute("disabled"));
        Assert.True(cut.FindMenuItem("CertbotRenewalCheck")!.HasAttribute("disabled"));
    }

    private IRenderedComponent<PageHeaderOutletHost> Render(CertbotDataDto certbot) =>
        this.RenderWithPageHeader<ServerCertbotSection>(parameters => parameters
            .Add(section => section.Server, new ServerDetailDto
            {
                Id = 10,
                Name = "cert-srv",
                Hostname = "10.0.0.10",
                Type = ServerType.Normal,
                Status = ServerStatus.Online,
                Tags = [],
                Services = [],
                Certbot = certbot
            })
            .Add(section => section.ServerId, 10));

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<PageHeaderOutletHost> cut, string text) =>
        cut.FindAll("button").Single(button =>
            button.TextContent.Trim() == text);

    private static CertbotCertificateDto Certificate(
        string name, CertbotRenewalConvention convention, string authenticator, string webroot) => new()
        {
            Name = name,
            Domains = [name],
            ExpiryDate = DateTime.UtcNow.AddDays(60),
            Authenticator = authenticator,
            WebrootPath = webroot,
            RenewalConvention = convention
        };

    [Fact]
    public void R3_41_CreateIsTheOneVisibleAction_AndTheMaintenanceActionsSitInTheMenu()
    {
        var cut = Render(new CertbotDataDto { IsInstalled = true });

        var standalone = cut.FindAll("button:not(.omni-menu__item):not(.omni-overflow-menu__trigger)")
            .Select(b => b.TextContent.Trim()).ToList();
        Assert.Contains("Create", standalone);
        Assert.DoesNotContain("CertbotNormalize", standalone);
        Assert.DoesNotContain("CertbotRenewalCheck", standalone);
        Assert.DoesNotContain("Renew", standalone);
        Assert.NotNull(cut.FindMenuItem("CertbotNormalize"));
        Assert.NotNull(cut.FindMenuItem("CertbotRenewalCheck"));
        Assert.NotNull(cut.FindMenuItem("RenewAll"));
    }

    [Fact]
    public void R3_41_ACertificateRow_KeepsRenewVisible_AndRevokeInItsMenu()
    {
        var cut = Render(new CertbotDataDto
        {
            IsInstalled = true,
            Certificates = [Certificate("ok.example.com", CertbotRenewalConvention.Conforming, "webroot", "/var/www/aetheus-acme")]
        });

        var row = cut.Find("button[title='Renew']").ParentElement!;
        var standalone = row.QuerySelectorAll("button:not(.omni-menu__item):not(.omni-overflow-menu__trigger)")
            .Select(b => b.GetAttribute("title")).ToList();
        Assert.Equal(["Renew"], standalone);
        Assert.NotNull(cut.FindMenuItem("Revoke"));
    }
}
