// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.E2E;

/// <summary>What fills a route parameter of a page template, taken from the seeded E2E data.</summary>
internal enum PageEntity
{
    Server,
    ServerMailAccount,
    Project,
    Pipeline,
    PipelineLinkedToTemplate,
    PipelineRun,
    PipelineApprovalRun,
    PipelineApproval,
    PipelineTemplate,
    PipelineTemplateTargetVersion,
    Environment,
    Vault,
    VariableLibrary,
    Release,
    User,
    Role,
    Organization,
    Dashboard,
    TaskItem,
    AlertOccurrence,
    AnalysisFinding,
    Artifact,
    AiTask,
    GitRepository,
    GitBranch,
    GitCommit,
    GitCommitSha,
    HelpArticleKey,
    SettingsTab
}

/// <summary>One <c>@page</c> directive of <c>src/Aetheus.Front</c>: its template, its component, and the entities that
/// fill its route parameters, in the order the parameters appear in the template.</summary>
internal sealed record PageRoute(string Template, string Component, params PageEntity[] Parameters)
{
    private static readonly Regex Placeholder = new(@"\{[A-Za-z]+(:[a-z]+)?\}", RegexOptions.Compiled);

    public int PlaceholderCount => Placeholder.Matches(Template).Count;

    /// <summary>The URL path for the given parameter values, in template order.</summary>
    public string Fill(IReadOnlyList<string> values)
    {
        var index = 0;
        return Placeholder.Replace(Template, _ => values[index++]);
    }
}

/// <summary>
/// Every routable page of <c>Aetheus.Front</c>, copied from its <c>@page</c> directives (136 of them, one entry per
/// directive, so a component with two routes appears twice). The build captures the directives of the front into the
/// <c>front-page-directives.txt</c> resource, and <see cref="PageRulesTests"/> fails when this list differs from it: a
/// page added to or removed from the front must be added here, with the entity that fills its parameters.
/// </summary>
internal static class PageInventory
{
    private const string Servers = "Components/Servers/Sections/";
    private const string Projects = "Components/Projects/Sections/";

    /// <summary>The address of every page for the seeded data, or the reason a page has none.</summary>
    public static IReadOnlyList<PageTarget> Resolve(ResolvedEntities entities)
    {
        var targets = new List<PageTarget>();
        foreach (var route in All)
        {
            var unresolved = route.Parameters.Where(entity => !entities.Values.ContainsKey(entity)).ToList();
            if (unresolved.Count > 0)
            {
                var reason = entities.Missing.TryGetValue(unresolved[0], out var why) ? why : $"{unresolved[0]} was not resolved";
                targets.Add(new PageTarget(route, null, reason));
                continue;
            }
            var values = route.Parameters.Select(entity => entities.Values[entity]).ToList();
            targets.Add(new PageTarget(route, route.Fill(values), null));
        }
        return targets;
    }

    /// <summary>The template of the page a path belongs to (the most specific one that matches), or the path with its
    /// numbers replaced by <c>{n}</c> when no page of the inventory matches.</summary>
    public static string TemplateOf(string path)
    {
        var match = All
            .Where(route => TemplatePattern(route.Template).IsMatch(path))
            .OrderBy(route => route.PlaceholderCount)
            .FirstOrDefault();
        return match?.Template ?? Regex.Replace(path, @"/\d+(?=/|$)", "/{n}");
    }

    private static Regex TemplatePattern(string template) =>
        new("^" + Regex.Replace(Regex.Escape(template), @"\\\{[A-Za-z]+(:[a-z]+)?\}", "[^/]+") + "$");

