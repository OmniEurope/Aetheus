// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.PackageFeeds;
using Aetheus.Back.Components.PackageRegistry;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.VariableLibraries;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Back.Tests;

/// <summary> The column maps of the package feeds, package registry, template versions, coverage
/// assemblies, project artifacts, variable libraries (entries and versions) and vaults grids narrow
/// the rows in the query, before the count, and refuse a column they do not expose. </summary>
public sealed class PackagePipelineVariableColumnFilterTests : IDisposable
{
    private static readonly DateTime Origin = new(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(Origin));
    private readonly AppDbContext _db;

    public PackagePipelineVariableColumnFilterTests() =>
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options, _clock);

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GridFilter In(string field, params string[] values) =>
        new() { Field = field, Operator = GridFilterOperator.In, Value = string.Join(GridFilter.ListSeparator, values) };

    private static GridFilter Between(string field, DateTime from, DateTime to) => new()
    {
        Field = field,
        Operator = GridFilterOperator.GreaterThanOrEqual,
        Value = from.ToString("O"),
        SecondOperator = GridFilterOperator.LessThan,
        SecondValue = to.ToString("O")
    };

    [Fact]
    public async Task PackageFeeds_TheTypeListAndThePackageCountNarrowBeforeTheCount()
    {
        var nuget = new PackageFeed { Name = "corp-nuget", FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://nuget" };
        _db.PackageFeeds.AddRange(
            nuget,
            new PackageFeed { Name = "empty-nuget", FeedType = PackageFeedType.NuGet, UpstreamUrl = "https://nuget2" },
            new PackageFeed { Name = "npm", FeedType = PackageFeedType.Npm, UpstreamUrl = "https://npm" },
            new PackageFeed { Name = "maven", FeedType = PackageFeedType.Maven, UpstreamUrl = "https://maven" });
        await _db.SaveChangesAsync(Ct);
        _db.PackageEntries.Add(new PackageEntry { PackageFeedId = nuget.Id, Name = "xunit" });
        await _db.SaveChangesAsync(Ct);

        var (items, total) = await new PackageFeedRepository(_db).GetPagedFeedsAsync(
            null, null, 1, 10, null, false, Ct,
            [In("FeedType", "NuGet", "Npm"), new GridFilter { Field = "PackageCount", Operator = GridFilterOperator.GreaterThan, Value = "0" }]);

        Assert.Equal(1, total);
        Assert.Equal("corp-nuget", Assert.Single(items).Name);
    }

    [Fact]
    public async Task PackageRegistry_TheKindListAndTheLatestVersionNarrowBeforeTheCount()
    {
        _db.RegistryPackages.AddRange(
            new RegistryPackage
            {
                Id = 1,
                Kind = PackageRegistryKind.NuGet,
                Name = "Aetheus.Core",
                NormalizedName = "aetheus.core",
                Versions = [new RegistryPackageVersion { Id = 1, Version = "2.0.0", NormalizedVersion = "2.0.0", IsListed = true }]
            },
            new RegistryPackage
            {
                Id = 2,
                Kind = PackageRegistryKind.NuGet,
                Name = "Aetheus.Old",
                NormalizedName = "aetheus.old",
                Versions = [new RegistryPackageVersion { Id = 2, Version = "1.0.0", NormalizedVersion = "1.0.0", IsListed = true }]
            },
            new RegistryPackage { Id = 3, Kind = PackageRegistryKind.Npm, Name = "aetheus-ui", NormalizedName = "aetheus-ui" });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();

        var (items, total) = await new PackageRegistryRepository(_db).GetPagedAsync(
            null, null, 1, 10, Ct, columnFilters:
            [In("Kind", "NuGet"), new GridFilter { Field = "LatestVersion", Operator = GridFilterOperator.StartsWith, Value = "2." }]);

        Assert.Equal(1, total);
        Assert.Equal("Aetheus.Core", Assert.Single(items).Name);
    }

    [Fact]
    public async Task PackageRegistry_TheSizeColumnIsNotAFilter()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => new PackageRegistryRepository(_db).GetPagedAsync(
            null, null, 1, 10, Ct, columnFilters:
            [new GridFilter { Field = "TotalSizeBytes", Operator = GridFilterOperator.GreaterThan, Value = "1" }]));
    }

    [Fact]
    public async Task TemplateVersions_TheCreatedRangeNarrowsBeforeTheCount()
    {
        var template = new PipelineTemplate { Name = "Build", Category = "CI", LatestVersion = 3 };
        _db.PipelineTemplates.Add(template);
        await _db.SaveChangesAsync(Ct);
        foreach (var version in new[] { 1, 2, 3 })
        {
            _clock.SetUtcNow(new DateTimeOffset(Origin.AddDays(version)));
            _db.PipelineTemplateVersions.Add(new PipelineTemplateVersion
            {
                TemplateId = template.Id,
                Version = version,
                YamlContent = "name: t",
                CreatedByUsername = "alice"
            });
            await _db.SaveChangesAsync(Ct);
        }

        var (items, total) = await new PipelineTemplateRepository(_db).GetTemplateVersionsPagedAsync(
            template.Id, 1, 10, null, true, Ct,
            [Between("CreatedAt", Origin.AddDays(2).Date, Origin.AddDays(3).Date), In("CreatedByUsername", "ALICE")]);

        Assert.Equal(1, total);
        Assert.Equal(2, Assert.Single(items).Version);
    }

    [Fact]
    public void CoverageAssemblies_TheNameFilterNarrowsTheAssemblies()
    {
        var assemblies = new List<CoverageAssemblyDto>
        {
            new() { Name = "Aetheus.Back" },
            new() { Name = "Aetheus.Front" },
            new() { Name = "Tools" }
        };

        var kept = PipelineArtifactService.CoverageAssemblyColumns.ApplyFilters(
            assemblies.AsQueryable(), [new GridFilter { Field = "Name", Operator = GridFilterOperator.Contains, Value = "AETHEUS" }]);

        Assert.Equal(["Aetheus.Back", "Aetheus.Front"], kept.Select(assembly => assembly.Name));
        Assert.Throws<BadRequestException>(() => PipelineArtifactService.CoverageAssemblyColumns.ApplyFilters(
            assemblies.AsQueryable(), [new GridFilter { Field = "LineRate", Operator = GridFilterOperator.GreaterThan, Value = "0.5" }]).ToList());
    }

    [Fact]
    public async Task ProjectArtifacts_TheEnvironmentAndPipelineListsNarrow_AndTheirValuesComeFromTheProject()
    {
        _db.Pipelines.AddRange(new Pipeline { Id = 1, Name = "CI" }, new Pipeline { Id = 2, Name = "CD" });
        _db.PipelineRuns.Add(new PipelineRun { Id = 1, PipelineId = 1, Status = PipelineStatus.Success });
        _db.PipelineArtifacts.AddRange(
            Artifact(1, 1, "a.zip", "prod"),
            Artifact(2, 1, "b.zip", "staging"),
            Artifact(2, 1, "c.zip", "prod"),
            Artifact(1, 2, "other-project.zip", "qa"));
        await _db.SaveChangesAsync(Ct);
        var repository = new ArtifactRepository(_db);

        var (items, total) = await repository.GetByProjectPagedAsync(
            1, null, null, 1, 10, Ct, [In("EnvironmentName", "PROD"), In("PipelineName", "CD")]);
        var values = await repository.GetProjectFilterValuesAsync(1, Ct);

        Assert.Equal(1, total);
        Assert.Equal("c.zip", Assert.Single(items).Name);
        Assert.Equal(["CD", "CI"], values.PipelineNames);
        Assert.Equal(["prod", "staging"], values.EnvironmentNames);
    }

    private static PipelineArtifact Artifact(int pipelineId, int projectId, string name, string environment) => new()
    {
        PipelineId = pipelineId,
        ProjectId = projectId,
        PipelineRunId = 1,
        Name = name,
        FilePath = $"/artifacts/{name}",
        EnvironmentName = environment,
        RetentionExpiresAt = new DateTime(2026, 12, 31, 0, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public async Task VariableLibraries_TheProjectListAndTheCreatedRangeNarrow_AndTheValuesFollowTheCallerScope()
    {
        var aetheus = new Project { Name = "Aetheus" };
        var other = new Project { Name = "Other" };
        _db.Projects.AddRange(aetheus, other);
        await _db.SaveChangesAsync(Ct);
        var early = new VariableLibrary { Name = "early", Description = "d", ProjectId = aetheus.Id };
        _db.VariableLibraries.Add(early);
        await _db.SaveChangesAsync(Ct);
        _clock.SetUtcNow(new DateTimeOffset(Origin.AddDays(5)));
        var late = new VariableLibrary { Name = "late", Description = "d", ProjectId = aetheus.Id };
        var hidden = new VariableLibrary { Name = "hidden", Description = "d", ProjectId = other.Id };
        _db.VariableLibraries.AddRange(late, hidden, new VariableLibrary { Name = "global", Description = "d" });
        await _db.SaveChangesAsync(Ct);
        var repository = new VariableLibraryRepository(_db);

        var (items, total) = await repository.GetLibrariesPagedAsync(
            null, null, null, null, 1, 10, ct: Ct,
            columnFilters: [In("ProjectName", "aetheus"), Between("CreatedAt", Origin.AddDays(4).Date, Origin.AddDays(6).Date)]);
        var values = await repository.GetFilterValuesAsync([early.Id, late.Id], Ct);

        Assert.Equal(1, total);
        Assert.Equal("late", Assert.Single(items).Name);
        Assert.Equal(["Aetheus"], values.ProjectNames);
    }

    [Fact]
    public async Task VariableEntries_TheKeyFilterNarrows_AndTheVersionsFilterByChangeType()
    {
        var library = new VariableLibrary { Name = "lib", Description = "d" };
        _db.VariableLibraries.Add(library);
        await _db.SaveChangesAsync(Ct);
        var domain = new VariableLibraryEntry { VariableLibraryId = library.Id, Key = "SITE_DOMAIN", Value = "a" };
        _db.VariableLibraryEntries.AddRange(domain, new VariableLibraryEntry { VariableLibraryId = library.Id, Key = "PORT", Value = "80" });
        await _db.SaveChangesAsync(Ct);
        _db.VariableLibraryEntryVersions.AddRange(
            new VariableLibraryEntryVersion { VariableLibraryEntryId = domain.Id, Key = "SITE_DOMAIN", Value = "a", Version = 1, ChangeType = ChangeType.Created, ChangedAt = Origin },
            new VariableLibraryEntryVersion { VariableLibraryEntryId = domain.Id, Key = "SITE_DOMAIN", Value = "b", Version = 2, ChangeType = ChangeType.Updated, ChangedAt = Origin.AddDays(1) },
            new VariableLibraryEntryVersion { VariableLibraryEntryId = domain.Id, Key = "SITE_DOMAIN", Value = "a", Version = 3, ChangeType = ChangeType.Updated, ChangedAt = Origin.AddDays(9) });
        await _db.SaveChangesAsync(Ct);
        var repository = new VariableLibraryRepository(_db);

        var (entries, entryTotal) = await repository.GetEntriesPagedAsync(
            library.Id, null, 1, 10, null, false, Ct,
            [new GridFilter { Field = "Key", Operator = GridFilterOperator.Contains, Value = "domain" }]);
        var (versions, versionTotal) = await repository.GetEntryVersionsPagedAsync(
            domain.Id, null, 1, 10, null, false, Ct,
            [In("ChangeType", "Updated"), Between("ChangedAt", Origin.Date, Origin.AddDays(2).Date)]);

        Assert.Equal(1, entryTotal);
        Assert.Equal("SITE_DOMAIN", Assert.Single(entries).Key);
        Assert.Equal(1, versionTotal);
        Assert.Equal(2, Assert.Single(versions).Version);
    }

    [Fact]
    public async Task Vaults_TheProjectListNarrows_AndTheValuesFollowTheCallerScope()
    {
        var aetheus = new Project { Name = "Aetheus" };
        var other = new Project { Name = "Other" };
        _db.Projects.AddRange(aetheus, other);
        await _db.SaveChangesAsync(Ct);
        var mine = new Vault { Name = "mine", Description = "d", ProjectId = aetheus.Id };
        var theirs = new Vault { Name = "theirs", Description = "d", ProjectId = other.Id };
        _db.Vaults.AddRange(mine, theirs);
        await _db.SaveChangesAsync(Ct);
        var repository = new VaultRepository(_db, _clock);

        var (items, total) = await repository.GetVaultsPagedAsync(
            null, null, null, null, 1, 10, ct: Ct, columnFilters: [In("ProjectName", "Other")]);
        var values = await repository.GetFilterValuesAsync([mine.Id], Ct);

        Assert.Equal(1, total);
        Assert.Equal("theirs", Assert.Single(items).Name);
        Assert.Equal(["Aetheus"], values.ProjectNames);
    }
}
