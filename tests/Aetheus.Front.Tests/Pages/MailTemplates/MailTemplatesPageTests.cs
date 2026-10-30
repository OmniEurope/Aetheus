// SPDX-License-Identifier: EUPL-1.2
using Bunit;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Recette R1-75: the mail template page edits one kind in one language and sends a test to the administrator.</summary>
public sealed class MailTemplatesPageTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public MailTemplatesPageTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.Interactive(this);
    }

    private static MailTemplateDto Template(bool customized = false, string subject = "[Aetheus] {{ChannelName}}") => new()
    {
        Kind = MailTemplateKind.NotificationChannelTest,
        Language = MailTemplateLanguages.English,
        Subject = subject,
        Body = "<p>{{ChannelName}}</p>",
        IsCustomized = customized,
        UpdatedBy = customized ? "admin" : null,
        UpdatedAt = customized ? new DateTime(2026, 10, 13, 9, 0, 0) : null,
        Variables =
        [
            new() { Name = "ChannelName", SampleValue = "Operations team" },
            new() { Name = "InboxUrl", SampleValue = "https://aetheus.example.org/notifications" }
        ]
    };

    private IRenderedComponent<MailTemplates> RenderLoaded()
    {
        var cut = Render<MailTemplates>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-mail-template-editor")));
        return cut;
    }

    [Fact]
    public void TheTemplate_IsLoaded_WithOneButtonPerVariable_AndNothingToSaveYet()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/mail-templates/NotificationChannelTest/", Template());

        var cut = RenderLoaded();

        Assert.Equal(2, cut.FindAll(".omni-mail-template-editor__variable").Count);
        Assert.Equal("[Aetheus] {{ChannelName}}", cut.Find(".omni-mail-template-editor__subject input").GetAttribute("value"));
        Assert.Contains("MailTemplateDefault", cut.Find(".mail-template-state").TextContent);
        Assert.True(cut.Find(".mail-template-save").HasAttribute("disabled"));
        // Every mail written from a template is offered: the channel test, the event notifications, the daily recap.
        Assert.Equal(["NotificationChannelTest", "NotificationEvent", "DailyRecap", "UserWelcome", "PasswordReset", "SiteAdminGranted"],
            cut.FindAll("#mail-template-kind option").Select(option => option.TextContent.Trim().Split('_')[^1]));
    }

    [Fact]
    public void ALinkVariable_IsOfferedAsAnActionButton_AndABlockVariableAsABlock()
    {
        var template = Template() with
        {
            Kind = MailTemplateKind.NotificationEvent,
            Body = "<p>{{Details}}</p>",
            Variables =
            [
                new() { Name = "Event", SampleValue = "Pipeline failed" },
                new() { Name = "Link", SampleValue = "https://aetheus.example.org/pipelines/runs/1", Kind = MailTemplateVariableKind.Link },
                new() { Name = "Details", SampleValue = "<table><tr><td>Status</td><td>Failed</td></tr></table>", Kind = MailTemplateVariableKind.Block }
            ]
        };
        _handler.SetJsonResponse(HttpMethod.Get, "api/mail-templates/NotificationChannelTest/", template);

        var cut = RenderLoaded();

        // OE offers one action button per link variable; text and block variables are not button targets.
        var button = Assert.Single(cut.FindAll(".omni-mail-template-editor__button"));
        Assert.Equal("Link", button.GetAttribute("data-omni-button"));
        Assert.Equal(3, cut.FindAll(".omni-mail-template-editor__variable").Count);
    }

    [Theory]
    [InlineData(MailTemplateVariableKind.Text, OmniMailVariableKind.Text)]
    [InlineData(MailTemplateVariableKind.Link, OmniMailVariableKind.Link)]
    [InlineData(MailTemplateVariableKind.Block, OmniMailVariableKind.Block)]
    public void EachVariableKind_IsPassedToTheEditor(MailTemplateVariableKind kind, OmniMailVariableKind expected) =>
        Assert.Equal(expected, MailTemplates.KindOf(kind));

    [Fact]
    public void Saving_SendsTheEditedSubject_AndShowsTheTemplateAsCustomized()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/mail-templates/NotificationChannelTest/", Template());
        _handler.SetJsonResponse(HttpMethod.Put, "api/mail-templates/NotificationChannelTest/", Template(customized: true, subject: "Alert: {{ChannelName}}"));
        var cut = RenderLoaded();

        cut.Find(".omni-mail-template-editor__subject input").Input("Alert: {{ChannelName}}");
        cut.WaitForAssertion(() => Assert.False(cut.Find(".mail-template-save").HasAttribute("disabled")));
        cut.Find(".mail-template-save").Click();

        cut.WaitForAssertion(() =>
        {
            var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
            Assert.Contains("\"subject\":\"Alert: {{ChannelName}}\"", put.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"body\":\"\\u003Cp\\u003E{{ChannelName}}\\u003C/p\\u003E\"", put.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("MailTemplateCustomized", cut.Find(".mail-template-state").TextContent);
            Assert.Contains("MailTemplateChangedBy", cut.Find(".mail-template-state").TextContent);
        });
    }

    [Fact]
    public void SendingATest_PostsTheDraftToTheServer_AndSaysWhereItWent()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/mail-templates/NotificationChannelTest/", Template());
        _handler.SetJsonResponse(HttpMethod.Post, "api/mail-templates/NotificationChannelTest/",
            new MailTemplateTestResultDto { Status = NotificationTestStatus.Sent, To = "admin@example.org", Message = "Accepted" });
        var cut = RenderLoaded();

        cut.Find(".omni-mail-template-editor__send-test").Click();

        cut.WaitForAssertion(() =>
        {
            var post = Assert.Single(_handler.RequestDetails, request => request.Method == "POST");
            Assert.Matches("api/mail-templates/NotificationChannelTest/(en|fr)/test$", post.Url);
            Assert.Contains("\"subject\":\"[Aetheus] {{ChannelName}}\"", post.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
        });
    }

    [Fact]
    public void AFailedTest_ShowsTheSendersReason()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/mail-templates/NotificationChannelTest/", Template());
        _handler.SetJsonResponse(HttpMethod.Post, "api/mail-templates/NotificationChannelTest/",
            new MailTemplateTestResultDto { Status = NotificationTestStatus.Failed, To = "admin@example.org", Message = "550 mailbox unavailable" });
        var cut = RenderLoaded();

        cut.Find(".omni-mail-template-editor__send-test").Click();

        cut.WaitForAssertion(() => Assert.Contains(Services.Toasts(),
            toast => toast.Severity == OmniSeverity.Danger && toast.ToString().Contains("550 mailbox unavailable", StringComparison.Ordinal)));
    }

    [Fact]
    public void RestoringTheDefault_IsOfferedOnlyForACustomizedTemplate()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/mail-templates/NotificationChannelTest/", Template());

        var cut = RenderLoaded();

        Assert.True(cut.Find(".mail-template-more button").HasAttribute("disabled"));
    }
}
