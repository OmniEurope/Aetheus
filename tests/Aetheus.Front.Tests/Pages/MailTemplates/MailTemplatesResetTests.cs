// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages;

/// <summary>Restoring the default of a customized template is confirmed, shows the restored default,
/// and reports a refusal with the server's reason.</summary>
public sealed class MailTemplatesResetTests : BunitContext
{
    private const string Url = "api/mail-templates/NotificationChannelTest/";
    private readonly BunitTestHelper.TestHandler _handler;

    public MailTemplatesResetTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        BunitTestHelper.Interactive(this);
        _handler.SetJsonResponse(HttpMethod.Get, Url, Template(customized: true, subject: "Custom {{ChannelName}}"));
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    private OmniOverlayService Toasts => Services.GetRequiredService<OmniOverlayService>();

    private static MailTemplateDto Template(bool customized, string subject) => new()
    {
        Kind = MailTemplateKind.NotificationChannelTest,
        Language = MailTemplateLanguages.English,
        Subject = subject,
        Body = "<p>{{ChannelName}}</p>",
        IsCustomized = customized,
        Variables = [new() { Name = "ChannelName", SampleValue = "Operations team" }],
    };

    private IRenderedComponent<MailTemplates> RenderLoaded()
    {
        var cut = Render<MailTemplates>();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-mail-template-editor")));
        return cut;
    }

    [Fact]
    public void AConfirmedRestore_ShowsTheDefaultAgain()
    {
        Dialog.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Delete, Url, Template(customized: false, subject: "[Aetheus] {{ChannelName}}"));
        var cut = RenderLoaded();

        cut.FindMenuItem("MailTemplateRestoreDefault")!.Click();

        cut.WaitForAssertion(() =>
        {
            Assert.Equal("[Aetheus] {{ChannelName}}", cut.Find(".omni-mail-template-editor__subject input").GetAttribute("value"));
            Assert.Contains("MailTemplateDefault", cut.Find(".mail-template-state").TextContent);
            Assert.Equal(OmniSeverity.Success, Assert.Single(Toasts.Toasts()).Severity);
        });
    }

    [Fact]
    public void ARefusedRestore_KeepsTheCustomizedTemplateAndSaysWhy()
    {
        Dialog.ConfirmResult = true;
        _handler.SetRawResponse(HttpMethod.Delete, Url, """{"message":"Template locked"}""", "application/json", HttpStatusCode.BadRequest);
        var cut = RenderLoaded();

        cut.FindMenuItem("MailTemplateRestoreDefault")!.Click();

        cut.WaitForAssertion(() =>
        {
            var toast = Assert.Single(Toasts.Toasts());
            Assert.Equal(OmniSeverity.Danger, toast.Severity);
            Assert.Contains("Template locked", toast.Detail, StringComparison.Ordinal);
        });
        Assert.Equal("Custom {{ChannelName}}", cut.Find(".omni-mail-template-editor__subject input").GetAttribute("value"));
    }

    [Fact]
    public void ADeclinedRestore_SendsNothing()
    {
        Dialog.ConfirmResult = false;
        var cut = RenderLoaded();

        cut.FindMenuItem("MailTemplateRestoreDefault")!.Click();

        Assert.NotNull(Dialog.LastConfirmMessage);
        Assert.DoesNotContain(_handler.Requests, request => request.Method == "DELETE");
    }
}
