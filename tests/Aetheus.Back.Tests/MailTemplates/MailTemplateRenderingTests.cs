// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recettes R1-74 and R1-75. Every platform mail is written from its editable template in the recipient's
/// language and colours; a notification keeps the structure it had before templates (status banner, action
/// button, status badges), its blocks written by Aetheus and placed by the template.
/// </summary>
public sealed class MailTemplateRenderingTests
{
    private const string Banner = "<div style=\"margin:0 0 16px 0;padding: 20px 24px; background-color: #fde8e6; color: #a3231a\">";
    private static readonly string ActionButton = "bgcolor=\"" + OmniMailTheme.Default.Accent + "\"><a style=\"display:inline-block;padding:12px 24px";
    private const string FailedBadge = "font-weight: 600; background-color: #fde8e6; color: #a3231a";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static NotificationEmail FailedRun => NotificationEmailPresentationTests.FailedRun;

    [Fact]
    public void AFrenchRecipient_GetsTheRichLayout_InFrench()
    {
        var mail = NotificationMailKit.Build(FailedRun, "https://aetheus.example.org", languageJson: "\"fr-FR\"");

        Assert.Equal("[Aetheus] Échec de l'exécution du pipeline : publish-observability-packages", mail.Subject);
        Assert.Contains("lang=\"fr\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(Banner, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Notification de la plateforme", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(ActionButton, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("href=\"https://aetheus.example.org/pipelines/runs/2698\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Voir l'exécution et ses journaux", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(FailedBadge, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("Numéro de build\t11", mail.TextBody, StringComparison.Ordinal);
        // The banner states the event: the raw event key is never shown.
        Assert.DoesNotContain("Événement source", mail.TextBody, StringComparison.Ordinal);
        Assert.DoesNotContain("pipeline.failed", mail.TextBody, StringComparison.Ordinal);
    }

    /// <summary>Golden structure of a notification: the banner takes the colours of the status, payload first, else the event's.</summary>
    [Theory]
    [InlineData("pipeline.failed", """{"PipelineRunId":1,"Status":"Failed"}""", "#fde8e6", "#a3231a")]
    [InlineData("pipeline.child-results", """{"PipelineRunId":1,"Status":"Succeeded"}""", "#e3f4e8", "#17643a")]
    [InlineData("pipeline.succeeded", """{"PipelineRunId":1}""", "#e3f4e8", "#17643a")]
    [InlineData("server.offline", """{"ServerId":7}""", "#fde8e6", "#a3231a")]
    [InlineData("analysis.gate.warning", """{"ProjectId":1}""", "#fff3d6", "#7a4b00")]
    [InlineData("analysis.report.completed", """{"Status":2}""", "#fde8e6", "#a3231a")]
    [InlineData("pipeline.approval-requested", """{"PipelineRunId":1}""", "#eef0f3", "#404b5a")]
    [InlineData("future.event", "{}", "#eef0f3", "#404b5a")]
    public void TheStatusBanner_TakesTheColoursOfTheStatus(string eventType, string payload, string background, string ink)
    {
        var mail = NotificationMailKit.Build(new NotificationEmail("reader@example.org", eventType, eventType, payload), "https://aetheus.example.org");

        var banner = $"padding: 20px 24px; background-color: {background}; color: {ink}\"><span style=\"font-size: 12px; font-weight: 700; color: {ink}\">AETHEUS · ";
        Assert.Contains(banner, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Single(mail.HtmlBody!.Split("background-color: " + background + "; color: " + ink + "\"><span", StringSplitOptions.None).Skip(1));
    }

    /// <summary>
    /// Golden structure: banner first, then the explanation, the detail table, the action button to the event's page,
    /// in that order, and nothing left unrendered.
    /// </summary>
    [Fact]
    public void TheDefaultNotification_KeepsItsStructure_BannerDetailsButton()
    {
        var mail = NotificationMailKit.Build(FailedRun, "https://aetheus.example.org");
        var html = mail.HtmlBody!;

        var banner = html.IndexOf(Banner, StringComparison.Ordinal);
        var intro = html.IndexOf("The pipeline stopped with a failure.", StringComparison.Ordinal);
        var details = html.IndexOf(">Event details</h3>", StringComparison.Ordinal);
        var badge = html.IndexOf(FailedBadge + "\">Failed</span>", StringComparison.Ordinal);
        var button = html.IndexOf(ActionButton, StringComparison.Ordinal);
        Assert.True(banner >= 0 && banner < intro && intro < details && details < badge && badge < button,
            $"banner {banner}, intro {intro}, details {details}, badge {badge}, button {button}");
        Assert.Contains("href=\"https://aetheus.example.org/pipelines/runs/2698\" target=\"_blank\" rel=\"noopener noreferrer\">View execution and logs</a>",
            html, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", html, StringComparison.Ordinal);
        Assert.DoesNotContain("border-left", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ATranslatedStatus_KeepsTheColourOfItsBadge()
    {
        var mail = NotificationMailKit.Build(new NotificationEmail("reader@example.org", "analysis.report.completed",
            "analysis.report.completed", """{"Status":2,"GateStatus":2}"""), languageJson: "\"fr\"");

        Assert.Contains("Barrière qualité\tBloqué", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(FailedBadge + "\">Bloqué</span>", mail.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("\"en\"")]
    [InlineData("\"de-DE\"")]
    [InlineData("not json")]
    public void ARecipientWithoutAFrenchPreference_GetsTheRichLayout_InEnglish(string? languageJson)
    {
        var mail = NotificationMailKit.Build(FailedRun, "https://aetheus.example.org", languageJson: languageJson);

        Assert.StartsWith("[Aetheus] Pipeline execution failed: ", mail.Subject, StringComparison.Ordinal);
        Assert.Contains("lang=\"en\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(Banner, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(ActionButton, mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains(FailedBadge, mail.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void ANotification_IsWrittenFromTheAdministratorsTemplate_InTheRecipientsLanguage()
    {
        var stored = new MailTemplate
        {
            Kind = MailTemplateKind.NotificationEvent,
            Language = MailTemplateLanguages.French,
            Subject = "Alerte : {{Event}} ({{Project}})",
            Body = "<p>Bonjour, {{Context}}.</p><p>{{Details}}</p><p>{{#button Link}}Ouvrir</p>"
        };

        var mail = NotificationMailKit.Build(FailedRun, "https://aetheus.example.org", languageJson: "\"fr\"", stored: stored);
        var english = NotificationMailKit.Build(FailedRun, "https://aetheus.example.org", stored: stored);

        Assert.Equal("Alerte : Échec de l'exécution du pipeline (Aetheus)", mail.Subject);
        Assert.Contains("Bonjour, publish-observability-packages.", mail.TextBody, StringComparison.Ordinal);
        Assert.Contains(FailedBadge + "\">Failed</span>", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("href=\"https://aetheus.example.org/pipelines/runs/2698\" target=\"_blank\" rel=\"noopener noreferrer\">Ouvrir</a>",
            mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(Banner, mail.HtmlBody, StringComparison.Ordinal);
        // The English recipient keeps the default template: the stored one is French only.
        Assert.StartsWith("[Aetheus] Pipeline execution failed: ", english.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDailyRecap_IsWrittenFromItsTemplate_WithItsTilesAndSections()
    {
        var document = DailyRecapComposer.Compose(DailyRecapRenderingTests.SampleReport(),
            new DailyRecapText(System.Globalization.CultureInfo.GetCultureInfo("fr-FR")), "https://aetheus.example.org");

        var mail = DailyRecapRenderingTests.RenderMail(document, "fr-FR");

        Assert.Equal(document.Subject, mail.Subject);
        Assert.Empty(mail.MissingVariables);
        // Tiles: one stacked table per figure, in the colour of its tone, before the first section.
        var tiles = mail.Html.IndexOf("background-color: #e3f4e8; color: #17643a\"><span style=\"font-size: 22px", StringComparison.Ordinal);
        var failedTile = mail.Html.IndexOf("background-color: #fde8e6; color: #a3231a\"><span style=\"font-size: 22px", StringComparison.Ordinal);
        var firstSection = mail.Html.IndexOf(">Pipelines</h2>", StringComparison.Ordinal);
        Assert.True(tiles >= 0 && failedTile > tiles && firstSection > failedTile, $"tiles {tiles}, failed {failedTile}, section {firstSection}");
        Assert.Contains("font-weight: 600; background-color: #fde8e6; color: #a3231a\">Échec</span> <a href=\"https://aetheus.example.org/pipelines/runs/4821\"",
            mail.Html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("fr")]
    public void EveryKind_RendersItsDefaultTemplate_WithEveryVariableGiven(string language)
    {
        foreach (var kind in Enum.GetValues<MailTemplateKind>())
        {
            var (subject, body) = MailTemplateDefaults.For(kind, language);

            var samples = MailTemplateCatalog.Samples(kind, language);
            var mail = MailTemplateRenderer.Render(subject, body, samples, MailRecipientStyle.Default with { Language = language });

            Assert.Empty(mail.MissingVariables);
            Assert.Contains("Aetheus", mail.Subject, StringComparison.Ordinal);
            Assert.All(MailTemplateCatalog.Variables(kind).Where(variable => variable.Kind == MailTemplateVariableKind.Block),
                variable => Assert.True(samples.Blocks.ContainsKey(variable.Name) && !samples.Values.ContainsKey(variable.Name), variable.Name));
            Assert.Contains($"lang=\"{language}\"", mail.Html, StringComparison.Ordinal);
            Assert.DoesNotContain("{{", mail.Html, StringComparison.Ordinal);
            Assert.All(OmniMailRenderer.FindVariables(subject + body),
                name => Assert.Contains(name, MailTemplateCatalog.VariableNames(kind)));
        }
    }

    [Fact]
    public async Task TheChannelTest_UsesItsTemplate_InTheRequestersLanguageAndColours()
    {
        var preset = OmniThemePresets.All[^1];
        var palette = OmniThemePalettes.All.First(item => OmniMailTheme.From(preset.With(item)).Link != OmniMailTheme.Default.Link);
        var appearance = $$"""{"version":1,"appearance":"Light","preset":"{{preset.Name}}","palette":"{{palette.Name}}","font":""}""";
        var sender = Substitute.For<IEmailSender>();
        sender.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>()).Returns(EmailSendResult.Sent("no-reply@x.org"));
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Cors:FrontendUrl"] = "https://aetheus.example.org" }).Build();
        var emailer = new NotificationEmailer(sender, NotificationMailKit.Templates("\"fr\"", appearance), configuration);

        await emailer.SendChannelTestAsync("admin@x.org", "Équipe ops", Ct);

        await sender.Received(1).SendAsync(Arg.Is<EmailMessage>(message =>
            message.Subject == "[Aetheus] Test du canal de notification : « Équipe ops »"
            && message.TextBody.Contains("Canal : Équipe ops")
            && message.HtmlBody!.Contains(OmniMailTheme.From(preset.With(palette)).Link, StringComparison.OrdinalIgnoreCase)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    [InlineData("""{"preset":"No such theme","palette":"No such palette"}""")]
    [InlineData("[1,2]")]
    public void AnUnknownOrMissingAppearance_KeepsTheDefaultTheme(string? appearanceJson)
    {
        Assert.Equal(MailRecipientStyle.Default, MailRecipientStyle.FromPreferences(null, appearanceJson));
    }

    [Fact]
    public async Task AStoredTemplate_ReplacesTheDefault_ForItsLanguageOnly()
    {
        var stored = new MailTemplate
        {
            Kind = MailTemplateKind.NotificationChannelTest,
            Language = MailTemplateLanguages.French,
            Subject = "Essai : {{ChannelName}}",
            Body = "<p>Bonjour, {{ChannelName}}</p>"
        };
        var templates = NotificationMailKit.Templates(stored: stored);
        var values = MailTemplateCatalog.Samples(MailTemplateKind.NotificationChannelTest, "fr");

        var french = await templates.RenderAsync(MailTemplateKind.NotificationChannelTest, values, MailRecipientStyle.Default with { Language = "fr" }, Ct);
        var english = await templates.RenderAsync(MailTemplateKind.NotificationChannelTest, values, MailRecipientStyle.Default, Ct);

        Assert.Equal("Essai : Équipe exploitation", french.Subject);
        Assert.Contains("Bonjour, Équipe exploitation", french.Text, StringComparison.Ordinal);
        Assert.StartsWith("[Aetheus] Notification channel test", english.Subject, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AVariableWithoutValue_RendersNothing_IsListed_AndLogged()
    {
        var stored = new MailTemplate
        {
            Kind = MailTemplateKind.NotificationChannelTest,
            Language = MailTemplateLanguages.English,
            Subject = "Test {{ChannelName}}",
            Body = "<p>{{ChannelName}} {{Unknown}}</p>"
        };
        var logger = Substitute.For<ILogger<MailTemplateRenderer>>();
        var templates = new MailTemplateRenderer(NotificationMailKit.Repository(stored: stored), logger);

        var mail = await templates.RenderAsync(MailTemplateKind.NotificationChannelTest,
            MailTemplateData.Text(new Dictionary<string, string?> { ["ChannelName"] = "ops" }), MailRecipientStyle.Default, Ct);

        Assert.Equal(["Unknown"], mail.MissingVariables);
        Assert.DoesNotContain("Unknown", mail.Html, StringComparison.Ordinal);
        logger.ReceivedWithAnyArgs(1).Log(LogLevel.Warning, default, default(object)!, null, default!);
    }

    [Theory]
    [InlineData("https://aetheus.example.org/notifications", true)]
    [InlineData("mailto:ops@example.org", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("//evil.example.org", false)]
    [InlineData("", false)]
    public void ALinkAddress_IsKeptOnlyForHttpHttpsAndMailto(string address, bool kept)
    {
        var mail = MailTemplateRenderer.Render("s", "<p><a href=\"{{InboxUrl}}\">Open</a></p>",
            MailTemplateData.Text(new Dictionary<string, string?> { ["InboxUrl"] = address }), MailRecipientStyle.Default);

        Assert.Equal(kept, mail.Html.Contains("href=\"" + address + "\"", StringComparison.Ordinal));
        Assert.DoesNotContain("javascript:", mail.Html, StringComparison.Ordinal);
        Assert.Contains("Open", mail.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AValue_IsWrittenAsText_NeverAsMarkup()
    {
        var mail = MailTemplateRenderer.Render("{{ChannelName}}", "<p>{{ChannelName}}</p>",
            MailTemplateData.Text(new Dictionary<string, string?> { ["ChannelName"] = "<img src=x onerror=alert(1)>" }), MailRecipientStyle.Default);

        Assert.DoesNotContain("<img", mail.Html, StringComparison.Ordinal);
        Assert.Contains("&lt;img", mail.Html, StringComparison.Ordinal);
    }
}
