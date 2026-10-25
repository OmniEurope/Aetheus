// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests;

public class SeededTemplateScriptPathReconciliationTests
{
    private const string OldLine = "sh $(WORKSPACE)/deploy/scripts/normalize-coverage-report.sh $(WORKSPACE)/coverage";
    private const string NewLine = "sh $(WORKSPACE)/scripts/normalize-coverage-report.sh $(WORKSPACE)/coverage";
    private const string SeededName = "Build=VPS, Deploy=VPS";

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"SeededTemplatePathDb_{Guid.NewGuid()}")
            .Options);

    private static string Yaml(string scriptLine) => $"name: ci\nsteps:\n  - shell: |\n      echo before\n      {scriptLine}\n      echo after\n";

    private static PipelineTemplate Template(string name, string yaml, string author, string category = "CI/CD") => new()
    {
        Name = name,
        Description = "example",
        Category = category,
        OrganizationId = 1,
        LatestVersion = 1,
        Versions =
        [
            new PipelineTemplateVersion { Version = 1, YamlContent = yaml, ChangelogEntry = "Initial version", CreatedByUsername = author }
        ]
    };

    private static Task RunAsync(AppDbContext db) =>
        SeededTemplateScriptPathReconciliation.RunAsync(
            db, TimeProvider.System, NullLogger.Instance, TestContext.Current.CancellationToken);

    private static Task<PipelineTemplate> LoadAsync(AppDbContext db, string name) =>
        db.PipelineTemplates.Include(t => t.Versions).AsNoTracking()
            .SingleAsync(t => t.Name == name, TestContext.Current.CancellationToken);

    [Fact]
    public async Task RunAsync_PublishesANewVersionWithTheMovedPath_ForALegacySeededTemplate()
    {
        await using var db = NewDb();
        db.PipelineTemplates.Add(Template(SeededName, Yaml(OldLine), "system"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync(db);

        var template = await LoadAsync(db, SeededName);
        Assert.Equal(2, template.LatestVersion);
        Assert.Equal(2, template.Versions.Count);
        var v1 = template.Versions.Single(v => v.Version == 1);
        var v2 = template.Versions.Single(v => v.Version == 2);
        Assert.Equal(Yaml(OldLine), v1.YamlContent);
        Assert.Equal(Yaml(NewLine), v2.YamlContent);
        Assert.Equal("system", v2.CreatedByUsername);
        Assert.Contains("deploy/scripts", v2.ChangelogEntry, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_IsIdempotent()
    {
        await using var db = NewDb();
        db.PipelineTemplates.Add(Template(SeededName, Yaml(OldLine), "system"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync(db);
        await RunAsync(db);

        var template = await LoadAsync(db, SeededName);
        Assert.Equal(2, template.LatestVersion);
        Assert.Equal(2, template.Versions.Count);
    }

    [Fact]
    public async Task RunAsync_LeavesATemplateEditedByAPersonUntouched()
    {
        await using var db = NewDb();
        var edited = Template(SeededName, Yaml(OldLine), "alice");
        db.PipelineTemplates.Add(edited);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync(db);

        var template = await LoadAsync(db, SeededName);
        Assert.Equal(1, template.LatestVersion);
        Assert.Single(template.Versions);
        Assert.Equal(Yaml(OldLine), template.Versions.Single().YamlContent);
    }

    [Fact]
    public async Task RunAsync_LeavesATemplateWhoseLatestVersionWasPublishedByAPersonUntouched()
    {
        await using var db = NewDb();
        var template = Template(SeededName, Yaml(OldLine), "system");
        template.LatestVersion = 2;
        template.Versions.Add(new PipelineTemplateVersion { Version = 2, YamlContent = Yaml(OldLine), ChangelogEntry = "mine", CreatedByUsername = "alice" });
        db.PipelineTemplates.Add(template);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync(db);

        var loaded = await LoadAsync(db, SeededName);
        Assert.Equal(2, loaded.LatestVersion);
        Assert.Equal(2, loaded.Versions.Count);
    }

    [Fact]
    public async Task RunAsync_LeavesATemplateWithAnotherNameOrCategoryUntouched()
    {
        await using var db = NewDb();
        db.PipelineTemplates.AddRange(
            Template("My own template", Yaml(OldLine), "system"),
            Template("Build=VPS, Deploy=Windows", Yaml(OldLine), "system", category: "Custom"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync(db);

        Assert.All(await db.PipelineTemplates.Include(t => t.Versions).ToListAsync(TestContext.Current.CancellationToken), template =>
        {
            Assert.Equal(1, template.LatestVersion);
            Assert.Single(template.Versions);
        });
    }

    [Fact]
    public async Task RunAsync_ReplacesOnlyTheExactOldPath()
    {
        await using var db = NewDb();
        const string other = "sh $(WORKSPACE)/deploy/scripts/other-script.sh";
        db.PipelineTemplates.Add(Template(SeededName, Yaml(OldLine) + other + "\n", "system"));
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await RunAsync(db);

        var latest = (await LoadAsync(db, SeededName)).Versions.Single(v => v.Version == 2);
        Assert.Contains(NewLine, latest.YamlContent, StringComparison.Ordinal);
        Assert.Contains(other, latest.YamlContent, StringComparison.Ordinal);
        Assert.DoesNotContain("deploy/scripts/normalize-coverage-report.sh", latest.YamlContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_DoesNothingOnAFreshlySeededDatabase()
    {
        await using var db = NewDb();
        await DbInitializer.SeedAsync(db);

        await RunAsync(db);

        Assert.All(await db.PipelineTemplates.Include(t => t.Versions).ToListAsync(TestContext.Current.CancellationToken), template =>
        {
            Assert.Equal(1, template.LatestVersion);
            Assert.Single(template.Versions);
            Assert.DoesNotContain("deploy/scripts/", template.Versions.Single().YamlContent, StringComparison.Ordinal);
        });
    }
}
