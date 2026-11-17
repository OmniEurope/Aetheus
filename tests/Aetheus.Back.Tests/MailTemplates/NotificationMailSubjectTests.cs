// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-26. A notification subject says where the mail comes from: "Aetheus - project or server - word",
/// the word naming the event in the recipient's language. The body does not change.
/// </summary>
public sealed class NotificationMailSubjectTests
{
    private const string French = "\"fr-FR\"";

    private static string Subject(string eventType, string payload, string subject = "", string? languageJson = null, MailTemplate? stored = null) =>
        NotificationMailKit.Build(new NotificationEmail("reader@example.org", subject.Length == 0 ? eventType : subject, eventType, payload),
            "https://aetheus.example.org", languageJson, stored: stored).Subject;

    [Theory]
    [InlineData("pipeline.failed", "Failure", "Échec")]
    [InlineData("pipeline.succeeded", "Success", "Succès")]
    [InlineData("pipeline.rejected", "Rejection", "Rejet")]
    [InlineData("pipeline.child-results", "Result", "Résultat")]
    [InlineData("pipeline.approval-requested", "Approval", "Approbation")]
    [InlineData("pipeline.launch-refused", "Refusal", "Refus")]
    [InlineData("release.deployed", "Deployment", "Déploiement")]
    [InlineData("ai-task.completed", "Report", "Rapport")]
    [InlineData("agent-update.completed", "Update", "Actualisation")]
    [InlineData("agent-update.failed", "Failure", "Échec")]
    [InlineData("analysis.report.completed", "Analysis", "Analyse")]
    [InlineData("analysis.gate.warning", "Warning", "Avertissement")]
    [InlineData("analysis.gate.blocked", "Blocked", "Bloqué")]
    [InlineData("analysis.gate.error", "Error", "Erreur")]
    [InlineData("analysis.cve-sync.failed", "Failure", "Échec")]
    [InlineData("analysis.cve.detected", "Vulnerability", "Vulnérabilité")]
    [InlineData("analysis.dependency-update.ready", "Pull request", "Pull request")]
    [InlineData("analysis.dependency-update.closed", "Update", "Mise à jour")]
    [InlineData("analysis.governance.expiring", "Expiration", "Expiration")]
    [InlineData("analysis.operational.cve-sync-failed", "Alert", "Alerte")]
    [InlineData("analysis.operational.cve-db-stale", "Stale", "Périmé")]
    [InlineData("analysis.operational.storage-drift", "Inconsistency", "Incohérence")]
    [InlineData("analysis.operational.scanner-obsolete", "Obsolete", "Obsolète")]
    [InlineData("analysis.operational.workspace-residual", "Cleanup", "Nettoyage")]
    [InlineData("analysis.operational.scanner-unavailable", "Unavailable", "Indisponible")]
    [InlineData("app.down", "Down", "Indisponible")]
    [InlineData("app.recovered", "Recovered", "Rétabli")]
    [InlineData("app.analytics.quota", "Quota", "Quota")]
    [InlineData("project.storage.quota", "Quota", "Quota")]
    [InlineData("app.metric.threshold", "Threshold", "Seuil")]
    [InlineData("alert.triggered", "Alert", "Alerte")]
    [InlineData("certbot.renewal-check.failed", "Failure", "Échec")]
    [InlineData("certbot.certificate-expiring", "Expiration", "Expiration")]
    [InlineData("SecretExpiring", "Expiration", "Expiration")]
    [InlineData("server.offline", "Offline", "Hors ligne")]
    [InlineData("notification.test", "Test", "Test")]
    [InlineData("something.unlisted", "Notification", "Notification")]
    public void EveryEventKind_HasItsWord_InBothLanguages_WithoutProjectOrServer(string eventType, string english, string french)
    {
        Assert.Equal($"Aetheus - {english}", Subject(eventType, "{}"));
        Assert.Equal($"Aetheus - {french}", Subject(eventType, "{}", languageJson: French));
    }

    [Fact]
    public void AProjectEvent_NamesTheProject_BetweenAetheusAndTheWord()
    {
        var payload = """{"ProjectName":"Wordmorrow","PipelineName":"deploy"}""";

        Assert.Equal("Aetheus - Wordmorrow - Approval", Subject("pipeline.approval-requested", payload));
        Assert.Equal("Aetheus - Wordmorrow - Approbation", Subject("pipeline.approval-requested", payload, languageJson: French));
    }

    [Fact]
    public void AProjectNamedOnlyByTheDeliverySubject_IsStillNamed()
    {
        Assert.Equal("Aetheus - Shop - Échec", Subject("pipeline.failed", "{}", "pipeline.failed in project Shop", French));
    }

    [Fact]
    public void AServerEvent_NamesTheServer_WhenNoProjectIsInvolved()
    {
        var payload = """{"ServerId":4,"ServerName":"vps-01"}""";

        Assert.Equal("Aetheus - vps-01 - Offline", Subject("server.offline", payload));
        Assert.Equal("Aetheus - vps-01 - Hors ligne", Subject("server.offline", payload, languageJson: French));
    }

    [Fact]
    public void WhenBothAreKnown_TheProjectWins()
    {
        Assert.Equal("Aetheus - Shop - Alert", Subject("alert.triggered", """{"ProjectName":"Shop","ServerName":"vps-01"}"""));
    }

    [Fact]
    public void ANameWithLineBreaks_StaysOnOneLine()
    {
        var subject = Subject("pipeline.failed", "{\"ProjectName\":\"Evil\\r\\nBcc: x@y.z\\tShop\"}");

        Assert.Equal("Aetheus - Evil Bcc: x@y.z Shop - Failure", subject);
        Assert.DoesNotContain('\r', subject);
        Assert.DoesNotContain('\n', subject);
    }

    [Fact]
    public void ALongName_IsCut()
    {
        var subject = Subject("pipeline.failed", "{\"ProjectName\":\"" + new string('x', 500) + "\"}");

        Assert.Equal("Aetheus - " + new string('x', 60) + " - Failure", subject);
    }

    [Fact]
    public void ABlankName_IsLeftOut()
    {
        Assert.Equal("Aetheus - Failure", Subject("pipeline.failed", "{\"ProjectName\":\" \r\n \"}"));
    }

    [Fact]
    public void AnAdministratorsCustomSubject_StaysTheirs()
    {
        var stored = new MailTemplate
        {
            Kind = MailTemplateKind.NotificationEvent,
            Language = MailTemplateLanguages.English,
            Subject = "Mine: {{Summary}}",
            Body = "<p>{{Details}}</p>"
        };

        Assert.StartsWith("Mine: Pipeline execution failed", Subject("pipeline.failed", """{"ProjectName":"Shop"}""", stored: stored), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Aetheus - Test")]
    [InlineData(French, "Aetheus - Test")]
    public void TheChannelTestMail_CarriesTheTestWord(string? languageJson, string expected)
    {
        var message = NotificationMailKit.Build(new NotificationEmail("reader@example.org", "notification.test", "notification.test", "{}"),
            languageJson: languageJson);

        Assert.Equal(expected, message.Subject);
    }
}
