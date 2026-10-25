// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications.MailTemplates;

namespace Aetheus.Back.Tests;

/// <summary>Recettes R-51 to R-55: the "Monitoring alert triggered" mail.</summary>
public sealed class AlertMailTests
{
    private const string Origin = "https://aetheus.example.org";

    internal static NotificationEmail DockerRisk => new("reader@example.org", "Sec-Audit: container x", "alert.triggered",
        """
        {"RuleName":"Sec-Audit","ServerId":5,"ServerName":"vps2577917","Metric":"DockerContainerRisk","Severity":"Critical",
         "ContainerName":"testcontainers-ryuk","Image":"testcontainers/ryuk:0.14.0","Risks":["privileged"],"IsChange":false,
         "TriggeredAt":"2026-10-22T16:07:15.7685078Z"}
        """);

    private static string Html(NotificationEmail email, string? languageJson = null) =>
        NotificationMailKit.Build(email, Origin, languageJson).HtmlBody ?? string.Empty;

    [Fact]
    public void TheMonitoringRuleRow_IsLeftOut()
    {
        var html = Html(DockerRisk);

        Assert.DoesNotContain("Monitoring rule", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Sec-Audit", html, StringComparison.Ordinal);
        Assert.Contains("Metric", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServerName_OpensTheServer_OnItsLineAndInTheTable()
    {
        var html = Html(DockerRisk);

        Assert.Matches("<a href=\"" + Origin + "/servers/5/overview\"[^>]*><strong>vps2577917</strong></a>", html);
        Assert.Matches("<a href=\"" + Origin + "/servers/5/overview\"[^>]*>vps2577917</a></td>", html);
    }

    [Theory]
    [InlineData("DockerContainerRisk", "/servers/5/docker")]
    [InlineData("RkhunterWarnings", "/servers/5/rkhunter")]
    [InlineData("BlockedIps", "/servers/5/portsentry")]
    [InlineData("CpuPercent", "/servers/5/overview")]
    public void TheButton_LeadsToTheAlert_NotOnlyToTheServer(string metric, string path)
    {
        var email = new NotificationEmail("reader@example.org", "alert.triggered", "alert.triggered",
            $$"""{"ServerId":5,"ServerName":"vps","Metric":"{{metric}}"}""");

        var mail = NotificationMailKit.Build(email, Origin);

        Assert.Contains("href=\"" + Origin + path + "\"", mail.HtmlBody, StringComparison.Ordinal);
        Assert.Contains("View alert", mail.HtmlBody, StringComparison.Ordinal);
        Assert.DoesNotContain(">View server<", mail.HtmlBody, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("DockerContainerRisk")]
    [InlineData("Cpu")]
    [InlineData("OomKill")]
    public void TheButton_OpensTheStoredAlert_WhenTheEventCarriesItsIdentifier(string metric)
    {
        var email = new NotificationEmail("reader@example.org", "alert.triggered", "alert.triggered",
            $$"""{"AlertId":7391,"ServerId":5,"ServerName":"vps","Metric":"{{metric}}"}""");

        var html = NotificationMailKit.Build(email, Origin).HtmlBody ?? string.Empty;

        Assert.Contains("href=\"" + Origin + "/alerts/7391\"", html, StringComparison.Ordinal);
        Assert.Contains("View alert", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"" + Origin + "/servers/5/docker\"", html, StringComparison.Ordinal);
        // The server's name still opens the server; the identifier is the address, not a row of the table.
        Assert.Contains("href=\"" + Origin + "/servers/5/overview\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("7391", html.Replace("/alerts/7391", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void TheButton_OpensTheStoredAlert_EvenWhenTheAlertHasNoServer()
    {
        var email = new NotificationEmail("reader@example.org", "alert.triggered", "alert.triggered",
            """{"AlertId":9,"Metric":"Cpu"}""");

        var html = NotificationMailKit.Build(email, Origin).HtmlBody ?? string.Empty;

        Assert.Contains("href=\"" + Origin + "/alerts/9\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEventOtherThanAnAlert_IgnoresAnAlertIdentifier()
    {
        var email = new NotificationEmail("reader@example.org", "server.offline", "server.offline",
            """{"AlertId":9,"ServerId":5,"ServerName":"vps"}""");

        var html = NotificationMailKit.Build(email, Origin).HtmlBody ?? string.Empty;

        Assert.DoesNotContain("/alerts/9", html, StringComparison.Ordinal);
        Assert.Contains("href=\"" + Origin + "/servers/5/overview\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTriggerInstant_IsReadableInLocalTime_AndStillGivenInUtc()
    {
        var html = Html(DockerRisk, "\"fr-FR\"");

        // 16:07 UTC is 18:07 in Paris on 22 October 2026 (summer time ended on 25 October).
        Assert.Contains("22/10/2026 18:07:15 (Europe/Paris)", html, StringComparison.Ordinal);
        Assert.Contains("2026-10-22 16:07:15", html, StringComparison.Ordinal);
        Assert.DoesNotContain("7685078", html, StringComparison.Ordinal);
        Assert.Contains("Déclenchée le (UTC)", html, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMail_EndsOnItsOwnSendTime_SoItsTailIsNotTheSameAsTheLastOne()
    {
        var mail = NotificationMailKit.Build(DockerRisk, Origin, "\"fr-FR\"");

        Assert.Matches(@"Envoyé par Aetheus le \d{2}/\d{2}/\d{4} \d{2}:\d{2}:\d{2} \(Europe/Paris\)", mail.HtmlBody);
    }

    [Fact]
    public void AnEventWithoutAServer_HasNoLinkedName()
    {
        var email = new NotificationEmail("reader@example.org", "pipeline.failed", "pipeline.failed",
            """{"PipelineName":"ci","PipelineRunId":9,"Status":"Failed"}""");

        var html = Html(email);

        Assert.Contains("<strong>ci</strong>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("/servers/", html, StringComparison.Ordinal);
    }
}
