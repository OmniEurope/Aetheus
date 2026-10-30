// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Services.Email;
using Microsoft.Extensions.Configuration;
using MimeKit;
using NSubstitute;

namespace Aetheus.Back.Tests;

public sealed class NotificationEmailPresentationTests
{
    internal static NotificationEmail FailedRun => new("reader@example.org",
        "pipeline.failed in project Aetheus", NotificationEventTypes.PipelineFailed,
        """{"PipelineRunId":2698,"PipelineId":41,"PipelineName":"publish-observability-packages","BuildNumber":11,"ProjectId":1,"Status":"Failed"}""");

    [Fact]
    public void FailedPipeline_ExplainsTheEvent_InsteadOfUsingItsTechnicalNameAsTheHeading()
    {
        var message = NotificationMailKit.Build(FailedRun, "https://aetheus.example.org");
        var path = Environment.GetEnvironmentVariable("AETHEUS_NOTIFICATION_SAMPLE");
        if (!string.IsNullOrWhiteSpace(path))
        {
            var bodyEnd = message.HtmlBody!.IndexOf('>', message.HtmlBody.IndexOf("<body", StringComparison.Ordinal)) + 1;
            File.WriteAllText(path, message.HtmlBody.Insert(bodyEnd,
                "<div style=\"padding:8px 12px;background:#fff3d6;color:#7a4b00;font:13px Arial;text-align:center;\">EXEMPLE - données de test, aucun envoi</div>"));
        }

        Assert.DoesNotContain("pipeline.failed", message.Subject);
        Assert.Contains("publish-observability-packages", message.TextBody);
        Assert.Contains("logs", message.TextBody, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("https://aetheus.example.org/pipelines/runs/2698", message.TextBody);
        Assert.Contains("href=\"https://aetheus.example.org/pipelines/runs/2698\"", message.HtmlBody);
    }

    [Theory]
    [InlineData("\"fr\"", "Échec de l'exécution du pipeline", "Le pipeline s'est arrêté sur un échec.", "fr")]
    [InlineData("\"en\"", "Pipeline execution failed", "The pipeline stopped with a failure.", "en")]
    public void TheNotificationWords_AreReadFromTheEmbeddedResource_InTheRecipientsLanguage(
        string languageJson, string title, string intro, string htmlLanguage)
    {
        // NotificationEmailStrings(.fr).resx is found by its embedded name; this renders a real mail from
        // it in each language, so a moved or renamed resource fails here and not in a recipient's inbox.
        var message = NotificationMailKit.Build(FailedRun, "https://aetheus.example.org", languageJson: languageJson);

        Assert.Contains(title, message.Subject);
        Assert.Contains(intro, message.TextBody);
        Assert.Contains($"lang=\"{htmlLanguage}\"", message.HtmlBody);
    }

    public static IEnumerable<object[]> CatalogEvents => NotificationEventTypes.All.Select(e => new object[] { e.EventType });

    [Theory]
    [MemberData(nameof(CatalogEvents))]
    public void EveryCatalogEvent_HasAnExplanation_AndUsesEnglishWithoutARecipientPreference(string eventType)
    {
        var message = NotificationMailKit.Build(new NotificationEmail("reader@example.org", eventType, eventType, "{}"));
        Assert.DoesNotContain(eventType, message.Subject);
        Assert.DoesNotContain("Aetheus notification", message.Subject);
        Assert.DoesNotContain("Source event", message.TextBody);
        Assert.Contains("lang=\"en\"", message.HtmlBody);
        Assert.Contains("notification subscriptions or platform notification rules", message.TextBody);
    }

    [Theory]
    [InlineData("pipeline.approval-requested", "/pipelines/runs/2698")]
    [InlineData("analysis.gate.blocked", "/projects/1/quality")]
    [InlineData("agent-update.failed", "/servers/7/overview")]
    [InlineData("alert.triggered", "/servers/7/overview")]
    [InlineData("certbot.renewal-check.failed", "/servers/7/certbot")]
    [InlineData("certbot.certificate-expiring", "/servers/7/certbot")]
    [InlineData("release.deployed", "/releases/8")]
    [InlineData("app.down", "/projects/1/overview")]
    [InlineData("SecretExpiring", "/vaults")]
    public void EventLinks_UseTheConfiguredOriginAndTheRelevantRoute(string eventType, string route)
    {
        var email = FailedRun with { EventType = eventType, PayloadJson = """{"PipelineRunId":2698,"ProjectId":1,"ServerId":7,"ReleaseId":8}""" };
        var message = NotificationMailKit.Build(email, "https://app.example.org/ignored/path");
        Assert.Contains("href=\"https://app.example.org" + route + "\"", message.HtmlBody);
        Assert.Contains("https://app.example.org" + route, message.TextBody);
        Assert.DoesNotContain("ignored/path", message.HtmlBody);
    }

    [Fact]
    public void AnAiTaskRun_LinksToItsReport_OnTheTaskResultsPage()
    {
        var email = FailedRun with
        {
            EventType = NotificationEventTypes.AiTaskCompleted,
            PayloadJson = """{"ProjectId":12,"AiTaskDefinitionId":5,"AiRunResultId":300,"TaskName":"nightly review"}"""
        };

        var message = NotificationMailKit.Build(email, "https://app.example.org");

        Assert.Contains("href=\"https://app.example.org/projects/12/ai-tasks/5?result=300\"", message.HtmlBody);
        Assert.Contains("AI report ready", message.Subject);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("app.example.org")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@app.example.org")]
    public void MissingOrUnsafeOrigins_ProduceNoLinks(string? origin)
    {
        var message = NotificationMailKit.Build(FailedRun, origin);
        // The template's inbox link keeps its words and loses its address; no action button is drawn.
        Assert.DoesNotContain("href=", message.HtmlBody);
        Assert.DoesNotContain("bgcolor=\"" + OmniEurope.Blazor.Components.OmniMailTheme.Default.Accent + "\"", message.HtmlBody);
        Assert.Contains("A web address is not configured", message.TextBody);
    }

    [Theory]
    [InlineData("not JSON <script>alert(1)</script>")]
    [InlineData("[1,2,3]")]
    [InlineData("null")]
    public void UnknownOrMalformedPayload_IsRetainedWithoutBreakingDelivery(string payload)
    {
        var message = NotificationMailKit.Build(new NotificationEmail("reader@example.org", "A new event", "future.event", payload));
        Assert.Contains(payload, message.TextBody);
        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("A new event", message.TextBody);
    }

    [Fact]
    public void PayloadCannotInjectMarkupOrChooseTheLinkDestination()
    {
        var email = FailedRun with { PayloadJson = """{"PipelineName":"<img src=x onerror=alert(1)>","PipelineRunId":"//evil.example.org","ProjectId":-1,"Url":"javascript:alert(1)"}""" };
        var message = NotificationMailKit.Build(email, "https://app.example.org");
        Assert.DoesNotContain("<img", message.HtmlBody);
        Assert.Contains("&lt;img", message.HtmlBody);
        Assert.DoesNotContain("href=\"javascript:", message.HtmlBody);
        Assert.DoesNotContain("href=\"//evil", message.HtmlBody);
        Assert.Contains("href=\"https://app.example.org/notifications\"", message.HtmlBody);
    }

    [Fact]
    public void AnalysisEnums_AreReadableInsteadOfShowingTheirWireNumbers()
    {
        var message = NotificationMailKit.Build(new NotificationEmail("reader@example.org", "analysis.report.completed",
            "analysis.report.completed", """{"Status":2,"GateStatus":2}"""));
        Assert.Contains("Reported status\tError", message.TextBody);
        Assert.Contains("Quality gate\tBlocked", message.TextBody);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task Dispatch_PassesTheConfiguredFrontOriginToTheRenderer(string? remoteOrigin)
    {
        var sender = Substitute.For<IEmailSender>();
        sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Sent("no-reply@example.org")]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Cors:FrontendUrl"] = "https://app.example.org", ["Cors:Origins:0"] = remoteOrigin }).Build();
        var result = await new NotificationEmailer(sender, NotificationMailKit.Templates(), config).SendAsync([FailedRun], TestContext.Current.CancellationToken);
        Assert.Equal(EmailSendStatus.Sent, Assert.Single(result).Status);
        await sender.Received(1).SendBatchAsync(Arg.Is<IReadOnlyList<EmailMessage>>(messages =>
            messages[0].HtmlBody!.Contains("https://app.example.org/pipelines/runs/2698")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void MimeRoundTrip_PreservesUtf8AndBothAlternatives()
    {
        var mail = NotificationMailKit.Build(FailedRun with
        { PayloadJson = """{"PipelineName":"Échec à Bruxelles","PipelineRunId":2698}""" });
        using var message = new MimeMessage
        { Subject = mail.Subject, Body = new BodyBuilder { TextBody = mail.TextBody, HtmlBody = mail.HtmlBody }.ToMessageBody() };
        message.From.Add(MailboxAddress.Parse("no-reply@example.org"));
        message.To.Add(MailboxAddress.Parse(mail.To));
        using var stream = new MemoryStream();
        message.WriteTo(stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var read = MimeMessage.Load(stream, TestContext.Current.CancellationToken);
        Assert.NotNull(read.TextBody);
        Assert.NotNull(read.Body);
        Assert.Equal(mail.Subject, read.Subject);
        Assert.Contains("Échec à Bruxelles", read.Subject);
        Assert.Contains("Échec à Bruxelles", read.TextBody);
        Assert.Equal(mail.TextBody.ReplaceLineEndings("\n"), read.TextBody.ReplaceLineEndings("\n"));
        Assert.Equal(mail.HtmlBody!.ReplaceLineEndings("\n"), read.HtmlBody!.ReplaceLineEndings("\n"));
        Assert.True(read.Body.ContentType.IsMimeType("multipart", "alternative"));
    }

    [Fact]
    public async Task DispatchLinks_UseTheRemoteOriginInsteadOfTheDevelopmentDefault()
    {
        var sender = Substitute.For<IEmailSender>();
        sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns([EmailSendResult.Sent("no-reply@example.org")]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:FrontendUrl"] = "https://localhost:5401",
            ["Cors:Origins:0"] = "https://app.example.org"
        }).Build();
        await new NotificationEmailer(sender, NotificationMailKit.Templates(), config).SendAsync([FailedRun], TestContext.Current.CancellationToken);
        await sender.Received(1).SendBatchAsync(Arg.Is<IReadOnlyList<EmailMessage>>(messages =>
            messages[0].HtmlBody!.Contains("https://app.example.org/pipelines/runs/2698")
            && !messages[0].HtmlBody!.Contains("localhost")), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("Failed", "#fde8e6", "#a3231a")]
    [InlineData("Error", "#fde8e6", "#a3231a")]
    [InlineData("Blocked", "#fde8e6", "#a3231a")]
    [InlineData("Critical", "#fde8e6", "#a3231a")]
    [InlineData("Down", "#fde8e6", "#a3231a")]
    [InlineData("Offline", "#fde8e6", "#a3231a")]
    [InlineData("Passed", "#e3f4e8", "#17643a")]
    [InlineData("Success", "#e3f4e8", "#17643a")]
    [InlineData("Succeeded", "#e3f4e8", "#17643a")]
    [InlineData("Online", "#e3f4e8", "#17643a")]
    [InlineData("Healthy", "#e3f4e8", "#17643a")]
    [InlineData("Warning", "#fff3d6", "#7a4b00")]
    [InlineData("High", "#fff3d6", "#7a4b00")]
    [InlineData("Degraded", "#fff3d6", "#7a4b00")]
    [InlineData("failed", "#eef0f3", "#404b5a")]
    [InlineData("Queued", "#eef0f3", "#404b5a")]
    public void StatusDetail_ColoursTheBadgeByExactStatusValue(string status, string background, string ink)
    {
        var mail = NotificationMailKit.Build(FailedRun with { PayloadJson = $$"""{"Severity":"{{status}}"}""" });

        Assert.Contains(
            $"font-weight: 600; background-color: {background}; color: {ink}\">{status}</span>",
            mail.HtmlBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void NonStatusDetail_HasNoBadge()
    {
        var mail = NotificationMailKit.Build(FailedRun with { PayloadJson = """{"PipelineName":"Failed"}""" });

        Assert.DoesNotContain("font-weight: 600; background-color", mail.HtmlBody, StringComparison.Ordinal);
    }

    [Fact]
    public void R177_AChildResult_AlsoLinksTheRootRunOfItsLineage()
    {
        var email = FailedRun with
        {
            PayloadJson = """{"PipelineRunId":2698,"PipelineId":41,"PipelineName":"qa","BuildNumber":11,"ProjectId":1,"Status":"Failed","IsChildRun":true,"RootRunId":2690,"RootPipelineName":"nightly"}"""
        };

        var message = NotificationMailKit.Build(email, "https://aetheus.example.org");

        Assert.Contains("href=\"https://aetheus.example.org/pipelines/runs/2698\"", message.HtmlBody);
        Assert.Contains("href=\"https://aetheus.example.org/pipelines/runs/2690\"", message.HtmlBody);
        Assert.Contains("Open the root run of its lineage (nightly) (https://aetheus.example.org/pipelines/runs/2690)", message.TextBody);
    }

    [Fact]
    public void R177_ARootResult_HasNoSecondLink()
    {
        var email = FailedRun with
        {
            PayloadJson = """{"PipelineRunId":2698,"ProjectId":1,"Status":"Failed","IsChildRun":false,"RootRunId":2698}"""
        };

        var message = NotificationMailKit.Build(email, "https://aetheus.example.org");

        Assert.DoesNotContain("root run", message.TextBody, StringComparison.OrdinalIgnoreCase);
    }
}
