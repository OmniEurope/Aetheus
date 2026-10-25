// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Data;
using Aetheus.Back.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>Recettes R-55, R-57 and R-58 follow-up: the alert mail shows words, never a property name or an identifier.</summary>
public sealed class AlertMailLocalisationTests
{
    private const string Origin = "https://aetheus.example.org";

    /// <summary>One payload per producer of <c>alert.triggered</c>, as the back serialises them.</summary>
    private static readonly Dictionary<string, string> Payloads = new()
    {
        {
            "docker",
            """
            {"RuleName":"Sec-Audit","ServerId":5,"ServerName":"vps2577917","Metric":"DockerContainerRisk","Severity":"Critical",
             "ContainerName":"testcontainers-ryuk","Image":"testcontainers/ryuk:0.14.0",
             "Risks":"privileged, Docker socket mounted, host PID namespace, host network, capabilities SYS_ADMIN,NET_ADMIN",
             "Message":"Container testcontainers-ryuk (testcontainers/ryuk:0.14.0) runs with host access: privileged. Verify it is intended.",
             "IsChange":true,"TriggeredAt":"2026-10-22T16:07:15.7685078Z"}
            """
        },
        {
            "rule",
            """
            {"RuleName":"CPU haute","ServerId":5,"Metric":"DiskFree","Operator":"LessThanOrEqual","Threshold":10.0,"SustainedSeconds":300}
            """
        },
        {
            "oom",
            """
            {"RuleName":"OOM","ServerId":5,"ServerName":"vps2577917","Metric":"OomKill","Severity":"Critical","KillCount":2,
             "Victims":["java (pid 4242)"],"Message":"OOM kill on vps2577917: java (pid 4242) and 1 more not named.","TriggeredAt":"2026-10-22T16:07:15Z"}
            """
        },
        {
            "ssh",
            """
            {"RuleName":"Sec-Audit","ServerId":5,"ServerName":"vps2577917","Metric":"SshConfigChange","Severity":"Warning",
             "Message":"PermitRootLogin changed to yes (was no); new key added to /root/.ssh/authorized_keys (ssh-ed25519 SHA256:abc)",
             "Changes":[{"Code":"SettingChanged","Args":["PermitRootLogin","yes","no"]},
                        {"Code":"KeyAdded","Args":["/root/.ssh/authorized_keys","ssh-ed25519","SHA256:abc"]}],
             "TriggeredAt":"2026-10-22T16:07:15Z"}
            """
        },
        {
            "sudoers",
            """
            {"RuleName":"Sec-Audit","ServerId":5,"ServerName":"vps2577917","Metric":"SudoersDrift","Severity":"Critical",
             "Files":"/etc/sudoers.d/aetheus, /etc/sudoers.d/ops","Message":"Sudoers drop-in changed out-of-band: /etc/sudoers.d/aetheus. Verify the change was authorized; the install baseline no longer matches.","IsReminder":false,"TriggeredAt":"2026-10-22T16:07:15Z"}
            """
        }
    };

    private static string Html(string payload, string languageJson) => NotificationMailKit.Build(
        new NotificationEmail("reader@example.org", "alert.triggered", "alert.triggered", payload), Origin, languageJson).HtmlBody ?? string.Empty;

    [Theory]
    [InlineData("docker")]
    [InlineData("rule")]
    [InlineData("oom")]
    [InlineData("ssh")]
    [InlineData("sudoers")]
    public void EveryAlertField_HasALabel_InFrench_AndNoRawPropertyNameIsShown(string producer)
    {
        var html = Html(Payloads[producer], "\"fr-FR\"");

        foreach (var raw in new[]
        {
            "ContainerName", "IsChange", "IsReminder", "KillCount", "Victims", "SustainedSeconds", "ServerId", "ServerName",
            "TriggeredAt", ">Risks<", ">Metric<", ">Operator<", ">Files<", "Identifiant du serveur", "Server ID"
        })
            Assert.DoesNotContain(raw, html, StringComparison.Ordinal);
        Assert.True(html.Contains("<td", StringComparison.Ordinal), producer);
    }

