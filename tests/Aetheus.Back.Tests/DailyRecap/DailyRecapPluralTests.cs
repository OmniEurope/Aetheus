// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.DailyRecap;

namespace Aetheus.Back.Tests;

/// <summary>Recette R-57 follow-up: every count-bearing sentence of the recap agrees with its number.</summary>
public sealed class DailyRecapPluralTests
{
    private static readonly DateTime Generated = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);

    private static DailyRecapReport OneOfEverything() => DailyRecapRenderingTests.SampleReport() with
    {
        PipelineRuns = [new RecapPipelineRun(1, "exemple-ci", null, PipelineStatus.Failed, Generated, "develop")],
        Servers = [new RecapServer(1, "exemple-vps", ServerStatus.Offline, Generated, null, AgentCompatibilityStatus.Unknown, "2.4.0", "2.4.0", null)],
        Backups = [new RecapBackup("exemple-base", "Boutique", "exemple-vps", BackupRunStatus.Failed, Generated, null)],
        Certificates = [new RecapExpiring("boutique.exemple.org", "exemple-vps", Generated.AddDays(1.5))],
        Secrets = [new RecapExpiring("STRIPE_API_KEY", "exemple-coffre", Generated.AddDays(1.5))],
        CertificateHorizonDays = 1,
        SecretHorizonDays = 1,
        Findings = new RecapFindings(new Dictionary<AnalysisSeverity, int> { [AnalysisSeverity.High] = 1 }, 1),
        MailServers = [new RecapMailServer("exemple-vps", true, true, 1, Generated)]
    };

    private static string AllText(string language, DailyRecapReport report)
    {
        var document = DailyRecapComposer.Compose(report, new DailyRecapText(CultureInfo.GetCultureInfo(language)));
        return string.Join("\n", document.Tiles.Select(tile => tile.Value + " " + tile.Label)
            .Concat(document.Blocks.SelectMany(block => new[] { block.Summary }.Concat(block.Items.Select(item => item.Title + " " + item.Badge?.Text)))));
    }

    [Fact]
    public void French_OneOfEverything_IsSingular()
    {
        var text = AllText("fr-FR", OneOfEverything());

        Assert.Contains("1 exécution de pipeline en échec", text);
        Assert.Contains("1 exécution terminée : 0 réussie, 1 en échec, 0 autre.", text);
        Assert.Contains("1 serveur, dont 1 demande de l'attention.", text);
        Assert.Contains("0 / 1 serveur en ligne", text);
        Assert.Contains("1 sauvegarde terminée : 0 réussie, 1 en échec.", text);
        Assert.Contains("1 certificat expire dans 1 jour.", text);
        Assert.Contains("1 secret de coffre expire dans 1 jour.", text);
        Assert.Contains("Dans 1 jour", text);
        Assert.Contains("1 nouveau constat (1 élevé), 1 résolu.", text);
        Assert.Contains("1 nouveau", text);
    }

    [Fact]
    public void French_SeveralOfEverything_IsPlural()
    {
        var report = DailyRecapRenderingTests.SampleReport() with
        {
            PipelineRuns =
            [
                new RecapPipelineRun(1, "a", null, PipelineStatus.Failed, Generated, null),
                new RecapPipelineRun(2, "b", null, PipelineStatus.Failed, Generated, null),
                new RecapPipelineRun(3, "c", null, PipelineStatus.Success, Generated, null),
                new RecapPipelineRun(4, "d", null, PipelineStatus.Success, Generated, null),
                new RecapPipelineRun(5, "e", null, PipelineStatus.Partial, Generated, null),
                new RecapPipelineRun(6, "f", null, PipelineStatus.Partial, Generated, null)
            ]
        };

        var text = AllText("fr-FR", report);

        Assert.Contains("6 exécutions terminées : 2 réussies, 2 en échec, 2 autres.", text);
        Assert.Contains("2 exécutions de pipeline réussies", text);
        Assert.Contains("2 exécutions de pipeline en échec", text);
        Assert.Contains("3 serveurs, dont 3 demandent de l'attention.", text);
        Assert.Contains("2 sauvegardes terminées : 1 réussie, 1 en échec.", text);
        Assert.Contains("1 certificat expire dans 30 jours.", text);
        Assert.Contains("1 secret de coffre expire dans 14 jours.", text);
        Assert.Contains("7 nouveaux constats (2 élevés, 5 moyens), 3 résolus.", text);
    }

    [Fact]
    public void English_OneOfEverything_IsSingular_AndZeroIsPlural()
    {
        var text = AllText("en", OneOfEverything());

        Assert.Contains("1 failed pipeline run", text);
        Assert.Contains("1 run finished: 0 succeeded, 1 failed, 0 other.", text);
        Assert.Contains("1 server, 1 needing attention.", text);
        Assert.Contains("1 backup finished: 0 succeeded, 1 failed.", text);
        Assert.Contains("1 certificate expires within 1 day.", text);
        Assert.Contains("1 vault secret expires within 1 day.", text);
        Assert.Contains("In 1 day", text);
        Assert.Contains("1 new finding (1 high), 1 resolved.", text);
        Assert.DoesNotContain("1 failed pipeline runs", text);
    }

    [Theory]
    [InlineData("fr-FR", 0, true)]
    [InlineData("fr-FR", 1, true)]
    [InlineData("fr-FR", 1.5, true)]
    [InlineData("fr-FR", 2, false)]
    [InlineData("en", 0, false)]
    [InlineData("en", 1, true)]
    [InlineData("en", 2, false)]
    public void ZeroAndOne_AreSingularInFrench_OnlyOneIsSingularInEnglish(string culture, double count, bool singular) =>
        Assert.Equal(singular, new DailyRecapText(CultureInfo.GetCultureInfo(culture)).IsSingular(count));

    [Fact]
    public void ABackupLineOfTheDay_AtZeroFailures_ReadsZeroFailureInFrench()
    {
        var report = DailyRecapRenderingTests.SampleReport() with
        {
            Backups = [new RecapBackup("exemple-base", "Boutique", "exemple-vps", BackupRunStatus.Succeeded, Generated, null)]
        };

        Assert.Contains("1 sauvegarde terminée : 1 réussie, 0 en échec.", AllText("fr-FR", report));
    }
}
