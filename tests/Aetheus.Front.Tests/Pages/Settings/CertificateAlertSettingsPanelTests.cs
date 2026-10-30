// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>R-69: the certificate expiry alert lead times editor of the platform settings.</summary>
public sealed class CertificateAlertSettingsPanelTests : BunitContext
{
    private const string Url = "api/settings/certificate-alerts";

    private readonly BunitTestHelper.TestHandler _handler;

    public CertificateAlertSettingsPanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.Interactive(this);
    }

    private static CertificateAlertSettingsDto Defaults() => new() { LeadDays = [7, 3, 1] };

    private IRenderedComponent<CertificateAlertSettingsPanel> RenderPanel(CertificateAlertSettingsDto? saved = null)
    {
        _handler.SetJsonResponse(HttpMethod.Get, Url, saved ?? Defaults());
        _handler.SetJsonResponse(HttpMethod.Put, Url, saved ?? Defaults());
        var cut = Render<CertificateAlertSettingsPanel>();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find(".certificate-alert-save")));
        return cut;
    }

    private static string[] Rows(IRenderedComponent<CertificateAlertSettingsPanel> cut) =>
        [.. cut.FindAll(".certificate-alert-lead-text").Select(row => row.TextContent)];

    private string SavedBody()
    {
        var put = Assert.Single(_handler.RequestDetails, r => r.Method == "PUT" && r.Url.Contains(Url));
        return put.Body!;
    }

    [Fact]
    public void TheDefaultLeadTimes_AreListed_LongestFirst()
    {
        var cut = RenderPanel();

        // The test localizer answers the key; one day reads with its own singular key.
        Assert.Equal(["CertificateAlertsLeadMany", "CertificateAlertsLeadMany", "CertificateAlertsLeadOne"], Rows(cut));
        Assert.DoesNotContain("autofocus", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AddingALeadTime_KeepsTheListSorted_AndSaveSendsIt()
    {
        var cut = RenderPanel();

        cut.Find("#certificate-alert-new-days").Change("5");
        cut.Find(".certificate-alert-add").Click();

        Assert.Equal(4, Rows(cut).Length);
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("\"leadDays\":[7,5,3,1]", SavedBody(), StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ATypedValueNotAddedYet_IsAddedBySave()
    {
        var cut = RenderPanel();

        cut.Find("#certificate-alert-new-days").Change("14");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("\"leadDays\":[14,7,3,1]", SavedBody(), StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("3", "CertificateAlertsDaysDuplicate")]
    [InlineData("0", "CertificateAlertsDaysRange")]
    [InlineData("366", "CertificateAlertsDaysRange")]
    public void ADuplicateOrOutOfRangeValue_IsRefused_WithItsReason_AndNeverListed(string typed, string reason)
    {
        var cut = RenderPanel();

        cut.Find("#certificate-alert-new-days").Change(typed);
        cut.Find(".certificate-alert-add").Click();

        Assert.Equal(3, Rows(cut).Length);
        Assert.Contains(reason, cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void RemovingEveryLeadTime_ShowsTheEmptyState_AndSavesAnEmptyList()
    {
        var cut = RenderPanel();

        for (var remaining = 3; remaining > 0; remaining--)
            cut.Find(".certificate-alert-remove").Click();

        Assert.Empty(Rows(cut));
        Assert.NotNull(cut.Find(".certificate-alert-empty"));
        cut.Find("form").Submit();
        cut.WaitForAssertion(() => Assert.Contains("\"leadDays\":[]", SavedBody(), StringComparison.OrdinalIgnoreCase));
    }
}
