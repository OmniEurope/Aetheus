// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AiTasks;
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Git;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Plugins;
using Aetheus.Back.Components.ServiceConnections;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

/// <summary> The column maps of the AI, analysis portfolio, backups, git, service connections,
/// plugins, pipeline fleet and notification administration grids. Each list is filtered through its
/// repository (in-memory database) so the proof covers the wiring, filter applied before the count,
/// not only the map; the git lists read from git and the fleet row are filtered in memory.
/// </summary>
public sealed class GridColumnMapsTests : IDisposable
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString())
        .Options);

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static GridFilter In(string field, params string[] values) =>
        new() { Field = field, Operator = GridFilterOperator.In, Value = string.Join(GridFilter.ListSeparator, values) };

    private static GridFilter Is(string field, string value) =>
        new() { Field = field, Operator = GridFilterOperator.Equals, Value = value };

    private static GridFilter Since(string field, DateTime from) =>
        new() { Field = field, Operator = GridFilterOperator.GreaterThanOrEqual, Value = from.ToString("O") };

    [Fact]
    public async Task AiProfilesAndTasks_AreFilteredBeforeTheCount()
    {
        var claude = new AiRunnerProfile { Name = "reviewer", Model = "claude-sonnet-5-5", SendsDataExternally = true };
        var local = new AiRunnerProfile { Name = "local", Model = "llama-4" };
        _db.AiRunnerProfiles.AddRange(claude, local);
        _db.AiTaskDefinitions.AddRange(
            new AiTaskDefinition { Name = "nightly", Profile = claude, Enabled = true },
            new AiTaskDefinition { Name = "weekly", Profile = local, Enabled = false },
            new AiTaskDefinition { Name = "audit", Profile = local, Enabled = true });
        await _db.SaveChangesAsync(Ct);
        var repo = new AiTaskRepository(_db);

        var (profiles, profileTotal) = await repo.GetProfilesPageAsync(null, 1, 10, Ct,
            [In("Model", "CLAUDE-SONNET-5-5"), Is("SendsDataExternally", "True")]);
        var (tasks, taskTotal) = await repo.GetDefinitionsPageAsync(null, 1, 10, null, null, null, null, Ct,
            [In("ProfileName", "local"), Is("Enabled", "True")]);

        Assert.Equal(1, profileTotal);
        Assert.Equal(["reviewer"], profiles.Select(profile => profile.Name));
        Assert.Equal(1, taskTotal);
        Assert.Equal(["audit"], tasks.Select(task => task.Name));
    }

    [Fact]
    public async Task AnalysisPortfolio_ColumnFiltersReplaceTheFilterBar()
    {
        var acme = new Organization { Name = "Acme" };
        var other = new Organization { Name = "Other" };
        var website = new Project { Name = "Website", Organization = acme };
        var api = new Project { Name = "Api", Organization = other };
        _db.AnalysisReports.AddRange(
            new AnalysisReport { Organization = acme, Project = website, ScannerName = "semgrep", Category = AnalysisCategory.Sast, CompletedAt = new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc) },
            new AnalysisReport { Organization = acme, Project = website, ScannerName = "gitleaks", Category = AnalysisCategory.Secrets, CompletedAt = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc) },
            new AnalysisReport { Organization = other, Project = api, ScannerName = "semgrep", Category = AnalysisCategory.Sast, CompletedAt = new DateTime(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc) });
        await _db.SaveChangesAsync(Ct);
        var repo = new AnalysisRepository(_db);

        var (items, total) = await repo.GetPortfolioAsync(null, new AnalysisPortfolioPaginationRequest
        {
            Filters = [In("OrganizationName", "Acme"), In("Category", "Sast", "Secrets"), Since("CompletedAt", new DateTime(2026, 9, 11, 0, 0, 0, DateTimeKind.Utc))]
        }, Ct);
        var values = await repo.GetPortfolioFilterValuesAsync(null, Ct);

        Assert.Equal(1, total);
        Assert.Equal(["gitleaks"], items.Select(item => item.ScannerName));
        Assert.Equal(["Acme", "Other"], values.Organizations);
        Assert.Equal(["Api", "Website"], values.Projects);
        Assert.Equal(["gitleaks", "semgrep"], values.Scanners);
    }

    [Fact]
    public async Task BackupPoliciesAndRuns_AreFilteredBeforeTheCount()
    {
        var project = new Project { Name = "Website" };
        var server = new Server { Name = "db-1", Hostname = "db-1" };
        var postgres = new BackupPolicy { Name = "pg", Project = project, Server = server, DbEngine = BackupDbEngine.Postgres, Enabled = true };
        var files = new BackupPolicy { Name = "files", Project = project, Server = server, DbEngine = BackupDbEngine.None, Enabled = false };
        _db.BackupPolicies.AddRange(postgres, files);
        _db.BackupRuns.AddRange(
            new BackupRun { BackupPolicy = postgres, Status = BackupRunStatus.Succeeded, RestoreCheckStatus = RestoreCheckStatus.Verified },
            new BackupRun { BackupPolicy = postgres, Status = BackupRunStatus.Failed });
        await _db.SaveChangesAsync(Ct);
        var repo = new BackupRepository(_db);

        var (policies, policyTotal) = await repo.GetPoliciesPagedAsync(null, null, null, false, 1, 10, Ct,
            [In("DbEngine", "Postgres"), In("ServerName", "db-1"), Is("Enabled", "True")]);
        var (runs, runTotal) = await repo.GetRunsForPolicyPagedAsync(postgres.Id, null, null, false, 1, 10, Ct,
            [In("RestoreCheckStatus", "Verified")]);

        Assert.Equal(1, policyTotal);
        Assert.Equal(["pg"], policies.Select(policy => policy.Name));
        Assert.Equal(1, runTotal);
        Assert.Equal(BackupRunStatus.Succeeded, runs.Single().Status);
    }

    [Fact]
    public async Task GitRepositories_AreFilteredBeforeTheCount_AndOfferTheirDefaultBranches()
    {
        var project = new Project { Name = "Website" };
        _db.GitInternalRepos.AddRange(
            new GitInternalRepo { Name = "web", Slug = "web", Project = project, DefaultBranch = "main", IsEmpty = false },
            new GitInternalRepo { Name = "docs", Slug = "docs", Project = project, DefaultBranch = "develop", IsEmpty = false },
            new GitInternalRepo { Name = "new", Slug = "new", Project = project, DefaultBranch = "main", IsEmpty = true });
        await _db.SaveChangesAsync(Ct);
        var repo = new GitLightRepository(_db);

        var (items, total) = await repo.GetAccessiblePagedAsync(null, null, null, null, false, 1, 10, Ct,
            [In("DefaultBranch", "main"), Is("IsEmpty", "False")]);
        var branches = await repo.GetDefaultBranchesAsync(null, null, Ct);

        Assert.Equal(1, total);
        Assert.Equal(["web"], items.Select(item => item.Name));
        Assert.Equal(["develop", "main"], branches);
    }

    [Fact]
    public async Task GitPullRequests_AreFilteredBeforeTheCount_AndOfferTheirAuthors()
    {
        _db.PullRequests.AddRange(
            new PullRequest { GitConnectionId = 5, ExternalId = 1, Title = "a", AuthorLogin = "alice", Status = PullRequestStatus.Open },
            new PullRequest { GitConnectionId = 5, ExternalId = 2, Title = "b", AuthorLogin = "bob", Status = PullRequestStatus.Merged },
            new PullRequest { GitConnectionId = 5, ExternalId = 3, Title = "c", AuthorLogin = "alice", Status = PullRequestStatus.Merged },
            new PullRequest { GitConnectionId = 6, ExternalId = 4, Title = "d", AuthorLogin = "carol", Status = PullRequestStatus.Merged });
        await _db.SaveChangesAsync(Ct);
        var repo = new GitRepository(_db);

        var (items, total) = await repo.GetPullRequestsPagedAsync(5, null, 1, 10, null, Ct, null, true,
            [In("AuthorLogin", "alice"), In("Status", "Merged")]);
        var authors = await repo.GetPullRequestAuthorsAsync(5, Ct);

        Assert.Equal(1, total);
        Assert.Equal([3], items.Select(item => item.ExternalId));
        Assert.Equal(["alice", "bob"], authors);
    }

    [Fact]
    public void GitCollections_AreFilteredInMemory_BeforeTheCount()
    {
        var branches = GitLightCollectionPager.Branches(
        [
            new GitLightBranchDto { Name = "main", LastCommitDate = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc) },
            new GitLightBranchDto { Name = "release/1", LastCommitDate = new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc) },
            new GitLightBranchDto { Name = "release/0", LastCommitDate = null }
        ], new PaginationRequest { Filters = [new GridFilter { Field = "Name", Operator = GridFilterOperator.StartsWith, Value = "release" }, Since("LastCommitDate", new DateTime(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc))] });
        var rules = GitLightCollectionPager.Protection(
        [
            new BranchProtectionRuleDto { Id = 1, Pattern = "main", PreventForcePush = true },
            new BranchProtectionRuleDto { Id = 2, Pattern = "release/*", PreventForcePush = false }
        ], new PaginationRequest { Filters = [Is("PreventForcePush", "False")] });

        Assert.Equal(1, branches.TotalCount);
        Assert.Equal(["release/1"], branches.Items.Select(branch => branch.Name));
        Assert.Equal([2], rules.Items.Select(rule => rule.Id));
    }

    [Fact]
    public void GitCommitAuthors_AcceptOnlyAListOnTheAuthorColumn()
    {
        Assert.Null(GitCommitListQuery.Parse(null));
        Assert.Equal(["alice", "bob"], GitCommitListQuery.Parse([In("AuthorName", "alice", "bob")])!.Authors);
        Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse([Is("Message", "fix")]));
        Assert.Throws<BadRequestException>(() => GitCommitListQuery.Parse(
            [new GridFilter { Field = "AuthorName", Operator = GridFilterOperator.Contains, Value = "al" }]));
    }

    [Fact]
    public async Task ServiceConnections_AreFilteredBeforeTheCount_AndOfferTheirProjects()
    {
        var website = new Project { Name = "Website" };
        _db.ServiceConnections.AddRange(
            new ServiceConnection { Name = "gh", Type = ServiceConnectionType.GitHub, Project = website },
            new ServiceConnection { Name = "registry", Type = ServiceConnectionType.DockerRegistry, Project = website },
            new ServiceConnection { Name = "global gh", Type = ServiceConnectionType.GitHub });
        await _db.SaveChangesAsync(Ct);
        var repo = new ServiceConnectionRepository(_db);

        var (items, total) = await repo.GetPagedAsync(null, null, 1, 10, null, Ct, null, false,
            [In("Type", "GitHub"), In("ProjectName", "website")]);
        var projects = await repo.GetProjectNamesAsync(null, Ct);

        Assert.Equal(1, total);
        Assert.Equal(["gh"], items.Select(item => item.Name));
        Assert.Equal(["Website"], projects);
    }

    [Fact]
    public async Task Plugins_AreFilteredBeforeTheCount_AndOfferTheirAuthors()
    {
        _db.PluginRegistrations.AddRange(
            new PluginRegistration { Name = "a", Version = "1", Author = "acme", Type = PluginType.Notifier, Status = PluginStatus.Enabled },
            new PluginRegistration { Name = "b", Version = "1", Author = "acme", Type = PluginType.Collector, Status = PluginStatus.Disabled },
            new PluginRegistration { Name = "c", Version = "1", Author = null, Type = PluginType.Notifier, Status = PluginStatus.Enabled });
        await _db.SaveChangesAsync(Ct);
        var repo = new PluginRepository(_db);

        var (items, total) = await repo.GetPageAsync(null, null, false, 1, 10, Ct,
            [In("Author", "acme"), In("Status", "Enabled")]);
        var authors = await repo.GetAuthorsAsync(Ct);

        Assert.Equal(1, total);
        Assert.Equal(["a"], items.Select(item => item.Name));
        Assert.Equal(["acme"], authors);
    }

    [Fact]
    public async Task NotificationChannelsAndRules_AreFilteredBeforeTheCount()
    {
        var slack = new NotificationChannel { Name = "slack-ops", Type = NotificationChannelType.Slack, IsEnabled = true };
        var mail = new NotificationChannel { Name = "mail", Type = NotificationChannelType.Email, IsEnabled = false };
        _db.NotificationChannels.AddRange(slack, mail);
        _db.NotificationRules.AddRange(
            new NotificationRule { Channel = slack, EventType = "alert.triggered", IsEnabled = true },
            new NotificationRule { Channel = mail, EventType = "alert.triggered", IsEnabled = true },
            new NotificationRule { Channel = slack, EventType = "pipeline.failed", IsEnabled = false });
        await _db.SaveChangesAsync(Ct);
        var repo = new NotificationRepository(_db);

        var (channels, channelTotal) = await repo.GetChannelsPagedAsync(null, 1, 10, null, false, Ct,
            [In("Type", "Slack"), Is("IsEnabled", "True")]);
        var (rules, ruleTotal) = await repo.GetRulesPagedAsync(null, 1, 10, null, false, Ct,
            [In("ChannelName", "slack-ops"), Is("IsEnabled", "True")]);
        var (eventTypes, channelNames) = await repo.GetRuleFilterValuesAsync(Ct);

        Assert.Equal(1, channelTotal);
        Assert.Equal(["slack-ops"], channels.Select(channel => channel.Name));
        Assert.Equal(1, ruleTotal);
        Assert.Equal(["alert.triggered"], rules.Select(rule => rule.EventType));
        Assert.Equal(["alert.triggered", "pipeline.failed"], eventTypes);
        Assert.Equal(["mail", "slack-ops"], channelNames);
    }

    [Fact]
    public void PipelineFleet_ColumnsFilterTheProjectedRow()
    {
        PipelineFleetItemDto[] fleet =
        [
            new() { PipelineId = 1, PipelineName = "web", TemplateName = "ci", Freshness = PipelineFleetFreshness.Current },
            new() { PipelineId = 2, PipelineName = "api", TemplateName = "ci", Freshness = PipelineFleetFreshness.Outdated },
            new() { PipelineId = 3, PipelineName = "docs", TemplateName = null, Freshness = PipelineFleetFreshness.OffCatalog }
        ];

        var ids = PipelineFleetQuery.Columns
            .ApplyFilters(fleet.AsQueryable(), [In("TemplateName", "CI"), In("Freshness", "Outdated", "OffCatalog")])
            .Select(item => item.PipelineId);

        Assert.Equal([2], ids);
    }
}
