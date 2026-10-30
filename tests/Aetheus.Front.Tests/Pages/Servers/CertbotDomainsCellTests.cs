// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R-123: a certificate with one name shows it; with two or more, a badge "N domaines" opens the
/// list of its names in a dialog (the same pattern as the readers of a library).</summary>
public sealed class CertbotDomainsCellTests : BunitContext
{
    private readonly IRenderedComponent<OmniComponentsHost> _host;

    public CertbotDomainsCellTests()
    {
        BunitTestHelper.RegisterServices(this);
        // The wording of the badge is the point: read it from the real resource file, not from keys.
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.French());
        _host = Render<OmniComponentsHost>(p => p.Add(h => h.OverlayService, Services.GetRequiredService<OmniOverlayService>()));
    }

    private IRenderedComponent<CertbotDomainsCell> RenderCell(params string[] domains) =>
        Render<CertbotDomainsCell>(p => p.Add(x => x.Certificate,
            new CertbotCertificateDto { Name = "aetheus.sonytumen.com", Domains = [.. domains], ExpiryDate = DateTime.UtcNow.AddDays(60) }));

    [Fact]
    public void OneDomain_IsShownAsText_WithNoBadgeNorButton()
    {
        var cut = RenderCell("aetheus.sonytumen.com");

        Assert.Equal("aetheus.sonytumen.com", cut.Find(".certbot-domains").TextContent.Trim());
        Assert.Empty(cut.FindAll("button"));
    }

    [Fact]
    public void TwoDomainsOrMore_ShowOnlyTheCountBadge_AndNoName()
    {
        var cut = RenderCell("a.example.com", "b.example.com", "c.example.com");

        Assert.Equal("3 domaines", cut.Find("button.omni-badge").TextContent.Trim());
        // R-67 / R-53: the badge itself is the click target, blue, and no OmniButton sits around it.
        Assert.Contains("omni-badge--info", cut.Find("button.omni-badge").ClassList);
        Assert.Empty(cut.FindAll(".omni-button"));
        Assert.False(string.IsNullOrWhiteSpace(cut.Find("button").GetAttribute("aria-label")));
        Assert.DoesNotContain("a.example.com", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void ClickingTheBadge_OpensADialogListingEveryDomain()
    {
        var cut = RenderCell("a.example.com", "b.example.com");

        cut.Find("button").Click();

        _host.WaitForAssertion(() => Assert.NotEmpty(_host.FindAll(".omni-dialog")));
        Assert.Equal("Domaines de aetheus.sonytumen.com", _host.Find(".omni-dialog__title").TextContent.Trim());
        Assert.Equal(["a.example.com", "b.example.com"],
            _host.FindAll("[data-testid=certbot-domains-dialog-list] li").Select(item => item.TextContent.Trim()));
    }

    [Theory]
    [InlineData("www.example.com", true)]
    [InlineData("WWW.EXAMPLE", true)]
    [InlineData("other.org", false)]
    public void TheDomainsFilterMatchesAnyNameOfTheCertificate(string filter, bool expected)
    {
        var certificate = new CertbotCertificateDto { Name = "example.com", Domains = ["example.com", "www.example.com"] };

        Assert.Equal(expected, ServerCertbotSection.DomainsMatch(certificate, filter));
    }
}