    [Fact]
    public void ADockerContainerAlert_ShowsFrenchLabelsAndValues()
    {
        var html = System.Net.WebUtility.HtmlDecode(Html(Payloads["docker"], "\"fr-FR\""));

        foreach (var expected in new[]
        {
            ">Conteneur<", ">Accès à l'hôte<", ">Changement depuis la dernière alerte<",
            "Risque d'un conteneur Docker", ">Critique<",
            "conteneur privilégié, socket Docker monté, espace de processus de l'hôte, réseau de l'hôte, capacités SYS_ADMIN,NET_ADMIN"
        })
            Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.DoesNotContain("DockerContainerRisk", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">privileged", html, StringComparison.Ordinal);
        // The English message only repeats the table.
        Assert.DoesNotContain("Verify it is intended", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ARuleAlert_ShowsTheComparisonAndTheMetricInWords()
    {
        var html = Html(Payloads["rule"], "\"fr-FR\"");

        Assert.Contains("Disque (espace libre)", html, StringComparison.Ordinal);
        Assert.Contains("inférieur ou égal à", html, StringComparison.Ordinal);
        Assert.DoesNotContain("LessThanOrEqual", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">DiskFree<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheEnglishMail_ShowsEnglishWords()
    {
        var html = Html(Payloads["docker"], "\"en\"");

        Assert.Contains("Docker container risk", html, StringComparison.Ordinal);
        Assert.Contains("privileged container, Docker socket mounted", html, StringComparison.Ordinal);
        Assert.Contains(">Container<", html, StringComparison.Ordinal);
        Assert.Contains(">Critical<", html, StringComparison.Ordinal);
    }

    [Fact]
    public void TheServerIdentifierRow_IsDropped_WhenTheServerNameLinksToTheServer()
    {
        var html = Html(Payloads["docker"], "\"en\"");

        Assert.DoesNotContain("Server ID", html, StringComparison.Ordinal);
        Assert.Contains(Origin + "/servers/5/overview", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docker", "\"fr-FR\"")]
    [InlineData("rule", "\"en\"")]
    [InlineData("oom", "\"fr-FR\"")]
    [InlineData("ssh", "\"en\"")]
    [InlineData("sudoers", "\"fr-FR\"")]
    public void TheRawSourceEventKey_IsNeverShown_BecauseTheBannerStatesTheEvent(string producer, string languageJson)
    {
        var html = Html(Payloads[producer], languageJson);

        Assert.DoesNotContain("alert.triggered", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Événement source", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Source event", html, StringComparison.Ordinal);
    }

    private static readonly string[] EnglishSentences =
    [
        "Sudoers drop-in", "Verify the change", "OOM kill on", "more not named", "changed to", "new key added", "was killed"
    ];

    [Fact]
    public void AnOomAlert_IsWordedInFrench_FromItsStructuredFields()
    {
        var html = System.Net.WebUtility.HtmlDecode(Html(Payloads["oom"], "\"fr-FR\""));

        Assert.Contains("Arrêtés par manque de mémoire : java (pid 4242) et 1 autre(s) non nommé(s).", html, StringComparison.Ordinal);
        foreach (var english in EnglishSentences) Assert.DoesNotContain(english, html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOomAlert_IsWordedInEnglish_FromItsStructuredFields()
    {
        var html = Html(Payloads["oom"], "\"en\"");

        Assert.Contains("Killed for lack of memory: java (pid 4242) and 1 more not named.", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOomAlert_WithoutNamedVictims_SaysSo()
    {
        const string payload = """{"ServerId":5,"ServerName":"vps","Metric":"OomKill","KillCount":3,"Victims":[],"Message":"3 OOM kills on vps; processes not named.","TriggeredAt":"2026-10-22T16:07:15Z"}""";

        var html = System.Net.WebUtility.HtmlDecode(Html(payload, "\"fr-FR\""));

        Assert.Contains("3 processus ont été arrêtés par manque de mémoire", html, StringComparison.Ordinal);
        Assert.DoesNotContain("OOM kills on", html, StringComparison.Ordinal);
    }

    [Fact]
    public void ASudoersAlert_IsWordedInFrench_AndInEnglish()
    {
        var french = System.Net.WebUtility.HtmlDecode(Html(Payloads["sudoers"], "\"fr-FR\""));
        var english = Html(Payloads["sudoers"], "\"en\"");

        Assert.Contains("Fichier sudoers modifié hors procédure : /etc/sudoers.d/aetheus, /etc/sudoers.d/ops.", french, StringComparison.Ordinal);
        foreach (var sentence in EnglishSentences) Assert.DoesNotContain(sentence, french, StringComparison.Ordinal);
        Assert.Contains("Sudoers drop-in changed out-of-band: /etc/sudoers.d/aetheus, /etc/sudoers.d/ops.", english, StringComparison.Ordinal);
    }

    [Fact]
    public void AnSshAlert_IsWordedInFrench_FromItsStructuredChanges()
    {
        var html = System.Net.WebUtility.HtmlDecode(Html(Payloads["ssh"], "\"fr-FR\""));

        Assert.Contains("PermitRootLogin est passé à yes (auparavant no).", html, StringComparison.Ordinal);
        Assert.Contains("Nouvelle clé ajoutée à /root/.ssh/authorized_keys (ssh-ed25519 SHA256:abc).", html, StringComparison.Ordinal);
        foreach (var english in EnglishSentences) Assert.DoesNotContain(english, html, StringComparison.Ordinal);
        Assert.DoesNotContain("SettingChanged", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AnSshAlert_IsWordedInEnglish_FromItsStructuredChanges()
    {
        var html = Html(Payloads["ssh"], "\"en\"");

        Assert.Contains("PermitRootLogin changed to yes (was no).", html, StringComparison.Ordinal);
        Assert.Contains("New key added to /root/.ssh/authorized_keys (ssh-ed25519 SHA256:abc).", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("en")]
    public void AnSshAlert_WithoutStructuredChanges_ShowsNoEnglishSentence(string language)
    {
        const string legacy = """{"ServerId":5,"ServerName":"vps","Metric":"SshConfigChange","Severity":"Warning","Message":"PermitRootLogin changed to yes (was no)","TriggeredAt":"2026-10-22T16:07:15Z"}""";

        Assert.DoesNotContain("was no", Html(legacy, $"\"{language}\""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("server.offline", """{"ServerId":5,"ServerName":"vps2577917"}""", "overview")]
    [InlineData("agent-update.completed", """{"ServerId":5,"ServerName":"vps2577917","FromVersion":"1.0","ToVersion":"1.1"}""", "overview")]
    [InlineData("agent-update.failed", """{"ServerId":5,"ServerName":"vps2577917","FromVersion":"1.0","ToVersion":"1.1"}""", "overview")]
    [InlineData("alert.triggered", """{"ServerId":5,"ServerName":"vps2577917","Metric":"OomKill","TriggeredAt":"2026-10-22T16:07:15Z"}""", "overview")]
    public void ForEveryServerEvent_TheServerIdentifierIsDropped_AndTheServerNameLinks(string eventType, string payload, string page)
    {
        foreach (var language in new[] { "\"fr-FR\"", "\"en\"" })
        {
            var html = NotificationMailKit.Build(new NotificationEmail("reader@example.org", eventType, eventType, payload), Origin, language)
                .HtmlBody ?? string.Empty;

            Assert.DoesNotContain("Identifiant du serveur", html, StringComparison.Ordinal);
            Assert.DoesNotContain("Server ID", html, StringComparison.Ordinal);
            Assert.DoesNotContain("ServerId", html, StringComparison.Ordinal);
            var link = $"<a href=\"{Origin}/servers/5/{page}\"[^>]*>";
            Assert.Matches(link + "<strong>vps2577917</strong></a>", html);
            Assert.Matches(link + "vps2577917</a>", html);
        }
    }

    [Fact]
    public void ACertbotFailure_DropsTheServerIdentifier_AndLinksItsCertbotPage()
    {
        const string payload = """{"ServerId":5,"CheckedAt":"2026-10-22T16:07:15Z"}""";

        var html = NotificationMailKit.Build(new NotificationEmail("reader@example.org", "certbot.renewal-check.failed", "certbot.renewal-check.failed", payload), Origin, "\"fr-FR\"")
            .HtmlBody ?? string.Empty;

        Assert.DoesNotContain("Identifiant du serveur", html, StringComparison.Ordinal);
        Assert.DoesNotContain("CheckedAt", html, StringComparison.Ordinal);
        Assert.DoesNotContain("certbot.renewal-check.failed", html, StringComparison.Ordinal);
        Assert.Contains(Origin + "/servers/5/certbot", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheMailTimes_AreStatedInThePlatformTimeZone_ReadOncePerBatch()
    {
        var reads = 0;
        MailTimeZoneResolver zone = _ =>
        {
            reads++;
            return Task.FromResult(TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        };
        var sender = Substitute.For<IEmailSender>();
        IReadOnlyList<EmailMessage>? sent = null;
        sender.SendBatchAsync(Arg.Any<IReadOnlyList<EmailMessage>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                sent = call.Arg<IReadOnlyList<EmailMessage>>();
                return (IReadOnlyList<EmailSendResult>)sent.Select(_ => EmailSendResult.Sent("no-reply@example.org")).ToList();
            });
        var payload = """{"ServerId":5,"ServerName":"vps","Metric":"OomKill","TriggeredAt":"2026-10-22T16:07:15Z"}""";
        var emails = new[]
        {
            new NotificationEmail("a@example.org", "alert.triggered", "alert.triggered", payload),
            new NotificationEmail("b@example.org", "alert.triggered", "alert.triggered", payload)
        };

        await new NotificationEmailer(sender, NotificationMailKit.Templates("\"fr-FR\""), timeZones: zone)
            .SendAsync(emails, TestContext.Current.CancellationToken);

        Assert.NotNull(sent);
        Assert.Equal(2, sent.Count);
        foreach (var message in sent)
        {
            // 16:07 UTC is 12:07 in New York (daylight time) and not 18:07 in Paris.
            Assert.Contains("22/10/2026 12:07:15 (America/New_York)", message.HtmlBody, StringComparison.Ordinal);
            Assert.DoesNotContain("Europe/Paris", message.HtmlBody, StringComparison.Ordinal);
        }
        Assert.Equal(1, reads);
    }
}

/// <summary>The platform time zone is the daily recap's setting, with the recap service's UTC fallback.</summary>
public sealed class DailyRecapMailTimeZoneProviderTests
{
    private static async Task<string> ZoneOfAsync(string? zoneId)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 10, 9, 5, 0, 0, TimeSpan.Zero));
        using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options, clock);
        var repo = new DailyRecapRepository(db);
        if (zoneId is not null)
            await repo.SaveSettingRowsAsync(new Dictionary<string, string> { [DailyRecapSettingKeys.TimeZone] = zoneId },
                clock.GetUtcNow().UtcDateTime, TestContext.Current.CancellationToken);
        var provider = new DailyRecapMailTimeZoneProvider(repo, NullLogger<DailyRecapMailTimeZoneProvider>.Instance);
        return (await provider.GetAsync(TestContext.Current.CancellationToken)).Id;
    }

    [Fact]
    public async Task TheSettingIsRead() => Assert.Equal("America/New_York", await ZoneOfAsync("America/New_York"));

    [Fact]
    public async Task WithoutASetting_TheRecapDefaultIsUsed() => Assert.Equal("Europe/Paris", await ZoneOfAsync(null));

    [Fact]
    public async Task AnUnknownZone_FallsBackToUtc() => Assert.Equal(TimeZoneInfo.Utc.Id, await ZoneOfAsync("Mars/Olympus"));
}
