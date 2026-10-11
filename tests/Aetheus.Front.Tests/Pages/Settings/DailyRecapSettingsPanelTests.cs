// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>The daily recap tab: its settings, the send on request and the preview.</summary>
public sealed class DailyRecapSettingsPanelTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public DailyRecapSettingsPanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.Interactive(this);
    }

    private static DailyRecapSettingsDto Defaults() => new()
    {
        Sections = [.. Enum.GetValues<DailyRecapSection>()],
        Recipients = ["alice@example.org"],
        RecipientsAreDefault = true
    };

    [Fact]
    public void Defaults_AreShown_EverySectionTicked_AndTheAdministratorsPrefilledAsRecipients()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/settings/daily-recap", Defaults());

        var cut = Render<DailyRecapSettingsPanel>();

        cut.WaitForAssertion(() => Assert.Equal(Enum.GetValues<DailyRecapSection>().Length, cut.FindAll(".daily-recap-section input[type=checkbox]").Count));
        Assert.All(cut.FindAll(".daily-recap-section input[type=checkbox]"), box => Assert.True(box.HasAttribute("checked")));
        Assert.Contains("DailyRecapRecipientsDefaultHint", cut.Markup);
        Assert.Equal(["alice@example.org"], cut.FindAll(".daily-recap-recipient-address").Select(item => item.TextContent));
        Assert.Contains("DailyRecapNextRecipients", cut.Find(".daily-recap-next-recipients").TextContent);
        Assert.Contains("DailyRecapSendNowHint", cut.Find(".daily-recap-send-now").GetAttribute("title"));
        Assert.Contains("DailyRecapLastNever", cut.Find(".daily-recap-last").TextContent);
        Assert.Equal("Europe/Paris", cut.Find("#daily-recap-zone").GetAttribute("value"));
        Assert.DoesNotContain("autofocus", cut.Markup, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Save_SendsTheSectionsLeftTicked_AndTheEditedRecipientList()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/settings/daily-recap", Defaults());
        _handler.SetJsonResponse(HttpMethod.Put, "api/settings/daily-recap", Defaults());

        var cut = Render<DailyRecapSettingsPanel>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".daily-recap-section input[type=checkbox]")));
        cut.FindAll(".daily-recap-section input[type=checkbox]")[0].Change(false);
        cut.Find("#daily-recap-new-recipient").Input("ops@example.org");
        cut.Find(".daily-recap-add-recipient").Click();
        cut.Find(".daily-recap-remove-recipient").Click(); // alice, first in the list
        cut.Find("#daily-recap-new-recipient").Input("cto@partner.net"); // typed, not added: the save adds it

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var put = Assert.Single(_handler.RequestDetails, r => r.Method == "PUT" && r.Url.Contains("api/settings/daily-recap"));
            Assert.Contains("\"recipients\":[\"ops@example.org\",\"cto@partner.net\"]", put.Body, StringComparison.OrdinalIgnoreCase);
            using var body = System.Text.Json.JsonDocument.Parse(put.Body!);
            var sections = body.RootElement.EnumerateObject()
                .Single(property => string.Equals(property.Name, "sections", StringComparison.OrdinalIgnoreCase)).Value;
            Assert.Equal(Enum.GetValues<DailyRecapSection>().Length - 1, sections.GetArrayLength());
            Assert.Contains("\"timeZoneId\":\"Europe/Paris\"", put.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void Adding_RefusesAnInvalidAddress_AndADuplicate()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/settings/daily-recap", Defaults());

        var cut = Render<DailyRecapSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#daily-recap-new-recipient")));

        cut.Find("#daily-recap-new-recipient").Input("not-an-address");
        cut.Find(".daily-recap-add-recipient").Click();
        Assert.Contains("DailyRecapRecipientInvalid", cut.Find(".daily-recap-recipients").TextContent);

        cut.Find("#daily-recap-new-recipient").Input("ALICE@example.org");
        cut.Find(".daily-recap-add-recipient").Click();
        Assert.Contains("DailyRecapRecipientDuplicate", cut.Find(".daily-recap-recipients").TextContent);

        Assert.Single(cut.FindAll(".daily-recap-recipient-address"));
    }

    [Fact]
    public void SwitchedOnWithAnEmptyList_BlocksTheSave()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/settings/daily-recap", Defaults());

        var cut = Render<DailyRecapSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".daily-recap-remove-recipient")));
        cut.Find(".daily-recap-remove-recipient").Click();
        Assert.Contains("DailyRecapRecipientsEmpty", cut.Markup);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("DailyRecapRecipientsRequired", cut.Find(".daily-recap-recipients").TextContent));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public void SendNow_ShowsTheServersVerdict()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/settings/daily-recap", Defaults());
        _handler.SetJsonResponse(HttpMethod.Post, "api/settings/daily-recap/send-now", new DailyRecapSendResultDto
        {
            Status = DailyRecapSendStatus.Failed,
            Message = "5.7.1 Relay access denied",
            Recipients = ["alice@example.org"],
            Delivered = 0
        });

        var cut = Render<DailyRecapSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".daily-recap-send-now")));

        cut.Find(".daily-recap-send-now").Click();

        cut.WaitForAssertion(() => Assert.Contains("5.7.1 Relay access denied", cut.Find(".daily-recap-send-result").TextContent));
        Assert.Contains("DailyRecapSendNotSent", cut.Find(".daily-recap-send-result").TextContent);
    }

    [Fact]
    public void Preview_IsDrawnByTheShellScript_WithTheRecapHtml()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/settings/daily-recap", Defaults());
        _handler.SetJsonResponse(HttpMethod.Get, "api/settings/daily-recap/preview", new DailyRecapPreviewDto
        {
            Day = new DateOnly(2026, 10, 8),
            Subject = "Récapitulatif quotidien Aetheus",
            Html = "<!DOCTYPE html><html><body><p>recap</p></body></html>"
        });

        var cut = Render<DailyRecapSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".daily-recap-preview-toggle")));

        cut.Find(".daily-recap-preview-toggle").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".daily-recap-preview-frame")));
        var call = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.renderEmailPreview");
        Assert.Equal("<!DOCTYPE html><html><body><p>recap</p></body></html>", call.Arguments[1]);
    }
}
