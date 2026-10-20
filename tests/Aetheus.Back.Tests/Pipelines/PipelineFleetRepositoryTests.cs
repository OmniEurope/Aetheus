// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Environment = Aetheus.Back.Data.Entities.Environment;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// The fleet query composed against a store: the four ownership branches it concatenates, the
/// template left-join that decides freshness, the filters, and every sort key. The PostgreSQL
/// counterpart lives in <c>PipelineFleetPagingIntegrationTests</c>; this suite pins the shape of
/// the projection itself, which is where the ownership and freshness rules are encoded.
/// </summary>
public sealed class PipelineFleetRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PipelineFleetRepository _repository;

    public PipelineFleetRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repository = new PipelineFleetRepository(_db);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task SaveAsync() => await _db.SaveChangesAsync(Ct);

    private static Pipeline Pipeline(
        int id, string name, int? projectId = null, int? environmentId = null,
        int? projectServerId = null, string? templateName = null, int? templateVersion = null) =>
        new()
        {
            Id = id,
            Name = name,
            ProjectId = projectId,
            EnvironmentId = environmentId,
            ProjectServerId = projectServerId,
            TemplateReferenceName = templateName,
            TemplateReferenceVersion = templateVersion,
            YamlDefinition = $"name: {name}"
        };

    /// <summary>One project (id 10) in organization 7, plus a "ci" template at version 3.</summary>
    private async Task SeedProjectAndTemplateAsync()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.PipelineTemplates.Add(new PipelineTemplate
        {
            Id = 100,
            Name = "ci",
            Category = "CI",
            OrganizationId = 7,
            LatestVersion = 3
        });
        await SaveAsync();
    }

    private static PipelineFleetPaginationRequest Request(
        string? search = null, string? sortBy = null, bool descending = false,
        int page = 1, int pageSize = 50, int? templateId = null, int? projectId = null,
        PipelineFleetFreshness? freshness = null) =>
        new()
        {
            Page = page,
            PageSize = pageSize,
            Search = search,
            SortBy = sortBy,
            SortDescending = descending,
            TemplateId = templateId,
            ProjectId = projectId,
            Freshness = freshness
        };

    // ---------- ownership branches ----------

    [Fact]
    public async Task GetPageAsync_ResolvesTheOwnerOfEachOwnershipBranch()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 20, Name = "staging", ProjectId = 10 });
        _db.ProjectServers.Add(new ProjectServer { Id = 30, ProjectId = 10, DisplayName = "vps-1" });
        _db.Pipelines.AddRange(
            Pipeline(1, "by-project", projectId: 10),
            Pipeline(2, "by-environment", environmentId: 20),
            Pipeline(3, "by-project-server", projectServerId: 30),
            Pipeline(4, "orphan"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(sortBy: "PipelineName"), null, null, Ct);

        Assert.Equal(4, page.TotalCount);
        var byName = page.Items.ToDictionary(item => item.PipelineName);
        Assert.Equal(("aetheus", "Project", 7), Owner(byName["by-project"]));
        Assert.Equal(("staging", "Environment", 7), Owner(byName["by-environment"]));
        Assert.Equal(("vps-1", "ProjectServer", 7), Owner(byName["by-project-server"]));
        Assert.Equal(("Unowned", "ProjectServer", 0), Owner(byName["orphan"]));

        static (string, string, int) Owner(PipelineFleetItemDto item) =>
            (item.OwnerName, item.OwnerType, item.OrganizationId);
    }

    [Fact]
    public async Task GetPageAsync_ScopedToAProject_LeavesOutEveryOtherProject()
    {
        // Project filtering includes direct pipelines and those owned through environments or
        // project servers.
        await SeedProjectAndTemplateAsync();
        _db.Projects.Add(new Project { Id = 11, Name = "other", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 21, Name = "staging", ProjectId = 10 });
        _db.ProjectServers.Add(new ProjectServer { Id = 31, DisplayName = "vps-1", ProjectId = 10 });
        _db.Pipelines.AddRange(
            Pipeline(1, "mine-direct", projectId: 10),
            Pipeline(2, "mine-via-environment", environmentId: 21),
            Pipeline(3, "mine-via-server", projectServerId: 31),
            Pipeline(4, "someone-elses", projectId: 11));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(projectId: 10), null, null, Ct);

        Assert.Equal(3, page.TotalCount);
        Assert.DoesNotContain(page.Items, item => item.PipelineName == "someone-elses");
    }

    // ---------- freshness ----------

    [Fact]
    public async Task GetPageAsync_ClassifiesFreshnessAgainstTheTemplateLatestVersion()
    {
        await SeedProjectAndTemplateAsync();
        _db.Pipelines.AddRange(
            Pipeline(1, "pinned-latest", projectId: 10, templateName: "ci", templateVersion: 3),
            Pipeline(2, "pinned-old", projectId: 10, templateName: "ci", templateVersion: 2),
            Pipeline(3, "legacy-unpinned", projectId: 10, templateName: "ci"),
            Pipeline(4, "off-catalog", projectId: 10),
            Pipeline(5, "unknown-template", projectId: 10, templateName: "nope", templateVersion: 1));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(), null, null, Ct);
        var byName = page.Items.ToDictionary(item => item.PipelineName);

        Assert.Equal(PipelineFleetFreshness.Current, byName["pinned-latest"].Freshness);
        Assert.Equal(PipelineFleetFreshness.Outdated, byName["pinned-old"].Freshness);
        Assert.Equal(PipelineFleetFreshness.Current, byName["legacy-unpinned"].Freshness);
        Assert.Equal(PipelineFleetFreshness.OffCatalog, byName["off-catalog"].Freshness);
        Assert.Equal(PipelineFleetFreshness.Outdated, byName["unknown-template"].Freshness);
    }

    [Fact]
    public async Task GetPageAsync_FlagsTheLegacyReferenceAndCarriesTheTemplateVersions()
    {
        await SeedProjectAndTemplateAsync();
        _db.Pipelines.AddRange(
            Pipeline(1, "pinned", projectId: 10, templateName: "ci", templateVersion: 2),
            Pipeline(2, "legacy", projectId: 10, templateName: "ci"));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var byName = (await _repository.GetPageAsync(Request(), null, null, Ct))
            .Items.ToDictionary(item => item.PipelineName);

        Assert.False(byName["pinned"].UsesLegacyReference);
        Assert.Equal(2, byName["pinned"].PinnedVersion);
        Assert.Equal(3, byName["pinned"].LatestVersion);
        Assert.Equal(100, byName["pinned"].TemplateId);
        Assert.True(byName["legacy"].UsesLegacyReference);
        Assert.Null(byName["legacy"].PinnedVersion);
    }

    [Fact]
    public async Task GetPageAsync_MatchesTheTemplateReferenceIgnoringCase()
    {
        await SeedProjectAndTemplateAsync();
        _db.Pipelines.Add(Pipeline(1, "shouty", projectId: 10, templateName: "CI", templateVersion: 3));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var item = Assert.Single((await _repository.GetPageAsync(Request(), null, null, Ct)).Items);

        Assert.Equal(100, item.TemplateId);
        Assert.Equal(PipelineFleetFreshness.Current, item.Freshness);
    }

    [Fact]
    public async Task GetPageAsync_DoesNotMatchATemplateOfAnotherOrganization()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.PipelineTemplates.Add(new PipelineTemplate
        {
            Id = 100,
            Name = "ci",
            Category = "CI",
            OrganizationId = 8,
            LatestVersion = 3
        });
        _db.Pipelines.Add(Pipeline(1, "cross-org", projectId: 10, templateName: "ci", templateVersion: 3));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var item = Assert.Single((await _repository.GetPageAsync(Request(), null, null, Ct)).Items);

        Assert.Null(item.TemplateId);
        Assert.Null(item.LatestVersion);
        Assert.Equal(PipelineFleetFreshness.Outdated, item.Freshness);
    }

    // ---------- filters ----------

    [Fact]
    public async Task GetPageAsync_RestrictsToTheAccessiblePipelineIds()
    {
        await SeedProjectAndTemplateAsync();
        _db.Pipelines.AddRange(
            Pipeline(1, "visible", projectId: 10),
            Pipeline(2, "hidden", projectId: 10));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(), null, [1], Ct);

        Assert.Equal(["visible"], page.Items.Select(item => item.PipelineName));
    }

    [Fact]
    public async Task GetPageAsync_RestrictsToTheAccessibleOrganizations()
    {
        _db.Projects.AddRange(
            new Project { Id = 10, Name = "ours", OrganizationId = 7 },
            new Project { Id = 11, Name = "theirs", OrganizationId = 8 });
        _db.Pipelines.AddRange(
            Pipeline(1, "ours", projectId: 10),
            Pipeline(2, "theirs", projectId: 11));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(), [7], null, Ct);

        Assert.Equal(["ours"], page.Items.Select(item => item.PipelineName));
    }

    [Fact]
    public async Task GetPageAsync_SearchesTheNameTheTemplateAndEveryOwnerLabel()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "needle-project", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 20, Name = "needle-env", ProjectId = 10 });
        _db.ProjectServers.Add(new ProjectServer { Id = 30, ProjectId = 10, DisplayName = "needle-server" });
        _db.Projects.Add(new Project { Id = 11, Name = "quiet", OrganizationId = 7 });
        _db.Pipelines.AddRange(
            Pipeline(1, "needle-name", projectId: 11),
            Pipeline(2, "by-template", projectId: 11, templateName: "needle-template"),
            Pipeline(3, "by-project", projectId: 10),
            Pipeline(4, "by-environment", environmentId: 20),
            Pipeline(5, "by-project-server", projectServerId: 30),
            Pipeline(6, "unrelated", projectId: 11));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(search: "  needle  "), null, null, Ct);

        Assert.Equal(5, page.TotalCount);
        Assert.DoesNotContain("unrelated", page.Items.Select(item => item.PipelineName));
    }

    [Fact]
    public async Task GetPageAsync_NarrowsToTheOwningProjectAcrossOwnershipBranches()
    {
        _db.Projects.AddRange(
            new Project { Id = 10, Name = "wanted", OrganizationId = 7 },
            new Project { Id = 11, Name = "other", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 20, Name = "staging", ProjectId = 10 });
        _db.ProjectServers.Add(new ProjectServer { Id = 30, ProjectId = 11, DisplayName = "vps-1" });
        _db.Pipelines.AddRange(
            Pipeline(1, "direct", projectId: 10),
            Pipeline(2, "via-environment", environmentId: 20),
            Pipeline(3, "via-project-server", projectServerId: 30),
            Pipeline(4, "elsewhere", projectId: 11));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(projectId: 10), null, null, Ct);

        Assert.Equal(
            ["direct", "via-environment"],
            page.Items.Select(item => item.PipelineName).Order());
    }

    [Fact]
    public async Task GetPageAsync_FiltersOnTheJoinedTemplateIdAndOnFreshness()
    {
        await SeedProjectAndTemplateAsync();
        _db.PipelineTemplates.Add(new PipelineTemplate
        {
            Id = 101,
            Name = "cd",
            Category = "CD",
            OrganizationId = 7,
            LatestVersion = 1
        });
        _db.Pipelines.AddRange(
            Pipeline(1, "ci-current", projectId: 10, templateName: "ci", templateVersion: 3),
            Pipeline(2, "ci-outdated", projectId: 10, templateName: "ci", templateVersion: 1),
            Pipeline(3, "cd-current", projectId: 10, templateName: "cd", templateVersion: 1));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var byTemplate = await _repository.GetPageAsync(Request(templateId: 100), null, null, Ct);
        var byFreshness = await _repository.GetPageAsync(
            Request(freshness: PipelineFleetFreshness.Outdated), null, null, Ct);

        Assert.Equal(2, byTemplate.TotalCount);
        Assert.Equal(["ci-outdated"], byFreshness.Items.Select(item => item.PipelineName));
    }

    // ---------- sorting and paging ----------

    [Theory]
    [InlineData("PipelineName", false, new[] { "alpha", "beta", "gamma" })]
    [InlineData("PipelineName", true, new[] { "gamma", "beta", "alpha" })]
    [InlineData("OwnerName", false, new[] { "beta", "gamma", "alpha" })]
    [InlineData("OwnerName", true, new[] { "alpha", "gamma", "beta" })]
    public async Task GetPageAsync_SortsOnTheRequestedTextKey(
        string sortBy, bool descending, string[] expected)
    {
        _db.Projects.AddRange(
            new Project { Id = 1, Name = "alpha", OrganizationId = 7 },
            new Project { Id = 2, Name = "beta", OrganizationId = 7 },
            new Project { Id = 3, Name = "gamma", OrganizationId = 7 });
        // Pipeline names, owner names and ids are deliberately uncorrelated: sorting on the
        // owner yields beta/gamma/alpha, on the id gamma/alpha/beta, on the name alpha/beta/gamma,
        // so each expected order is produced by exactly one sort key.
        _db.Pipelines.AddRange(
            Pipeline(2, "alpha", projectId: 3),
            Pipeline(3, "beta", projectId: 1),
            Pipeline(1, "gamma", projectId: 2));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(sortBy: sortBy, descending: descending), null, null, Ct);

        Assert.Equal(expected, page.Items.Select(item => item.PipelineName));
    }

    [Theory]
    [InlineData("TemplateName", false, new[] { "alpha-tpl", "zulu-tpl", "no-tpl" })]
    [InlineData("TemplateName", true, new[] { "zulu-tpl", "alpha-tpl", "no-tpl" })]
    [InlineData("PinnedVersion", false, new[] { "no-tpl", "alpha-tpl", "zulu-tpl" })]
    [InlineData("PinnedVersion", true, new[] { "zulu-tpl", "alpha-tpl", "no-tpl" })]
    [InlineData("LatestVersion", false, new[] { "no-tpl", "zulu-tpl", "alpha-tpl" })]
    [InlineData("LatestVersion", true, new[] { "alpha-tpl", "zulu-tpl", "no-tpl" })]
    [InlineData("Freshness", false, new[] { "alpha-tpl", "zulu-tpl", "no-tpl" })]
    [InlineData("Freshness", true, new[] { "no-tpl", "zulu-tpl", "alpha-tpl" })]
    public async Task GetPageAsync_SortsOnTheRequestedTemplateKey(
        string sortBy, bool descending, string[] expected)
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.PipelineTemplates.AddRange(
            new PipelineTemplate { Id = 100, Name = "alpha", Category = "CI", OrganizationId = 7, LatestVersion = 2 },
            new PipelineTemplate { Id = 101, Name = "zulu", Category = "CI", OrganizationId = 7, LatestVersion = 1 });
        _db.Pipelines.AddRange(
            Pipeline(1, "alpha-tpl", projectId: 10, templateName: "alpha", templateVersion: 2),
            Pipeline(2, "zulu-tpl", projectId: 10, templateName: "zulu", templateVersion: 3),
            Pipeline(3, "no-tpl", projectId: 10));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(sortBy: sortBy, descending: descending), null, null, Ct);

        Assert.Equal(expected, page.Items.Select(item => item.PipelineName));
    }

    [Fact]
    public async Task GetPageAsync_DefaultSortGroupsCataloguedPipelinesFirst()
    {
        await SeedProjectAndTemplateAsync();
        _db.Pipelines.AddRange(
            Pipeline(1, "zzz-off-catalog", projectId: 10),
            Pipeline(2, "aaa-off-catalog", projectId: 10),
            Pipeline(3, "zzz-ci", projectId: 10, templateName: "ci", templateVersion: 3),
            Pipeline(4, "aaa-ci", projectId: 10, templateName: "ci", templateVersion: 3));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(Request(sortBy: "unknown-key"), null, null, Ct);

        Assert.Equal(
            ["aaa-ci", "zzz-ci", "aaa-off-catalog", "zzz-off-catalog"],
            page.Items.Select(item => item.PipelineName));
    }

    [Fact]
    public async Task GetPageAsync_PagesAfterSortingAndReportsTheUnpagedTotal()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Pipelines.AddRange(Enumerable.Range(1, 7)
            .Select(index => Pipeline(index, $"pipeline-{index:D2}", projectId: 10)));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var page = await _repository.GetPageAsync(
            Request(sortBy: "PipelineName", page: 3, pageSize: 2), null, null, Ct);

        Assert.Equal(7, page.TotalCount);
        Assert.Equal(3, page.Page);
        Assert.Equal(2, page.PageSize);
        Assert.Equal(["pipeline-05", "pipeline-06"], page.Items.Select(item => item.PipelineName));
    }

    // ---------- single-row projection ----------

    [Fact]
    public async Task GetAsync_ProjectsTheOwnerChainOfAProjectOwnedPipeline()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Pipelines.Add(new Pipeline
        {
            Id = 1,
            Name = "build",
            Description = "the build",
            YamlDefinition = "name: build",
            SourceBranch = "main",
            ProjectId = 10
        });
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var row = await _repository.GetAsync(1, Ct);

        Assert.Equal("build", row!.PipelineName);
        Assert.Equal("the build", row.Description);
        Assert.Equal("name: build", row.YamlDefinition);
        Assert.Equal("main", row.SourceBranch);
        Assert.Equal(10, row.ProjectId);
        Assert.Equal(10, row.OwnerProjectId);
        Assert.Equal("aetheus", row.OwnerName);
        Assert.Equal("Project", row.OwnerType);
        Assert.Equal(7, row.OrganizationId);
    }

    [Fact]
    public async Task GetAsync_ResolvesTheOwningProjectThroughTheEnvironment()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.Environments.Add(new Environment { Id = 20, Name = "staging", ProjectId = 10 });
        _db.Pipelines.Add(Pipeline(1, "deploy", environmentId: 20));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var row = await _repository.GetAsync(1, Ct);

        Assert.Null(row!.ProjectId);
        Assert.Equal(10, row.OwnerProjectId);
        Assert.Equal(20, row.EnvironmentId);
        Assert.Equal("staging", row.OwnerName);
        Assert.Equal("Environment", row.OwnerType);
        Assert.Equal(7, row.OrganizationId);
    }

    [Fact]
    public async Task GetAsync_ResolvesTheOwningProjectThroughTheProjectServer()
    {
        _db.Projects.Add(new Project { Id = 10, Name = "aetheus", OrganizationId = 7 });
        _db.ProjectServers.Add(new ProjectServer { Id = 30, ProjectId = 10, DisplayName = "vps-1" });
        _db.Pipelines.Add(Pipeline(1, "maintain", projectServerId: 30));
        await SaveAsync();
        _db.ChangeTracker.Clear();

        var row = await _repository.GetAsync(1, Ct);

        Assert.Equal(10, row!.OwnerProjectId);
        Assert.Equal(30, row.ProjectServerId);
        Assert.Equal("vps-1", row.OwnerName);
        Assert.Equal("ProjectServer", row.OwnerType);
        Assert.Equal(7, row.OrganizationId);
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForAnUnknownPipeline()
    {
        Assert.Null(await _repository.GetAsync(404, Ct));
    }

    public void Dispose() => _db.Dispose();
}
