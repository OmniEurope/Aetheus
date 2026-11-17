// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Tests;

/// <summary>Recette R-27 for every mail the platform sends: the action buttons are at the top, and the analysis button opens
/// the analysis concerned, never the quality of every project. Mails are composed by the production composer and the
/// shipped default templates; only the sender is a double.</summary>
public sealed class NotificationMailButtonTests
{
    private const string Origin = "https://aetheus.example.org";

    private static NotificationEmail Event(string eventType, string payload) =>
        new("reader@example.org", eventType, eventType, payload);

    [Theory]
    [InlineData("pipeline.failed", """{"PipelineRunId":2698,"ProjectId":1,"PipelineName":"build"}""", "/pipelines/runs/2698")]
    [InlineData("alert.triggered", """{"ServerId":7,"ServerName":"web-1","Metric":"CpuUsage","AlertId":42,"Message":"CPU high"}""", "/alerts/42")]
    [InlineData("analysis.gate.blocked", """{"PipelineRunId":2698,"ProjectId":1,"GateStatus":2}""", "/pipelines/runs/2698?tab=gate")]
    [InlineData("SecretExpiring", """{"ProjectId":1,"SecretName":"db"}""", "/vaults")]
    public void TheEventButton_IsTheFirstLinkAndComesBeforeTheIntroAndTheDetails(string eventType, string payload, string route)
    {
        var message = NotificationMailKit.Build(Event(eventType, payload), Origin);

        var button = message.HtmlBody!.IndexOf($"href=\"{Origin}{route}\"", StringComparison.Ordinal);
        var detailsHeading = message.HtmlBody.IndexOf("Event details", StringComparison.Ordinal);
        Assert.True(button >= 0, "the mail has its button");
        Assert.True(detailsHeading > button, "the button comes before the details");
        Assert.Equal(button, message.HtmlBody.IndexOf("href=\"", StringComparison.Ordinal));
    }

    [Fact]
    public void AnApprovalMail_KeepsItsActionsAndItsButtonTogetherAtTheTop()
    {
        var message = NotificationMailKit.Build(Event("pipeline.approval-requested",
            """{"PipelineRunId":2698,"ApprovalId":5,"ProjectId":1,"PipelineName":"deploy"}"""), Origin);

        var firstLink = message.HtmlBody!.IndexOf("href=\"", StringComparison.Ordinal);
        var detailsHeading = message.HtmlBody.IndexOf("Event details", StringComparison.Ordinal);
        Assert.True(firstLink >= 0 && firstLink < detailsHeading);
        Assert.Contains($"href=\"{Origin}/pipelines/runs/2698\"", message.HtmlBody[..detailsHeading]);
    }

    [Theory]
    [InlineData(MailTemplateKind.NotificationEvent, "{{Intro}}")]
    [InlineData(MailTemplateKind.NotificationEvent, "{{Details}}")]
    [InlineData(MailTemplateKind.UserWelcome, "{{UserName}}")]
    [InlineData(MailTemplateKind.PasswordReset, "{{UserName}}")]
    [InlineData(MailTemplateKind.SiteAdminGranted, "{{UserName}}")]
    public void EveryDefaultTemplate_PutsItsButtonBeforeTheText(MailTemplateKind kind, string firstText)
    {
        foreach (var language in new[] { MailTemplateLanguages.English, MailTemplateLanguages.French })
        {
            var (_, body) = MailTemplateDefaults.For(kind, language);

            var button = body.IndexOf("{{#button", StringComparison.Ordinal);
            Assert.True(button >= 0, $"{kind}/{language} has a button");
            Assert.True(button < body.IndexOf(firstText, StringComparison.Ordinal), $"{kind}/{language}: the button comes before {firstText}");
            Assert.Equal(button, body.LastIndexOf("{{#button", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("analysis.report.completed", """{"Id":9,"ProjectId":1,"PipelineRunId":2698,"Status":2,"GateStatus":2}""", "/pipelines/runs/2698?tab=gate")]
    [InlineData("analysis.gate.warning", """{"Id":9,"ProjectId":1,"PipelineRunId":2698,"GateStatus":1}""", "/pipelines/runs/2698?tab=gate")]
    [InlineData("analysis.gate.error", """{"Id":9,"ProjectId":1,"PipelineRunId":2698,"GateStatus":3}""", "/pipelines/runs/2698?tab=gate")]
    [InlineData("analysis.governance.expiring", """{"Kind":"Decision","Id":4,"ProjectId":1,"FindingId":88}""", "/analysis/findings/88")]
    [InlineData("analysis.report.completed", """{"Id":9,"ProjectId":1,"Status":2}""", "/projects/1/quality")]
    [InlineData("analysis.operational.cve-db-stale", """{"Sources":["osv:NuGet"]}""", "/analysis")]
    public void TheAnalysisButton_OpensTheAnalysisOfTheEvent(string eventType, string payload, string route)
    {
        var message = NotificationMailKit.Build(Event(eventType, payload), Origin);

        Assert.Contains($"href=\"{Origin}{route}\"", message.HtmlBody);
        Assert.Contains("Review analysis", message.TextBody);
    }

    [Fact]
    public void AGateEventThatNamesAProjectAndARun_NoLongerOpensTheProjectsWholeQualityPage()
    {
        var message = NotificationMailKit.Build(
            Event("analysis.gate.blocked", """{"ProjectId":1,"PipelineRunId":2698,"GateStatus":2}"""), Origin);

        Assert.DoesNotContain($"href=\"{Origin}/projects/1/quality\"", message.HtmlBody);
    }
}
