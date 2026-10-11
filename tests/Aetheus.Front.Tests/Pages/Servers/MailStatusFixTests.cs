// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R1-9 / R1-11 / R1-14: under each red mail line, what the record is, the record to copy,
/// the effect of publishing it, and for an MX still showing the old host, the DNS cache it may come from.
/// Rendered with the real French resources, so the sentences the user reads are the ones asserted.</summary>
public sealed class MailStatusFixTests : BunitContext
{
    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private readonly CultureInfo _previousCulture = CultureInfo.CurrentCulture;

    public MailStatusFixTests()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
        BunitTestHelper.RegisterServices(this);
        Services.AddSingleton(MailOperationDialogValidationTests.RealLocalizer());
    }

    protected override void Dispose(bool disposing)
    {
        CultureInfo.CurrentUICulture = _previousUiCulture;
        CultureInfo.CurrentCulture = _previousCulture;
        base.Dispose(disposing);
    }

    private IRenderedComponent<MailStatusFix> RenderFix(MailDiagnosticItemDto item) =>
        Render<MailStatusFix>(p => p.Add(f => f.Item, item).Add(f => f.Hostname, "mx.sonytumen.com").Add(f => f.CanManage, true));

    private static MailDiagnosticItemDto Mx(int? ttl) => new()
    {
        Key = "mx:sonytumen.com",
        Verdict = MailCheckVerdict.Mismatch,
        Detail = "mx1.ovh.net, mx2.ovh.net, mxb.ovh.net",
        RecordName = "sonytumen.com",
        RecordType = "MX",
        RecordValue = "10 mx.sonytumen.com.",
        TimeToLiveSeconds = ttl
    };

    [Fact]
    public void R1_9_TheMxFix_SaysWhatItIs_TheRecordToCopy_AndThatMailLeavesTheProvider()
    {
        var cut = RenderFix(Mx(ttl: null));

        Assert.Contains("indique aux autres serveurs où livrer le courrier", cut.Find(".mail-fix-what").TextContent, StringComparison.Ordinal);
        var code = cut.FindComponent<OmniCodeBlock>().Instance;
        Assert.Equal(("MX sonytumen.com", "10 mx.sonytumen.com."), (code.Title, code.Code));
        Assert.Contains("le courrier n'arrive alors plus chez le fournisseur", cut.Find(".mail-fix-effect").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_14_AnMxWithTheResolverTtl_SaysHowLongTheOldAnswerMayStayInCache()
    {
        var cut = RenderFix(Mx(ttl: 81240));

        var note = cut.Find(".mail-fix-cache").TextContent;
        Assert.Contains("gardent l'ancienne réponse", note, StringComparison.Ordinal);
        Assert.Contains("environ 22 h 34 min", note, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_14_AnMxWithoutTtl_SaysItInGeneralTerms_WithoutInventingADuration()
    {
        var cut = RenderFix(Mx(ttl: null));

        var note = cut.Find(".mail-fix-cache").TextContent;
        Assert.Contains("souvent 24 heures pour un MX", note, StringComparison.Ordinal);
        Assert.DoesNotContain("environ", note, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_14_AShortTtl_IsShownInMinutes()
    {
        Assert.Contains("environ 2 min", RenderFix(Mx(ttl: 90)).Find(".mail-fix-cache").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R1_14_AMissingMx_HasNoCacheNote()
    {
        var cut = RenderFix(Mx(ttl: null) with { Verdict = MailCheckVerdict.Missing, Detail = "" });

        Assert.Empty(cut.FindAll(".mail-fix-cache"));
    }

    [Theory]
    [InlineData("spf:sonytumen.com", "sonytumen.com", "v=spf1 mx a include:mx.ovh.com ~all", "liste les serveurs autorisés", "garde les expéditeurs")]
    [InlineData("dkim:sonytumen.com", "mail._domainkey.sonytumen.com", "v=DKIM1; k=rsa; p=ABC", "signe chaque message", "Le courrier envoyé ailleurs n'est pas touché")]
    [InlineData("dmarc:sonytumen.com", "_dmarc.sonytumen.com", "v=DMARC1; p=quarantine; rua=mailto:postmaster@sonytumen.com", "échoue à la fois au SPF et au DKIM", "postmaster@sonytumen.com : créez cette boîte")]
    public void R1_9_EachTxtFix_SaysWhatItIs_TheRecord_AndItsEffect(string key, string name, string value, string what, string effect)
    {
        var cut = RenderFix(new MailDiagnosticItemDto
        {
            Key = key,
            Verdict = MailCheckVerdict.Missing,
            RecordName = name,
            RecordType = "TXT",
            RecordValue = value
        });

        Assert.Contains(what, cut.Find(".mail-fix-what").TextContent, StringComparison.Ordinal);
        Assert.Equal(value, cut.FindComponent<OmniCodeBlock>().Instance.Code);
        Assert.Contains(effect, cut.Find(".mail-fix-effect").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".mail-fix-cache"));
    }

    [Fact]
    public void R1_9_TheCertificateFix_SaysWhatItIs_AndKeepsItsButtons()
    {
        var cut = RenderFix(new MailDiagnosticItemDto { Key = "tls", Verdict = MailCheckVerdict.Mismatch, Detail = "self-signed" });

        Assert.Contains("Le certificat chiffre les connexions", cut.Find(".mail-fix-what").TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".mail-fix-effect"));
        Assert.Equal(2, cut.FindAll("button").Count);
    }

    [Fact]
    public void R1_11_TheHelp_IsFolded_AndCoversOvh_TheRuaTrap_TheMxWarning_AndPostmaster()
    {
        var cut = Render<MailDnsHelp>(p => p.Add(h => h.Domain, "sonytumen.com"));

        var details = cut.Find("details");
        Assert.False(details.HasAttribute("open"));
        var text = details.TextContent;
        Assert.Contains("Zone DNS", text, StringComparison.Ordinal);
        Assert.Contains("Filtrez le tableau par type", text, StringComparison.Ordinal);
        Assert.Contains("le bouton ••• au bout de sa ligne", text, StringComparison.Ordinal);
        Assert.Contains("Ajouter une entrée", text, StringComparison.Ordinal);
        Assert.Contains("son champ rua n'attend que l'adresse des rapports (mailto:postmaster@sonytumen.com)", text, StringComparison.Ordinal);
        Assert.Contains("le courrier n'arrive plus dans ses boîtes", cut.Find(".mail-dns-help-mx-warning").TextContent, StringComparison.Ordinal);
        Assert.Contains("rapports DMARC à postmaster@sonytumen.com", text, StringComparison.Ordinal);
        Assert.Equal(2, cut.FindAll("ol.mail-dns-help-list").Count);
    }
}
