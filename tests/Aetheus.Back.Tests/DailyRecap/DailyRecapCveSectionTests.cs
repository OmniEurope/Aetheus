// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Components.VulnerabilityWatch;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>The CVE section of the recap (PLAN-004, lot 3, point 15): its words, its numbers, and the attribution its sources require.</summary>
public sealed class DailyRecapCveSectionTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 5, 0, 0, DateTimeKind.Utc);

    private static readonly RecapCveWatch Busy = new(
        new Dictionary<AnalysisSeverity, int> { [AnalysisSeverity.Critical] = 1, [AnalysisSeverity.High] = 2, [AnalysisSeverity.Medium] = 5 },
        OpenExploited: 1, OpenUnrated: 4, OpenToVerify: 3, NewInDay: 2, ClosedInDay: 1);

    private static DailyRecapReport Report(RecapCveWatch watch, IReadOnlySet<DailyRecapSection>? sections = null, IReadOnlyList<VulnerabilityAttribution>? sources = null) =>
        DailyRecapRenderingTests.SampleReport(sections ?? new HashSet<DailyRecapSection> { DailyRecapSection.Cve }) with
        {
            CveWatch = watch,
            CveSources = sources ?? VulnerabilityAttributions.For(VulnerabilitySourceCatalog.DefaultEcosystems)
        };

    private static RecapDocument Compose(DailyRecapReport report, string culture = "fr-FR") =>
        DailyRecapComposer.Compose(report, new DailyRecapText(CultureInfo.GetCultureInfo(culture)), "https://app.example.org");

    [Fact]
    public void TheSection_SaysHowManyAreOpen_NewAndClosed_AndNamesTheExploitedAndTheUnverified()
    {
        var block = Assert.Single(Compose(Report(Busy)).Blocks);

        Assert.Equal("Vulnérabilités connues (CVE)", block.Title);
        Assert.Contains("12 ouvertes", block.Summary);
        Assert.Contains("1 exploitée", block.Summary);
        Assert.Contains("1 critique", block.Summary);
        Assert.Contains("2 élevées", block.Summary);
        Assert.Contains("5 moyennes", block.Summary);
        Assert.Contains("4 sans gravité publiée", block.Summary);
        Assert.Contains("3 à vérifier", block.Summary);
        Assert.Contains("2 nouvelles", block.Summary);
        Assert.Contains("1 fermée", block.Summary);
        Assert.Equal("https://app.example.org/analysis", block.Link!.Url);
    }

    [Fact]
    public void TheSection_IsWrittenInEnglishToo()
    {
        var block = Assert.Single(Compose(Report(Busy), "en").Blocks);

        Assert.Equal("Known vulnerabilities (CVE)", block.Title);
        Assert.Contains("12 open", block.Summary);
        Assert.Contains("1 exploited", block.Summary);
        Assert.Contains("4 without a published severity", block.Summary);
        Assert.Contains("3 to verify", block.Summary);
        Assert.Contains("2 new", block.Summary);
        Assert.Contains("1 closed", block.Summary);
    }

    [Fact]
    public void TheSection_CarriesTheAttributionOfItsSources_InTheHtmlAndInTheText()
    {
        var document = Compose(Report(Busy));
        var mail = DailyRecapRenderingTests.RenderMail(document, "fr-FR");

        foreach (var expected in new[] { "OSV.dev", "GitHub Advisory Database", "CC-BY-4.0", "RustSec Advisory Database", "CC0-1.0", "CISA", "Alpine Linux", "CC-BY-SA-4.0" })
        {
            Assert.Contains(expected, mail.Html);
            Assert.Contains(expected, mail.Text);
        }
    }

    [Fact]
    public void ASectionWithNothingToSay_IsLeftOut_AndTheQuietDayTakesItsPlace()
    {
        var block = Assert.Single(Compose(Report(RecapCveWatch.Empty)).Blocks);

        Assert.Equal("Rien à signaler", block.Title);
    }

    [Fact]
    public void ASectionSwitchedOff_IsNotComposed_WhateverTheWatchHolds()
    {
        var report = Report(Busy, new HashSet<DailyRecapSection> { DailyRecapSection.Pipelines });

        Assert.DoesNotContain(Compose(report).Blocks, block => block.Title.Contains("CVE", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSection_IsOnByDefault_AndSavedWithTheOthers()
    {
        Assert.Contains(DailyRecapSection.Cve, DailyRecapSettings.Default.Sections);
        var rows = DailyRecapSettings.Default.ToRows();
        Assert.Contains("Cve", rows[DailyRecapSettingKeys.Sections].Split(','));
        Assert.Contains(DailyRecapSection.Cve, DailyRecapSettings.Parse(rows).Sections);
    }

    private static async Task<DailyRecapReport> CollectAsync(
        IReadOnlySet<DailyRecapSection> sections, Dictionary<string, string?>? config = null)
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(new DateTimeOffset(Now)));
        db.Organizations.Add(new Organization { Id = 1, Name = "Org", Slug = "org", Description = "d" });
        db.Projects.Add(new Project { Id = 1, Name = "Shop", OrganizationId = 1 });
        db.VulnerabilityAdvisories.Add(new VulnerabilityAdvisory { Id = 1, ExternalId = "GHSA-1", Severity = AnalysisSeverity.High, SeverityPublished = true });
        db.VulnerabilityMatches.Add(new VulnerabilityMatch
        {
            Id = 1,
            OrganizationId = 1,
            ProjectId = 1,
            AdvisoryId = 1,
            Ecosystem = "NuGet",
            PackageName = "liba",
            Version = "1.0.0",
            Status = VulnerabilityMatchStatus.Affected,
            FirstMatchedAt = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var collector = new DailyRecapCollector(
            new DailyRecapRepository(db), Substitute.For<IAgentCompatibilityPolicy>(),
            new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());
        Assert.True(DailyRecapClock.TryResolveTimeZone("Europe/Paris", out var paris));
        return await collector.CollectAsync(new DateOnly(2026, 10, 31), paris, sections, Now, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheCollector_ReadsTheWatchOnlyWhenTheSectionIsAskedFor()
    {
        var without = await CollectAsync(new HashSet<DailyRecapSection> { DailyRecapSection.Mail });
        var with = await CollectAsync(new HashSet<DailyRecapSection> { DailyRecapSection.Cve });

        Assert.True(without.CveWatch.IsQuiet);
        Assert.Empty(without.CveSources);
        Assert.Equal(1, with.CveWatch.OpenBySeverity[AnalysisSeverity.High]);
        Assert.Equal(1, with.CveWatch.NewInDay);
        Assert.NotEmpty(with.CveSources);
    }

    [Fact]
    public async Task TheCollector_AttributesTheSourcesOfTheEcosystemsTheWatchRuns()
    {
        var report = await CollectAsync(new HashSet<DailyRecapSection> { DailyRecapSection.Cve }, new Dictionary<string, string?>
        {
            ["VulnerabilityWatch:Ecosystems:0"] = "NuGet",
            ["VulnerabilityWatch:Ecosystems:1"] = "Ubuntu"
        });

        Assert.Contains(report.CveSources, source => source.Name == "Ubuntu");
        Assert.DoesNotContain(report.CveSources, source => source.Name == "Debian");
    }
}


/// <summary>The attribution table: a licence obliges us to name its source, so no source may be loaded without one.</summary>
public sealed class VulnerabilityAttributionTests
{
    [Fact]
    public void EverySourceTheCatalogMayStore_HasAnAttribution_WithALicenceAndAnHttpsLink()
    {
        foreach (var source in VulnerabilitySourceCatalog.KnownSources)
        {
            var attribution = VulnerabilityAttributions.All.SingleOrDefault(candidate => candidate.Name == source.Name);
            Assert.True(attribution is not null, $"The source '{source.Name}' is loaded without any attribution.");
            Assert.Equal(source.Licence, attribution.Licence);
            Assert.StartsWith("https://", attribution.Url, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheAggregatorAndTheExploitedCatalogue_AreAttributedToo()
    {
        Assert.Contains(VulnerabilityAttributions.All, source => source.Name == "OSV.dev");
        var kev = Assert.Single(VulnerabilityAttributions.All, source => source.Name.StartsWith("CISA", StringComparison.Ordinal));
        Assert.Equal("CC0-1.0", kev.Licence);
    }

    [Theory]
    [InlineData("endoflife.date")]
    [InlineData(".NET release metadata (Microsoft)")]
    [InlineData("Node.js release index")]
    [InlineData("PHP releases and change log")]
    public void TheRuntimeSources_AreNamedWhateverTheEcosystems(string name) =>
        Assert.Contains(VulnerabilityAttributions.For(["npm"]), source => source.Name == name);

    [Fact]
    public void ASourceExcludedByItsLicence_IsNeverAttributed()
    {
        var names = VulnerabilityAttributions.All.Select(source => source.Name).ToList();

        Assert.DoesNotContain(names, name => name.Contains("Chainguard", StringComparison.OrdinalIgnoreCase) || name.Contains("Wolfi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ADistributionSource_IsAttributed_OnlyWhenItsEcosystemRuns()
    {
        var byDefault = VulnerabilityAttributions.For(VulnerabilitySourceCatalog.DefaultEcosystems).Select(source => source.Name).ToList();
        var languagesOnly = VulnerabilityAttributions.For(["NuGet", "npm"]).Select(source => source.Name).ToList();
        var withoutUbuntu = VulnerabilityAttributions.For(VulnerabilitySourceCatalog.DefaultEcosystems.Where(ecosystem => ecosystem != "Ubuntu")).Select(source => source.Name).ToList();

        Assert.Contains("Debian", byDefault);
        Assert.Contains("Alpine Linux", byDefault);
        // R-41: Ubuntu runs by default since 2026-11-06, so its attribution follows; left out, it is not named.
        Assert.Contains("Ubuntu", byDefault);
        Assert.DoesNotContain("Ubuntu", withoutUbuntu);
        Assert.DoesNotContain("Debian", languagesOnly);
        Assert.Contains("GitHub Advisory Database", languagesOnly);
        Assert.Contains("OSV.dev", languagesOnly);
        Assert.Equal(byDefault.Count, byDefault.Distinct(StringComparer.Ordinal).Count());
    }
}
