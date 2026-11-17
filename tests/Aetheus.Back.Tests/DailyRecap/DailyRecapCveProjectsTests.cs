// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>
/// Recette R-25: the open CVEs are not mailed again, the recap says, per project, how many are still to handle,
/// and a recipient is only told about the projects they may read.
/// </summary>
public sealed class DailyRecapCveProjectsTests
{
    private static readonly RecapCveWatch Quiet = RecapCveWatch.Empty with { OpenBySeverity = new Dictionary<AnalysisSeverity, int> { [AnalysisSeverity.High] = 3 } };

    private static RecapProjectCve Project(int id, string name, int high = 0, int critical = 0, int exploited = 0, int unrated = 0) =>
        new(id, name, new Dictionary<AnalysisSeverity, int>
        {
            [AnalysisSeverity.High] = high,
            [AnalysisSeverity.Critical] = critical
        }.Where(pair => pair.Value > 0).ToDictionary(pair => pair.Key, pair => pair.Value), exploited, unrated);

    private static DailyRecapReport Report(params RecapProjectCve[] projects) =>
        DailyRecapRenderingTests.SampleReport(new HashSet<DailyRecapSection> { DailyRecapSection.Cve }) with
        {
            CveWatch = Quiet,
            CveProjects = projects,
            CveSources = []
        };

    private static RecapBlock Block(DailyRecapReport report, string culture = "fr-FR") =>
        Assert.Single(DailyRecapComposer.Compose(report, new DailyRecapText(CultureInfo.GetCultureInfo(culture)), "https://app.example.org").Blocks);

    [Fact]
    public void EachProjectWithOpenCves_SaysHowManyAreToHandle_AndTheSplit()
    {
        var block = Block(Report(Project(1, "Alpha", high: 2, critical: 1, exploited: 1), Project(2, "Beta", high: 1)));

        Assert.Equal(["Alpha", "Beta"], block.Items.Select(item => item.Title));
        Assert.Equal("Sur ce projet il y a 3 CVE à traiter (1 exploitée, 1 critique, 2 élevées)", block.Items[0].Subtitle);
        Assert.Equal("Sur ce projet il y a 1 CVE à traiter (1 élevée)", block.Items[1].Subtitle);
    }

    [Fact]
    public void AProjectWithNoOpenCve_ShowsNothing()
    {
        var block = Block(Report(Project(1, "Alpha", high: 1), Project(2, "Calme")));

        Assert.Equal(["Alpha"], block.Items.Select(item => item.Title));
    }

    [Fact]
    public void WithNoProjectLine_TheSectionKeepsItsPlatformSummaryOnly()
    {
        var block = Block(Report());

        Assert.Empty(block.Items);
        Assert.Contains("3 ouvertes", block.Summary);
    }

    [Fact]
    public void TheProjectLine_IsWrittenInEnglishToo()
    {
        var block = Block(Report(Project(1, "Alpha", high: 2), Project(2, "Beta", critical: 1)), "en");

        Assert.Equal("On this project there are 2 CVEs to handle (2 high)", block.Items[0].Subtitle);
        Assert.StartsWith("On this project there is 1 CVE to handle", block.Items[1].Subtitle);
    }

    [Fact]
    public async Task ARecipient_IsTold_OnlyAboutTheProjectsTheyMayRead()
    {
        var (scope, authorization) = NewScope();
        authorization.GetAccessibleResourceIdsForUsernameAsync("reader", ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>()).Returns([2]);
        var report = Report(Project(1, "Alpha", high: 1), Project(2, "Beta", high: 1));

        var scoped = await scope.ForAsync(report, "reader@example.org", TestContext.Current.CancellationToken);

        Assert.Equal([2], scoped.CveProjects.Select(project => project.ProjectId));
    }

    [Theory]
    [InlineData("nobody@example.org")]
    [InlineData("left@example.org")]
    public async Task AnAddressWithNoActiveAccount_IsTold_AboutNoProject(string address)
    {
        var (scope, _) = NewScope();

        var scoped = await scope.ForAsync(Report(Project(1, "Alpha", high: 1)), address, TestContext.Current.CancellationToken);

        Assert.Empty(scoped.CveProjects);
    }

    [Fact]
    public async Task ASiteWideReader_IsTold_AboutEveryProject()
    {
        var (scope, authorization) = NewScope();
        authorization.GetAccessibleResourceIdsForUsernameAsync("admin", ResourceType.Project, Permission.Read, Arg.Any<CancellationToken>()).Returns((List<int>?)null);

        var scoped = await scope.ForAsync(Report(Project(1, "Alpha", high: 1), Project(2, "Beta", high: 1)), "admin@example.org", TestContext.Current.CancellationToken);

        Assert.Equal(2, scoped.CveProjects.Count);
    }

    private static (DailyRecapRecipientScope Scope, IResourceAuthorizationService Authorization) NewScope()
    {
        var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(new DateTimeOffset(2026, 11, 1, 5, 0, 0, TimeSpan.Zero)));
        db.Users.AddRange(
            new User { Username = "reader", Email = "Reader@Example.org", PasswordHash = "x", IsActive = true },
            new User { Username = "admin", Email = "admin@example.org", PasswordHash = "x", IsActive = true },
            new User { Username = "left", Email = "left@example.org", PasswordHash = "x", IsActive = false });
        db.SaveChanges();
        var authorization = Substitute.For<IResourceAuthorizationService>();
        return (new DailyRecapRecipientScope(new DailyRecapRepository(db), authorization), authorization);
    }
}