    public static readonly IReadOnlyList<PageRoute> All =
    [
        new("/", "Components/Dashboards/Home.razor"),
        new("/account/change-password", "Components/Auth/ChangePasswordRequired.razor"),
        new("/admin", "Components/Settings/Administration.razor"),
        new("/admin/ai-profiles", "Components/AiTasks/AiRunnerProfiles.razor"),
        new("/admin/api-reference", "Components/Help/ApiReference.razor"),
        new("/admin/audit", "Components/Audit/AuditLogs.razor"),
        new("/admin/dashboards", "Components/Dashboards/Dashboards.razor"),
        new("/admin/mail-templates", "Components/MailTemplates/MailTemplates.razor"),
        new("/admin/notifications", "Components/Notifications/NotificationsAdmin.razor"),
        new("/admin/organizations", "Components/Organizations/Organizations.razor"),
        new("/admin/organizations/new", "Components/Organizations/OrganizationEdit.razor"),
        new("/admin/organizations/{Id:int}", "Components/Organizations/OrganizationDetail.razor", PageEntity.Organization),
        new("/admin/organizations/{Id:int}/edit", "Components/Organizations/OrganizationEdit.razor", PageEntity.Organization),
        new("/admin/package-feeds", "Components/PackageFeeds/PackageFeedsAdmin.razor"),
        new("/admin/package-registry", "Components/PackageRegistry/PackageRegistryAdmin.razor"),
        new("/admin/performance", "Components/Logs/Performance.razor"),
        new("/admin/plugins", "Components/Plugins/PluginManagement.razor"),
        new("/admin/roles", "Components/Users/Roles.razor"),
        new("/admin/roles/new", "Components/Users/RoleEdit.razor"),
        new("/admin/roles/{Id:int}", "Components/Users/RoleEdit.razor", PageEntity.Role),
        new("/admin/settings", "Components/Settings/AdminSettings.razor"),
        new("/admin/system-logs", "Components/Logs/SystemLogs.razor"),
        new("/admin/users", "Components/Users/Users.razor"),
        new("/admin/users/new", "Components/Users/UserCreate.razor"),
        new("/admin/users/{Id:int}", "Components/Users/UserEdit.razor", PageEntity.User),
        new("/ai-tasks", "Components/AiTasks/AiTasks.razor"),
        new("/ai-tasks/{TaskId:int}", "Components/AiTasks/AiTaskResults.razor", PageEntity.AiTask),
        new("/alerts", "Components/Alerts/Alerts.razor"),
        new("/alerts/{AlertId:long}", "Components/Alerts/AlertOccurrenceDetail.razor", PageEntity.AlertOccurrence),
        new("/analysis", "Components/Analysis/AnalysisPortfolio.razor"),
        new("/analysis/findings/{Id:int}", "Components/Analysis/AnalysisFindingDetail.razor", PageEntity.AnalysisFinding),
        new("/api-reference", "Components/Help/ApiReference.razor"),
        new("/artifacts/{ArtifactId:int}", "Components/Artifacts/ArtifactDetail.razor", PageEntity.Artifact),
        new("/audit", "Components/Audit/AuditLogs.razor"),
        new("/backups", "Components/AppBackups/Backups.razor"),
        new("/dashboards", "Components/Dashboards/Dashboards.razor"),
        new("/dashboards/{Id:int}", "Components/Dashboards/DashboardEdit.razor", PageEntity.Dashboard),
        new("/environments", "Components/Environments/Environments.razor"),
        new("/environments/new", "Components/Environments/EnvironmentEdit.razor"),
        new("/environments/{Id:int}", "Components/Environments/EnvironmentEdit.razor", PageEntity.Environment),
        new("/git-repositories", "Components/Git/GitRepositories.razor"),
        new("/git-repositories/branches/{BranchId:int}", "Components/Git/BranchDetail.razor", PageEntity.GitBranch),
        new("/git-repositories/commits/{CommitId:int}", "Components/Git/CommitDetail.razor", PageEntity.GitCommit),
        new("/git-repositories/{Id:int}", "Components/Git/GitRepositoryDetail.razor", PageEntity.GitRepository),
        new("/git-repositories/{RepoId:int}/commits/{Sha}", "Components/Git/GitCommitDetail.razor", PageEntity.GitRepository, PageEntity.GitCommitSha),
        new("/git/branches/{BranchId:int}", "Components/Git/BranchDetail.razor", PageEntity.GitBranch),
        new("/git/commits/{CommitId:int}", "Components/Git/CommitDetail.razor", PageEntity.GitCommit),
        new("/help", "Components/Help/HelpCenter.razor"),
        new("/help/{PageKey}", "Components/Help/HelpArticle.razor", PageEntity.HelpArticleKey),
        new("/login", "Components/Auth/Login.razor"),
        new("/login/set-password", "Components/Auth/SetPassword.razor"),
        new("/logs", "Components/Logs/Logs.razor"),
        new("/mail", "Components/Mail/MailboxDirectory.razor"),
        new("/monitoring", "Components/Monitoring/Supervision.razor"),
        new("/not-found", "Components/Shared/NotFound.razor"),
        new("/notifications", "Components/Notifications/NotificationsInbox.razor"),
        new("/pipelines", "Components/Pipelines/Pipelines.razor"),
        new("/pipelines/fleet", "Components/Pipelines/Pipelines.razor"),
        new("/pipelines/new", "Components/Pipelines/PipelineEdit.razor"),
        new("/pipelines/runs/{RunId:int}", "Components/Pipelines/PipelineRun.razor", PageEntity.PipelineRun),
        new("/pipelines/runs/{RunId:int}/approvals/{ApprovalId:int}", "Components/Pipelines/PipelineApprovalLink.razor", PageEntity.PipelineApprovalRun, PageEntity.PipelineApproval),
        new("/pipelines/setup", "Components/Pipelines/PipelineSetupWizard.razor"),
        new("/pipelines/{Id:int}", "Components/Pipelines/PipelineEdit.razor", PageEntity.Pipeline),
        new("/pipelines/{PipelineId:int}/template/extract", "Components/Pipelines/PipelineTemplateExtract.razor", PageEntity.Pipeline),
        new("/pipelines/{PipelineId:int}/template/promote", "Components/Pipelines/PipelineTemplatePromote.razor", PageEntity.PipelineLinkedToTemplate),
        new("/pipelines/{PipelineId:int}/template/update/{TargetVersion:int}", "Components/Pipelines/PipelineTemplateUpdate.razor", PageEntity.PipelineLinkedToTemplate, PageEntity.PipelineTemplateTargetVersion),
        new("/plugins", "Components/Plugins/PluginManagement.razor"),
        new("/projects", "Components/Projects/Projects.razor"),
        new("/projects/new", "Components/Projects/ProjectEdit.razor"),
        new("/projects/{Id:int}", "Components/Projects/ProjectEdit.razor", PageEntity.Project),
        new("/projects/{Id:int}/artifacts", Projects + "Artifacts.razor", PageEntity.Project),
        new("/projects/{Id:int}/backups", Projects + "Backups.razor", PageEntity.Project),
        new("/projects/{Id:int}/board", Projects + "Board.razor", PageEntity.Project),
        new("/projects/{Id:int}/edit", Projects + "Edit.razor", PageEntity.Project),
        new("/projects/{Id:int}/environments", Projects + "Environments.razor", PageEntity.Project),
        new("/projects/{Id:int}/external-repo", Projects + "ExternalRepo.razor", PageEntity.Project),
        new("/projects/{Id:int}/libraries", Projects + "Libraries.razor", PageEntity.Project),
        new("/projects/{Id:int}/logs", Projects + "Logs.razor", PageEntity.Project),
        new("/projects/{Id:int}/monitoring", Projects + "Monitoring.razor", PageEntity.Project),
        new("/projects/{Id:int}/overview", Projects + "Overview.razor", PageEntity.Project),
        new("/projects/{Id:int}/pipelines", Projects + "Pipelines.razor", PageEntity.Project),
        new("/projects/{Id:int}/quality", Projects + "Quality.razor", PageEntity.Project),
        new("/projects/{Id:int}/releases", Projects + "Releases.razor", PageEntity.Project),
        new("/projects/{Id:int}/servers", Projects + "Servers.razor", PageEntity.Project),
        new("/projects/{Id:int}/tasks", Projects + "Tasks.razor", PageEntity.Project),
        new("/projects/{Id:int}/vaults", Projects + "Vaults.razor", PageEntity.Project),
        new("/projects/{ProjectId:int}/ai-tasks", "Components/AiTasks/AiTasks.razor", PageEntity.Project),
        new("/projects/{ProjectId:int}/ai-tasks/{TaskId:int}", "Components/AiTasks/AiTaskResults.razor", PageEntity.Project, PageEntity.AiTask),
        new("/releases", "Components/Releases/Releases.razor"),
        new("/releases/{ReleaseId:int}", "Components/Releases/ReleaseDetail.razor", PageEntity.Release),
        new("/servers", "Components/Servers/Servers.razor"),
        new("/servers/add-agent", "Components/Servers/AddAgent.razor"),
        new("/servers/{Id:int}", "Components/Servers/ServerDetail.razor", PageEntity.Server),
        new("/servers/{Id:int}/apache", Servers + "Apache.razor", PageEntity.Server),
        new("/servers/{Id:int}/apps", Servers + "Apps.razor", PageEntity.Server),
        new("/servers/{Id:int}/certbot", Servers + "Certbot.razor", PageEntity.Server),
        new("/servers/{Id:int}/configuration", Servers + "Configuration.razor", PageEntity.Server),
        new("/servers/{Id:int}/cron", Servers + "Cron.razor", PageEntity.Server),
        new("/servers/{Id:int}/docker", Servers + "Docker.razor", PageEntity.Server),
        new("/servers/{Id:int}/firewall", Servers + "Firewall.razor", PageEntity.Server),
        new("/servers/{Id:int}/libraries", Servers + "Libraries.razor", PageEntity.Server),
        new("/servers/{Id:int}/logs", Servers + "Logs.razor", PageEntity.Server),
        new("/servers/{Id:int}/mail", Servers + "Mail.razor", PageEntity.Server),
        new("/servers/{Id:int}/mail/accounts/{AccountId:int}/mailbox", Servers + "Mailbox.razor", PageEntity.Server, PageEntity.ServerMailAccount),
        new("/servers/{Id:int}/modules", Servers + "Modules.razor", PageEntity.Server),
        new("/servers/{Id:int}/overview", Servers + "Overview.razor", PageEntity.Server),
        new("/servers/{Id:int}/pipelines", Servers + "Pipelines.razor", PageEntity.Server),
        new("/servers/{Id:int}/ports", Servers + "Ports.razor", PageEntity.Server),
        new("/servers/{Id:int}/portsentry", Servers + "Portsentry.razor", PageEntity.Server),
        new("/servers/{Id:int}/projects", Servers + "Projects.razor", PageEntity.Server),
        new("/servers/{Id:int}/properties", Servers + "Properties.razor", PageEntity.Server),
        new("/servers/{Id:int}/releases", Servers + "Releases.razor", PageEntity.Server),
        new("/servers/{Id:int}/rkhunter", Servers + "Rkhunter.razor", PageEntity.Server),
        new("/servers/{Id:int}/services", Servers + "Services.razor", PageEntity.Server),
        new("/servers/{Id:int}/tasks", Servers + "Tasks.razor", PageEntity.Server),
        new("/servers/{Id:int}/teamspeak", Servers + "Teamspeak.razor", PageEntity.Server),
        new("/servers/{Id:int}/updates", Servers + "Updates.razor", PageEntity.Server),
        new("/servers/{Id:int}/vaults", Servers + "Vaults.razor", PageEntity.Server),
        new("/service-connections", "Components/ServiceConnections/ServiceConnections.razor"),
        new("/settings", "Components/Settings/Settings.razor"),
        new("/settings/{Tab}", "Components/Settings/Settings.razor", PageEntity.SettingsTab),
        new("/tasks", "Components/Tasks/Tasks.razor"),
        new("/tasks/{TaskId:int}", "Components/Tasks/TaskDetail.razor", PageEntity.TaskItem),
        new("/templates", "Components/Pipelines/Pipelines.razor"),
        new("/templates/new", "Components/Pipelines/PipelineTemplateEdit.razor"),
        new("/templates/{Id:int}", "Components/Pipelines/PipelineTemplateEdit.razor", PageEntity.PipelineTemplate),
        new("/templates/{TemplateId:int}/versions", "Components/Pipelines/PipelineTemplateHistory.razor", PageEntity.PipelineTemplate),
        new("/users", "Components/Users/Users.razor"),
        new("/users/new", "Components/Users/UserCreate.razor"),
        new("/users/{Id:int}", "Components/Users/UserEdit.razor", PageEntity.User),
        new("/variable-libraries", "Components/VariableLibraries/VariableLibraries.razor"),
        new("/variable-libraries/new", "Components/VariableLibraries/VariableLibraryEdit.razor"),
        new("/variable-libraries/{Id:int}", "Components/VariableLibraries/VariableLibraryEdit.razor", PageEntity.VariableLibrary),
        new("/vaults", "Components/Vaults/Vaults.razor"),
        new("/vaults/new", "Components/Vaults/VaultEdit.razor"),
        new("/vaults/{Id:int}", "Components/Vaults/VaultEdit.razor", PageEntity.Vault)
    ];
}
