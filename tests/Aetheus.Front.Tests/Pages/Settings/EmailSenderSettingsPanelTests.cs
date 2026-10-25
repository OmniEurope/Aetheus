// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>The platform e-mail sender tab.</summary>
public sealed class EmailSenderSettingsPanelTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public EmailSenderSettingsPanelTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        BunitTestHelper.Interactive(this);
    }

    private static EmailSenderSettingsDto DefaultInUse() => new()
    {
        EffectiveSource = EmailSenderSource.ManagedNoReply,
        EffectiveFromAddress = "no-reply@example.org",
        EffectiveHost = "mail.example.org",
        EffectivePort = 587,
        NoReplyAddress = "no-reply@example.org",
        CanPrepareNoReply = true
    };

    [Fact]
    public void DefaultSender_IsShown_WithNoFieldToFill_AndNoPrepareButton()
    {
        _handler.SetJsonResponse("api/settings/email-sender", DefaultInUse());

        var cut = Render<EmailSenderSettingsPanel>();

        cut.WaitForAssertion(() => Assert.Contains("EmailSenderEffectiveDefault", cut.Find(".email-sender-effective").TextContent));
        Assert.Contains("EmailSenderEffectiveDetail", cut.Find(".email-sender-effective").TextContent);
        Assert.Empty(cut.FindAll("#email-sender-host"));
        Assert.Empty(cut.FindAll(".email-sender-prepare-button"));
    }

    [Fact]
    public void DefaultSender_OffersToResetItsPassword_AndTheResetPostsToTheMailModuleAfterConfirmation()
    {
        _handler.SetJsonResponse("api/settings/email-sender", DefaultInUse());
        _handler.SetJsonResponse(HttpMethod.Post, "api/mail/no-reply-sender", DefaultInUse());

        var dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        var cut = Render<EmailSenderSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".email-sender-reset-button")));

        dialog.ConfirmResult = false;
        cut.Find(".email-sender-reset-button").Click();
        cut.WaitForAssertion(() => Assert.Equal(1, dialog.OpenCount));
        Assert.Contains("EmailSenderResetConfirm", dialog.LastConfirmMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "POST");

        dialog.ConfirmResult = true;
        cut.Find(".email-sender-reset-button").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/mail/no-reply-sender")));
    }

    [Fact]
    public void CustomServer_HasNoResetButton()
    {
        _handler.SetJsonResponse("api/settings/email-sender", DefaultInUse() with { UseCustom = true, EffectiveSource = EmailSenderSource.Custom });

        var cut = Render<EmailSenderSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#email-sender-host")));

        Assert.Empty(cut.FindAll(".email-sender-reset-button"));
    }

    [Fact]
    public void MissingMailbox_OffersToPrepareIt_AndThePreparationPostsToTheMailModule()
    {
        _handler.SetJsonResponse("api/settings/email-sender", DefaultInUse() with
        {
            EffectiveSource = EmailSenderSource.None,
            EffectiveFromAddress = null,
            Problem = EmailSenderProblem.NoReplyAccountMissing,
            ProblemDetail = "no-reply@example.org"
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/mail/no-reply-sender", DefaultInUse());

        var cut = Render<EmailSenderSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".email-sender-prepare-button")));
        Assert.Contains("Enum_EmailSenderProblem_NoReplyAccountMissing", cut.Find(".email-sender-effective").TextContent);

        cut.Find(".email-sender-prepare-button").Click();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, r => r.Method == "POST" && r.Url.Contains("api/mail/no-reply-sender")));
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".email-sender-prepare-button")));
    }

    [Fact]
    public void CustomServer_ShowsItsFields_AndSavesWithoutEchoingAStoredPassword()
    {
        _handler.SetJsonResponse("api/settings/email-sender", DefaultInUse() with
        {
            UseCustom = true,
            Host = "smtp.provider.net",
            Port = 465,
            TlsMode = SmtpTlsMode.SslOnConnect,
            Username = "aetheus",
            HasPassword = true,
            FromAddress = "alerts@provider.net",
            EffectiveSource = EmailSenderSource.Custom
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/settings/email-sender", DefaultInUse() with { UseCustom = true, EffectiveSource = EmailSenderSource.Custom });

        var cut = Render<EmailSenderSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#email-sender-host")));
        Assert.Equal("smtp.provider.net", cut.Find("#email-sender-host").GetAttribute("value"));
        Assert.Contains("EmailSenderPasswordKept", cut.Markup);

        cut.Find("form").Submit();

        cut.WaitForAssertion(() =>
        {
            var put = Assert.Single(_handler.RequestDetails, r => r.Method == "PUT" && r.Url.Contains("api/settings/email-sender"));
            Assert.Contains("\"host\":\"smtp.provider.net\"", put.Body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("\"password\":null", put.Body, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void CustomServerWithoutHost_IsNotSent_AndSaysWhy()
    {
        _handler.SetJsonResponse("api/settings/email-sender", DefaultInUse() with { UseCustom = true });

        var cut = Render<EmailSenderSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#email-sender-host")));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains("EmailSenderHostRequired", cut.Markup));
        Assert.DoesNotContain(_handler.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public void NoFieldTakesTheFocusOnOpen()
    {
        _handler.SetJsonResponse("api/settings/email-sender", DefaultInUse() with { UseCustom = true });

        var cut = Render<EmailSenderSettingsPanel>();
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("#email-sender-host")));

        Assert.Empty(cut.FindAll("[autofocus]"));
    }
}
