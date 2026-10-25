// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Components.Notifications.MailTemplates;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Tests;

/// <summary>The recap e-mail: its sections follow the settings, its statuses carry words, its data is encoded.</summary>
public sealed class DailyRecapRenderingTests
{
    private static readonly DateTime Generated = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);

    private static TimeZoneInfo Paris
    {
        get
        {
            Assert.True(DailyRecapClock.TryResolveTimeZone("Europe/Paris", out var zone));
            return zone;
        }
    }

    /// <summary>A realistic day made of fictitious data, used by the tests and by the sample written for review.</summary>
    internal static DailyRecapReport SampleReport(IReadOnlySet<DailyRecapSection>? sections = null) => new()
    {
        Day = new DateOnly(2026, 10, 8),
        GeneratedAtUtc = Generated,
        TimeZone = Paris,
        Sections = sections ?? DailyRecapSettings.AllSections,
        CertificateHorizonDays = 30,
        SecretHorizonDays = 14,
        PipelineRuns =
        [
            new RecapPipelineRun(4821, "exemple-release-candidate", "Boutique (exemple)", PipelineStatus.Failed, new DateTime(2026, 10, 8, 16, 42, 0, DateTimeKind.Utc), "develop"),
            new RecapPipelineRun(4818, "exemple-ci", "Boutique (exemple)", PipelineStatus.Success, new DateTime(2026, 10, 8, 9, 12, 0, DateTimeKind.Utc), "develop"),
            new RecapPipelineRun(4819, "exemple-nightly", null, PipelineStatus.Success, new DateTime(2026, 10, 8, 1, 30, 0, DateTimeKind.Utc), "main"),
            new RecapPipelineRun(4820, "exemple-qualification", "Portail (exemple)", PipelineStatus.Partial, new DateTime(2026, 10, 8, 13, 5, 0, DateTimeKind.Utc), "develop")
        ],
        Servers =
        [
            new RecapServer(1, "exemple-vps-prod", ServerStatus.Online, Generated, new RecapOsUpdates(true, 6, 2),
                AgentCompatibilityStatus.UpToDate, "2.4.0", "2.4.0", new RecapDisk(6.2, 80, 92.3, RecapDiskLevel.Critical, Generated)),
            new RecapServer(2, "exemple-vps-staging", ServerStatus.Online, Generated, new RecapOsUpdates(true, 0, 0),
                AgentCompatibilityStatus.UpdateRecommended, "2.3.1", "2.4.0", new RecapDisk(41.5, 80, 48.1, RecapDiskLevel.Ok, Generated)),
            new RecapServer(3, "exemple-poste-build", ServerStatus.Offline, Generated.AddHours(-9), null,
                AgentCompatibilityStatus.Unknown, "2.2.0", "2.4.0", null)
        ],
        Releases = [new RecapRelease("Boutique (exemple)", "1.14.0", ReleaseStatus.Promoted, new DateTime(2026, 10, 8, 10, 0, 0, DateTimeKind.Utc))],
        Backups =
        [
            new RecapBackup("exemple-base-nuit", "Boutique (exemple)", "exemple-vps-prod", BackupRunStatus.Succeeded, new DateTime(2026, 10, 8, 1, 5, 0, DateTimeKind.Utc), null),
            new RecapBackup("exemple-fichiers", "Portail (exemple)", "exemple-vps-staging", BackupRunStatus.Failed, new DateTime(2026, 10, 8, 2, 0, 0, DateTimeKind.Utc), "tar: /srv/uploads: Permission denied")
        ],
        Certificates = [new RecapExpiring("boutique.exemple.org", "exemple-vps-prod", Generated.AddDays(12))],
        Secrets = [new RecapExpiring("STRIPE_API_KEY", "exemple-coffre-prod", Generated.AddDays(4))],
        Findings = new RecapFindings(new Dictionary<AnalysisSeverity, int> { [AnalysisSeverity.High] = 2, [AnalysisSeverity.Medium] = 5 }, 3),
        MailServers = [new RecapMailServer("exemple-vps-prod", true, true, 3, Generated.AddMinutes(-3))]
    };

    private static (RecapDocument Document, string Html, string Text) Render(DailyRecapReport report, DailyRecapLanguage language = DailyRecapLanguage.French)
    {
        var culture = language == DailyRecapLanguage.English ? CultureInfo.GetCultureInfo("en") : CultureInfo.GetCultureInfo("fr-FR");
        var document = DailyRecapComposer.Compose(report, new DailyRecapText(culture));
        var mail = RenderMail(document, culture.Name);
        return (document, mail.Html, mail.Text);
    }

    /// <summary>The recap as it is sent: its default template rendered with the recap's values and blocks (recette R1-75).</summary>
    internal static OmniMailRenderResult RenderMail(RecapDocument document, string languageTag)
    {
        var language = MailTemplateLanguages.Normalize(languageTag);
        var (subject, body) = MailTemplateDefaults.For(MailTemplateKind.DailyRecap, language);
        return MailTemplateRenderer.Render(subject, body, DailyRecapHtmlRenderer.Data(document),
            MailRecipientStyle.Default with { Language = language });
    }

    [Fact]
    public void RunAndServerLinks_OpenTheReportedResources_InBothAlternatives()
    {
        var document = DailyRecapComposer.Compose(SampleReport(),
            new DailyRecapText(CultureInfo.GetCultureInfo("fr-FR")), "https://app.example.org");
        var html = RenderMail(document, "fr-FR").Html;
        var text = RenderMail(document, "fr-FR").Text;

        foreach (var url in new[] { "https://app.example.org/pipelines/runs/4821", "https://app.example.org/servers/1/overview" })
        {
            Assert.Contains(url, html);
            Assert.Contains(url, text);
        }
    }

    [Fact]
    public void EverySection_IsRendered_InFrenchByDefault()
    {
        var (document, html, text) = Render(SampleReport());

        Assert.Equal(8, document.Blocks.Count);
        Assert.Contains("Ce qui s'est passé le jeudi 8 octobre 2026", html);
        foreach (var title in new[] { "Pipelines", "Serveurs", "Versions", "Sauvegardes", "Certificats", "Secrets des coffres", "Constats de sécurité et de qualité", "Serveurs de messagerie" })
        {
            Assert.Contains($">{title}</h2>", html);
            Assert.Contains(title, text);
        }
    }

    [Fact]
    public void SectionsSwitchedOff_AreLeftOut()
    {
        var (document, html, _) = Render(SampleReport(new HashSet<DailyRecapSection> { DailyRecapSection.Pipelines }));

        Assert.Single(document.Blocks);
        Assert.DoesNotContain(">Serveurs</h2>", html);
        Assert.DoesNotContain("exemple-vps-staging", html);
        Assert.Contains("exemple-release-candidate", html);
    }

    [Fact]
    public void Statuses_CarryTheirWords_NotOnlyAColour()
    {
        var (_, html, text) = Render(SampleReport());

        Assert.Contains(">Échec</span>", html);
        Assert.Contains(">Réussi</span>", html);
        Assert.Contains(">Partiel</span>", html);
        Assert.Contains(">Espace critique</span>", html);
        Assert.Contains(">Mise à jour conseillée</span>", html);
        Assert.Contains(">Hors ligne</span>", html);
        Assert.Contains("Échec exemple-release-candidate", text);
        Assert.Contains("Espace critique 6,2 Go libres", text);
    }

    [Fact]
    public void Failures_ComeBeforeSuccesses()
    {
        var (document, _, _) = Render(SampleReport());

        var pipelines = document.Blocks[0];
        Assert.Equal("exemple-release-candidate", pipelines.Items[0].Title);
        Assert.Equal(RecapTone.Danger, pipelines.Items[0].Badge!.Tone);
        Assert.Equal(RecapTone.Success, pipelines.Items[^1].Badge!.Tone);
    }

    [Fact]
    public void Html_IsMadeForMailClients_InlineStyles_NoStyleElement_NoSideStripe()
    {
        var (_, html, _) = Render(SampleReport());

        Assert.DoesNotContain("<style", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("border-left", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("border-inline-start", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("max-width:640px", html);
        Assert.Contains("name=\"viewport\"", html);
        Assert.Contains("role=\"presentation\"", html);
    }

    [Fact]
    public void Data_IsHtmlEncoded()
    {
        var report = SampleReport() with
        {
            PipelineRuns = [new RecapPipelineRun(1, "<script>alert(1)</script>", null, PipelineStatus.Success, Generated, null)]
        };

        var (_, html, _) = Render(report);

        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
    }

    [Fact]
    public void EmptySections_AndZeroFigures_AreLeftOut()
    {
        var report = SampleReport() with
        {
            PipelineRuns = [new RecapPipelineRun(7, "exemple-ci", null, PipelineStatus.Success, Generated, "develop")],
            Backups = [],
            Releases = [],
            Findings = new RecapFindings(new Dictionary<AnalysisSeverity, int>(), 0)
        };

        var (document, html, text) = Render(report);

        Assert.Equal(5, document.Blocks.Count);
        Assert.DoesNotContain(">Sauvegardes</h2>", html);
        Assert.DoesNotContain(">Versions</h2>", html);
        Assert.DoesNotContain(">Constats de sécurité et de qualité</h2>", html);
        Assert.DoesNotContain("Sauvegardes", text);
        // One run succeeded, none failed: the failed tile is not drawn with a 0.
        Assert.DoesNotContain(document.Tiles, tile => tile.Value == "0");
        Assert.Contains(document.Tiles, tile => tile.Label == "exécution de pipeline réussie" && tile.Value == "1");
        // The sample mail server queue holds 3 messages: no "0 messages" line is drawn for an empty one.
        Assert.DoesNotContain("0 messages", html);
    }

    [Fact]
    public void QuietDay_SaysSo_InOneBlock()
    {
        var report = SampleReport() with
        {
            PipelineRuns = [],
            Servers = [],
            Releases = [],
            Backups = [],
            Certificates = [],
            Secrets = [],
            Findings = new RecapFindings(new Dictionary<AnalysisSeverity, int>(), 0),
            MailServers = []
        };

        var (document, html, _) = Render(report);

        Assert.Single(document.Blocks);
        Assert.Empty(document.Tiles);
        Assert.Contains(">Rien à signaler</h2>", html);
    }

    [Fact]
    public void SettingsLink_OpensTheRecapTab_WhenTheFrontIsKnown()
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        var document = DailyRecapComposer.Compose(SampleReport(), new DailyRecapText(culture), "https://app.example.org/");

        var html = RenderMail(document, culture.Name).Html;
        var text = RenderMail(document, "fr-FR").Text;

        Assert.Contains("href=\"https://app.example.org/admin/settings?tab=daily-recap\"", html);
        Assert.Contains(">Régler le récap</a>", html);
        Assert.Contains("Régler le récap (https://app.example.org/admin/settings?tab=daily-recap)", text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("app.example.org")]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:password@app.example.org")]
    public void SettingsLink_IsLeftOut_WithoutAnHttpFront(string? front)
    {
        var document = DailyRecapComposer.Compose(SampleReport(), new DailyRecapText(CultureInfo.GetCultureInfo("fr-FR")), front);

        Assert.Null(document.SettingsLink);
        Assert.DoesNotContain("href=", RenderMail(document, "fr-FR").Html);
    }

    [Fact]
    public void WaitingMail_IsStillReported()
    {
        var report = SampleReport() with { MailServers = [new RecapMailServer("exemple-vps-prod", true, true, 4, Generated)] };

        var (_, html, _) = Render(report);

        Assert.Contains("4 messages", html);
    }

    private static (string Html, RecapDocument Document) RenderLinked(DailyRecapReport report)
    {
        var culture = CultureInfo.GetCultureInfo("fr-FR");
        var document = DailyRecapComposer.Compose(report, new DailyRecapText(culture), "https://app.example.org");
        return (RenderMail(document, culture.Name).Html, document);
    }

    [Fact]
    public void Header_KeepsTheDay_AndDropsThePeriodAndTheGenerationTime()
    {
        var (_, html, text) = Render(SampleReport());

        Assert.Contains("Ce qui s'est passé le jeudi 8 octobre 2026", html);
        foreach (var gone in new[] { "Journée de", "Généré le", "Europe/Paris" })
        {
            Assert.DoesNotContain(gone, html);
            Assert.DoesNotContain(gone, text);
        }
    }

    [Fact]
    public void Tiles_AreStacked_OneRowEach_WithTheirFigureOnOneLine()
    {
        var (_, html, _) = Render(SampleReport());

        // No row of side-by-side cells: that is what split "1 / 1" over two lines on a phone.
        Assert.DoesNotContain("width:33%", html);
        Assert.DoesNotContain("width:50%", html);
        Assert.Contains("2&nbsp;/&nbsp;3", html);
        Assert.DoesNotContain("2 / 3", html);
        var tileRows = System.Text.RegularExpressions.Regex.Matches(html, @"<td[^>]*padding:\s*12px[^>]*>\s*<span[^>]*font-size:\s*22px");
        Assert.Equal(3, tileRows.Count);
    }

    [Fact]
    public void EachRun_IsAtMostTwoLines_AndItsNameOpensTheRun()
    {
        var (html, document) = RenderLinked(SampleReport());

        Assert.Contains("<a href=\"https://app.example.org/pipelines/runs/4821\"", html);
        Assert.Contains("<strong>exemple-release-candidate</strong></a>", html);
        Assert.DoesNotContain("Voir l'exécution et les journaux", html);
        Assert.DoesNotContain("Voir l’exécution et les journaux", html);
        Assert.All(document.Blocks[0].Items, item =>
        {
            Assert.Empty(item.Lines);
            Assert.True(item.TitleIsLink);
            Assert.Contains("exécution n° ", item.Subtitle);
        });
    }

    [Fact]
    public void ThePipelinesSentence_ExplainingParentsOnly_IsGone()
    {
        var (document, html, text) = Render(SampleReport());

        Assert.Null(document.Blocks[0].Note);
        Assert.DoesNotContain("Pipelines parents uniquement", html);
        Assert.DoesNotContain("Pipelines parents uniquement", text);
        Assert.Contains("4 exécutions terminées", html);
    }

    [Fact]
    public void Findings_AreSummarisedBySeverity_WithOneLinkAndNoItem()
    {
        var report = SampleReport() with
        {
            Findings = new RecapFindings(new Dictionary<AnalysisSeverity, int>
            {
                [AnalysisSeverity.Critical] = 3,
                [AnalysisSeverity.High] = 5,
                [AnalysisSeverity.Medium] = 12,
                [AnalysisSeverity.Low] = 0
            }, 2)
        };
        var (html, document) = RenderLinked(report);

        var findings = document.Blocks.Single(block => block.Title == "Constats de sécurité et de qualité");
        Assert.Empty(findings.Items);
        Assert.Equal("20 nouveaux constats (3 critiques, 5 élevés, 12 moyens), 2 résolus.", findings.Summary);
        Assert.DoesNotContain("faibles", findings.Summary);
        Assert.Contains("20 nouveaux constats (3 critiques, 5 élevés, 12 moyens), 2 résolus.", html);
        Assert.Equal("https://app.example.org/analysis", findings.Link?.Url);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "href=\"https://app.example.org/analysis\""));
        Assert.Contains(">Voir les constats</a>", html);
    }

    [Fact]
    public void Findings_Summary_AgreesWithTheCountOfEachSeverity()
    {
        var report = SampleReport() with
        {
            Findings = new RecapFindings(new Dictionary<AnalysisSeverity, int> { [AnalysisSeverity.Critical] = 1, [AnalysisSeverity.High] = 1 }, 0)
        };
        var (document, _, _) = Render(report);

        var findings = document.Blocks.Single(block => block.Title == "Constats de sécurité et de qualité");
        Assert.Equal("2 nouveaux constats (1 critique, 1 élevé), 0 résolu.", findings.Summary);
    }

    [Fact]
    public void TheMailServerSection_IsHidden_WhenThereIsNothingToReport()
    {
        var healthy = SampleReport() with { MailServers = [new RecapMailServer("exemple-vps-prod", true, true, 0, Generated)] };

        var (document, html, text) = Render(healthy);

        Assert.DoesNotContain(document.Blocks, block => block.Title == "Serveurs de messagerie");
        Assert.DoesNotContain("Serveurs de messagerie", html);
        Assert.DoesNotContain("Serveurs de messagerie", text);
    }

    [Theory]
    [InlineData(false, true, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 2)]
    public void TheMailServerSection_IsShown_WhenAServiceStoppedOrMailWaits(bool postfix, bool dovecot, int queue)
    {
        var report = SampleReport() with { MailServers = [new RecapMailServer("exemple-vps-prod", postfix, dovecot, queue, Generated)] };

        var (_, html, _) = Render(report);

        Assert.Contains(">Serveurs de messagerie</h2>", html);
    }

    [Fact]
    public void TheFooter_KeepsTheSenderAndTheSettingsLink_Only()
    {
        var (html, _) = RenderLinked(SampleReport());

        Assert.Contains("Envoyé par Aetheus", html);
        Assert.Contains(">Régler le récap</a>", html);
        foreach (var gone in new[] { "no-reply", "Destinataires", "heure d'envoi", "Paramètres de la plateforme" })
            Assert.DoesNotContain(gone, html);
    }

    [Fact]
    public void English_IsAvailable()
    {
        var (document, html, _) = Render(SampleReport(), DailyRecapLanguage.English);

        Assert.StartsWith("Aetheus daily recap", document.Subject);
        Assert.Contains(">Servers</h2>", html);
        Assert.Contains(">Failed</span>", html);
        Assert.Contains("lang=\"en\"", html);
    }

    [Fact]
    public void ExpiringSoon_TurnsRed_WithinAWeek()
    {
        var (document, _, _) = Render(SampleReport());

        var secrets = document.Blocks.Single(block => block.Title == "Secrets des coffres");
        Assert.Equal(RecapTone.Danger, secrets.Items[0].Badge!.Tone);
        var certificates = document.Blocks.Single(block => block.Title == "Certificats");
        Assert.Equal(RecapTone.Warning, certificates.Items[0].Badge!.Tone);
    }

    /// <summary>
    /// Writes the sample recap for a human review when AETHEUS_DAILY_RECAP_SAMPLE names a file; the
    /// page carries a banner saying its data is fictitious. Without the variable it only renders.
    /// </summary>
    [Fact]
    public void Sample_RendersAndCanBeSavedForReview()
    {
        var document = DailyRecapComposer.Compose(SampleReport(),
            new DailyRecapText(CultureInfo.GetCultureInfo("fr-FR")), "https://aetheus.example.org");
        var html = RenderMail(document, "fr-FR").Html;
        const string Banner = "<div style=\"background:#fff3d6;color:#7a4b00;font-family:Arial,sans-serif;font-size:13px;padding:8px 12px;text-align:center;\">EXEMPLE - données fictives, généré par DailyRecapRenderingTests</div>";
        var bodyEnd = html.IndexOf('>', html.IndexOf("<body", StringComparison.Ordinal)) + 1;
        var sample = html.Insert(bodyEnd, Banner);
        Assert.Contains("EXEMPLE - données fictives", sample);

        var path = Environment.GetEnvironmentVariable("AETHEUS_DAILY_RECAP_SAMPLE");
        if (!string.IsNullOrWhiteSpace(path)) File.WriteAllText(path, sample);
    }
}
