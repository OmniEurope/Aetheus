// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using Aetheus.Front.Components.Git;
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Components.Plugins;
using Bunit;
using BackupsPage = Aetheus.Front.Components.AppBackups.Backups;
using ServiceConnectionsPage = Aetheus.Front.Components.ServiceConnections.ServiceConnections;

namespace Aetheus.Front.Tests.Pages;

/// <summary> On the server-paged grids of these pages a header filter reaches the API as a column
/// filter (<c>Filters[i].Field</c>...), so the server filters the whole list, not the page on
/// screen; and a live refresh keeps the filter the header shows. </summary>
public sealed class GridHeaderFilterApiTests : BunitContext
{
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private readonly BunitTestHelper.TestHandler _handler;

    public GridHeaderFilterApiTests() => _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);

    private static GridLoadArgs Filtered(string field, string value) => new()
    {
        Skip = 0,
        Top = 25,
        Filters = [new GridFilterDescriptor(field, value, OmniDataGridFilterOperator.Equals)]
    };

    private bool Sent(string path, string field, string value) => _handler.Requests.Any(request =>
        Uri.UnescapeDataString(request.Url).Contains(path, StringComparison.Ordinal)
        && Uri.UnescapeDataString(request.Url).Contains($"Filters[0].Field={field}", StringComparison.Ordinal)
        && Uri.UnescapeDataString(request.Url).Contains($"Filters[0].Value={value}", StringComparison.Ordinal));

    private static Task InvokePrivate(object instance, string method, params object[] arguments) =>
        (Task)instance.GetType().GetMethod(method, Private)!.Invoke(instance, arguments)!;

    [Fact]
    public async Task Backups_PolicyAndRunFilters_ReachTheApi()
    {
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups", new List<BackupPolicyDto>());
        _handler.SetPaginatedJsonResponse(HttpMethod.Get, "api/backups/3/runs", new List<BackupRunDto>());
        _handler.SetJsonResponse(HttpMethod.Get, "api/projects", new PaginatedResult<ProjectDto> { Items = [] });
        _handler.SetJsonResponse(HttpMethod.Get, "api/servers", new PaginatedResult<ServerDto> { Items = [] });
        var cut = Render<BackupsPage>();

        await cut.InvokeAsync(() => cut.Instance.OnLoadPoliciesAsync(Filtered("DbEngine", "Postgres")));
        typeof(BackupsPage).GetField("_runsFor", Private)!.SetValue(cut.Instance, new BackupPolicyDto { Id = 3, Name = "nightly" });
        await cut.InvokeAsync(() => cut.Instance.OnLoadRunsAsync(Filtered("Status", "Failed")));

        Assert.True(Sent("api/backups?", "DbEngine", "Postgres"));
        Assert.True(Sent("api/backups/3/runs", "Status", "Failed"));
    }

    [Fact]
    public async Task GitRepositories_Filter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/git/repos/filter-values", new GitRepositoryFilterValuesDto { DefaultBranches = ["main"] });
        _handler.SetPaginatedJsonResponse("api/git/repos", new List<GitLightRepoDto>());
        var cut = Render<GitRepositories>();

        await cut.InvokeAsync(() => cut.Instance.OnLoadDataAsync(Filtered("DefaultBranch", "main")));

        Assert.True(Sent("api/git/repos?", "DefaultBranch", "main"));
        Assert.Contains(_handler.Requests, request => request.Url.Contains("api/git/repos/filter-values", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GitRepositoryDetail_CommitAndPullRequestFilters_ReachTheApi()
    {
        _handler.SetJsonResponse("api/git/repos/1", new GitLightRepoDto { Id = 1, ProjectId = 1, Name = "repo", DefaultBranch = "main" });
        _handler.SetJsonResponse("api/git/repos/1/filter-values", new GitRepositoryDetailFilterValuesDto { CommitAuthors = ["alice"] });
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branches", new List<GitLightBranchDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tags", new List<GitLightTagDto>());
        _handler.SetJsonResponse("api/git/repos/1/commits", new PaginatedResult<GitLightCommitDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/1/tree", new List<GitLightTreeEntryDto>());
        _handler.SetJsonResponse("api/git/repos/1/pull-requests", new PaginatedResult<InternalPullRequestDto>());
        _handler.SetPaginatedJsonResponse("api/git/repos/1/branch-protection", new List<BranchProtectionRuleDto>());
        _handler.SetJsonResponse("api/git/repos/1/graph-data", new List<GitLightCommitDto>());
        var cut = Render<GitRepositoryDetail>(parameters => parameters.Add(page => page.Id, 1));
        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Url.Contains("api/git/repos/1/filter-values", StringComparison.Ordinal)));

        await cut.InvokeAsync(() => InvokePrivate(cut.Instance, "OnLoadCommits", Filtered("AuthorName", "alice")));
        await cut.InvokeAsync(() => InvokePrivate(cut.Instance, "OnLoadPrs", Filtered("Status", "Merged")));
        await cut.InvokeAsync(() => InvokePrivate(cut.Instance, "OnLoadBranches", Filtered("Name", "release")));

        Assert.True(Sent("api/git/repos/1/commits", "AuthorName", "alice"));
        Assert.True(Sent("api/git/repos/1/pull-requests", "Status", "Merged"));
        Assert.True(Sent("api/git/repos/1/branches", "Name", "release"));
    }

    /// <summary>Refresh through the grid so a live update retains its current header
    /// filters.</summary>
    [Fact]
    public async Task ServiceConnections_LiveRefresh_KeepsTheHeaderFilter()
    {
        _handler.SetJsonResponse("api/service-connections/filter-values", new ServiceConnectionFilterValuesDto { Projects = ["Website"] });
        _handler.SetJsonResponse("api/service-connections", new PaginatedResult<ServiceConnectionDto>
        {
            Items = [new ServiceConnectionDto { Id = 1, Name = "GitHub prod", Type = ServiceConnectionType.GitHub }],
            TotalCount = 1
        });
        var cut = Render<ServiceConnectionsPage>();
        cut.WaitForState(() => cut.Markup.Contains("GitHub prod", StringComparison.Ordinal));
        var grid = cut.FindComponent<AetheusDataGrid<ServiceConnectionDto>>().Instance.Grid!;
        await cut.InvokeAsync(() => grid.SetFiltersAsync(new Dictionary<string, string?> { ["Name"] = "prod" }));
        cut.WaitForAssertion(() => Assert.True(Sent("api/service-connections?", "Name", "prod")));
        var before = _handler.Requests.Count;

        await cut.InvokeAsync(() => InvokePrivate(cut.Instance, "RefreshGridAsync"));

        cut.WaitForAssertion(() => Assert.True(_handler.Requests.Count > before));
        var refreshed = Uri.UnescapeDataString(_handler.Requests.Skip(before)
            .Last(request => request.Url.Contains("api/service-connections?", StringComparison.Ordinal)).Url);
        Assert.Contains("Filters[0].Field=Name", refreshed, StringComparison.Ordinal);
        Assert.Contains("Filters[0].Value=prod", refreshed, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ServiceConnections_Filter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/service-connections/filter-values", new ServiceConnectionFilterValuesDto());
        _handler.SetJsonResponse("api/service-connections", new PaginatedResult<ServiceConnectionDto>());
        var cut = Render<ServiceConnectionsPage>();

        await cut.InvokeAsync(() => cut.Instance.LoadData(Filtered("Type", "GitHub")));

        Assert.True(Sent("api/service-connections?", "Type", "GitHub"));
    }

    [Fact]
    public async Task Plugins_Filter_ReachesTheApi()
    {
        _handler.SetJsonResponse("api/plugins/filter-values", new PluginFilterValuesDto { Authors = ["acme"] });
        _handler.SetPaginatedJsonResponse("api/plugins", new List<PluginRegistrationDto>());
        var cut = Render<PluginManagement>();

        await cut.InvokeAsync(() => cut.Instance.OnLoadDataAsync(Filtered("Status", "Enabled")));

        Assert.True(Sent("api/plugins?", "Status", "Enabled"));
    }

    [Fact]
    public async Task NotificationsAdmin_ChannelAndRuleFilters_ReachTheApi()
    {
        _handler.SetJsonResponse("api/notifications/rules/filter-values", new NotificationAdminFilterValuesDto { Channels = ["ops"] });
        _handler.SetJsonResponse("api/notifications/channels", new PaginatedResult<NotificationChannelDto>());
        _handler.SetJsonResponse("api/notifications/rules", new PaginatedResult<NotificationRuleDto>());
        var cut = Render<NotificationsAdmin>();

        await cut.InvokeAsync(() => cut.Instance.LoadChannelsAsync(Filtered("Type", "Slack")));
        await cut.InvokeAsync(() => cut.Instance.LoadRulesAsync(Filtered("ChannelName", "ops")));

        Assert.True(Sent("api/notifications/channels", "Type", "Slack"));
        Assert.True(Sent("api/notifications/rules?", "ChannelName", "ops"));
    }
}
