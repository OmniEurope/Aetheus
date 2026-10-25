// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

internal static class BreadcrumbRouteResolver
{
    private static readonly HashSet<string> AdministrativeAliasRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "api-reference", "audit", "dashboards", "plugins", "users"
    };

    private static readonly IReadOnlyDictionary<string, string> SegmentKeys =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["alerts"] = "Alerts",
            ["ai-profiles"] = "AiRunnerProfiles",
            ["ai-tasks"] = "AiTasks",
            ["analysis"] = "AnalysisPortfolio",
            ["api-reference"] = "ApiReference",
            ["apps"] = "Apps",
            ["artifacts"] = "Artifacts",
            ["audit"] = "AuditLogs",
            ["backups"] = "Backups",
            ["board"] = "Board",
            ["certbot"] = "Certbot",
            ["configuration"] = "Configuration",
            ["cron"] = "Cron",
            ["dashboards"] = "Dashboards",
            ["docker"] = "Docker",
            ["environments"] = "Environments",
            ["external-repo"] = "ExternalRepo",
            ["firewall"] = "FirewallTitle",
            ["git-repositories"] = "GitRepositories",
            ["help"] = "HelpCenter",
            ["libraries"] = "Libraries",
            ["logs"] = "LiveLogs",
            ["mail"] = "Mail",
            ["modules"] = "Modules",
            ["monitoring"] = "Monitoring",
            ["notifications"] = "NotificationRules",
            ["organizations"] = "Organizations",
            ["overview"] = "Overview",
            ["package-feeds"] = "PackageFeeds",
            ["package-registry"] = "PackageRegistry",
            ["pipelines"] = "Pipelines",
            ["plugins"] = "Plugins",
            ["ports"] = "PortRegistryTitle",
            ["portsentry"] = "PortSentry",
            ["projects"] = "Projects",
            ["properties"] = "Properties",
            ["quality"] = "Quality",
            ["releases"] = "Releases",
            ["rkhunter"] = "RKHunter",
            ["roles"] = "Roles",
            ["servers"] = "Servers",
            ["services"] = "Services",
            ["service-connections"] = "ServiceConnections",
            ["settings"] = "Settings",
            ["system-logs"] = "SystemLogs",
            ["tasks"] = "Tasks",
            ["teamspeak"] = "TeamSpeak",
            ["templates"] = "PipelineTemplates",
            ["updates"] = "ServerUpdates",
            ["users"] = "Users",
            ["variable-libraries"] = "VariableLibraries",
            ["vaults"] = "Vaults"
        };

    public static IReadOnlyList<BreadcrumbItem> Resolve(string relativePath, Func<string, string> localize)
    {
        var path = relativePath.Split('?', '#')[0].Trim('/');
        if (path.Length == 0)
            return [new BreadcrumbItem(localize("Dashboard"))];

        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var items = new List<BreadcrumbItem>();
        var offset = 0;
        if (segments[0].Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new BreadcrumbItem(localize("Administration"), segments.Length > 1 ? "/admin" : null));
            offset = 1;
            if (segments.Length == 1) return items;
        }
        else if (AdministrativeAliasRoots.Contains(segments[0]))
        {
            items.Add(new BreadcrumbItem(localize("Administration"), "/admin"));
        }

        var route = segments[offset].ToLowerInvariant();
        if (TryAddFixedRoute(items, route, localize)) return items;
        if (TryAddStructuredRoute(items, route, segments, offset, localize)) return items;

        AddGenericPath(items, segments, offset, localize);
        return items;
    }

    private static bool TryAddFixedRoute(
        List<BreadcrumbItem> items,
        string route,
        Func<string, string> localize)
    {
        switch (route)
        {
            case "login":
                items.Add(new BreadcrumbItem(localize("Login")));
                return true;
            case "not-found":
                items.Add(new BreadcrumbItem(localize("NotFound")));
                return true;
            case "account":
                items.Add(new BreadcrumbItem(localize("Settings"), "/settings"));
                items.Add(new BreadcrumbItem(localize("ChangePassword")));
                return true;
            default:
                return false;
        }
    }

    private static bool TryAddStructuredRoute(
        List<BreadcrumbItem> items,
        string route,
        string[] segments,
        int offset,
        Func<string, string> localize)
    {
        switch (route)
        {
            case "pipelines":
                AddPipelinePath(items, segments, offset, localize);
                return true;
            case "projects":
                AddOwnedPath(items, segments, offset, "Projects", "Project", "/projects", localize);
                return true;
            case "servers":
                AddOwnedPath(items, segments, offset, "Servers", "Server", "/servers", localize);
                return true;
            case "git-repositories":
                AddGitPath(items, segments, offset, localize);
                return true;
            case "git":
                AddLegacyGitPath(items, segments, offset, localize);
                return true;
            case "templates":
                AddTemplatePath(items, segments, offset, localize);
                return true;
            case "analysis":
                AddAnalysisPath(items, segments, offset, localize);
                return true;
            default:
                return false;
        }
    }

    private static void AddPipelinePath(List<BreadcrumbItem> items, string[] segments, int offset, Func<string, string> localize)
    {
        items.Add(new BreadcrumbItem(localize("Pipelines"), segments.Length > offset + 1 ? "/pipelines" : null));
        if (segments.Length == offset + 1) return;
        var second = segments[offset + 1];
        if (second.Equals("new", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new BreadcrumbItem(localize("NewPipeline")));
            return;
        }
        if (second.Equals("fleet", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new BreadcrumbItem(localize("PipelineFleet")));
            return;
        }
        if (second.Equals("runs", StringComparison.OrdinalIgnoreCase) && segments.Length > offset + 2)
        {
            items.Add(new BreadcrumbItem(
                $"{localize("PipelineRun")} #{segments[offset + 2]}",
                IsLoading: true));
            return;
        }
        items.Add(new BreadcrumbItem(
            $"{localize("Pipeline")} #{second}",
            segments.Length > offset + 2 ? $"/pipelines/{second}" : null,
            IsLoading: true));
        if (segments.Length > offset + 2)
            items.Add(new BreadcrumbItem(PipelineActionLabel(segments, offset + 2, localize)));
    }

    private static string PipelineActionLabel(string[] segments, int index, Func<string, string> localize)
    {
        if (segments[index].Equals("template", StringComparison.OrdinalIgnoreCase) && segments.Length > index + 1)
            return segments[index + 1].ToLowerInvariant() switch
            {
                "extract" => localize("ExtractAsTemplate"),
                "promote" => localize("PromoteToTemplate"),
                "update" => localize("UpdatePipelineTemplate"),
                _ => localize("PipelineTemplates")
            };
        return Label(segments[index], localize);
    }

    private static void AddOwnedPath(List<BreadcrumbItem> items, string[] segments, int offset,
        string pluralKey, string singularKey, string rootHref, Func<string, string> localize)
    {
        items.Add(new BreadcrumbItem(localize(pluralKey), segments.Length > offset + 1 ? rootHref : null));
        if (segments.Length == offset + 1) return;
        var id = segments[offset + 1];
        if (id.Equals("new", StringComparison.OrdinalIgnoreCase) || id.Equals("add-agent", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new BreadcrumbItem(localize(id.Equals("new", StringComparison.OrdinalIgnoreCase)
                ? $"New{singularKey}"
                : "AddAgent")));
            return;
        }
        var detailHref = segments.Length > offset + 2 ? $"{rootHref}/{id}/overview" : null;
        items.Add(new BreadcrumbItem($"{localize(singularKey)} #{id}", detailHref, IsLoading: true));
        if (segments.Length > offset + 2)
            items.Add(new BreadcrumbItem(Label(segments[offset + 2], localize)));
    }

    private static void AddGitPath(List<BreadcrumbItem> items, string[] segments, int offset, Func<string, string> localize)
    {
        items.Add(new BreadcrumbItem(localize("GitRepositories"), segments.Length > offset + 1 ? "/git-repositories" : null));
        if (segments.Length == offset + 1) return;
        if (segments[offset + 1].Equals("commits", StringComparison.OrdinalIgnoreCase) ||
            segments[offset + 1].Equals("branches", StringComparison.OrdinalIgnoreCase))
        {
            items.Add(new BreadcrumbItem(
                Label(segments[offset + 1], localize),
                IsLoading: segments.Length > offset + 2));
            return;
        }
        var repositoryId = segments[offset + 1];
        items.Add(new BreadcrumbItem($"{localize("Repository")} #{repositoryId}",
            segments.Length > offset + 2 ? $"/git-repositories/{repositoryId}" : null,
            IsLoading: true));
        if (segments.Length > offset + 2)
            items.Add(new BreadcrumbItem(segments[offset + 2].Equals("commits", StringComparison.OrdinalIgnoreCase)
                ? localize("CommitDetails")
                : Label(segments[offset + 2], localize)));
    }

    private static void AddLegacyGitPath(List<BreadcrumbItem> items, string[] segments, int offset, Func<string, string> localize)
    {
        items.Add(new BreadcrumbItem(localize("GitRepositories"), "/git-repositories"));
        items.Add(new BreadcrumbItem(segments.Length > offset + 1 && segments[offset + 1].Equals("branches", StringComparison.OrdinalIgnoreCase)
            ? localize("Branches")
            : localize("CommitDetails"),
            IsLoading: segments.Length > offset + 2));
    }

    private static void AddTemplatePath(List<BreadcrumbItem> items, string[] segments, int offset, Func<string, string> localize)
    {
        items.Add(new BreadcrumbItem(localize("PipelineTemplates"), segments.Length > offset + 1 ? "/templates" : null));
        if (segments.Length == offset + 1) return;
        var id = segments[offset + 1];
        items.Add(id.Equals("new", StringComparison.OrdinalIgnoreCase)
            ? new BreadcrumbItem(localize("NewTemplate"))
            : new BreadcrumbItem(
                $"{localize("Template")} #{id}",
                segments.Length > offset + 2 ? $"/templates/{id}" : null,
                IsLoading: true));
        if (segments.Length > offset + 2)
            items.Add(new BreadcrumbItem(localize("VersionHistoryTitle")));
    }

    private static void AddAnalysisPath(List<BreadcrumbItem> items, string[] segments, int offset, Func<string, string> localize)
    {
        items.Add(new BreadcrumbItem(localize("AnalysisPortfolio"), segments.Length > offset + 1 ? "/analysis" : null));
        if (segments.Length > offset + 1)
            items.Add(new BreadcrumbItem(localize("AnalysisFindingDetail"), IsLoading: true));
    }

    private static void AddGenericPath(List<BreadcrumbItem> items, string[] segments, int offset, Func<string, string> localize)
    {
        var segment = segments[offset];
        var rootLabel = Label(segment, localize);
        items.Add(new BreadcrumbItem(rootLabel, segments.Length > offset + 1 ? $"/{segment}" : null));
        if (segments.Length == offset + 1) return;
        var detail = segments[offset + 1];
        items.Add(detail.Equals("new", StringComparison.OrdinalIgnoreCase)
            ? new BreadcrumbItem($"{localize("New")} {Singular(rootLabel)}")
            : new BreadcrumbItem($"{Singular(rootLabel)} #{detail}", IsLoading: true));
    }

    private static string Label(string segment, Func<string, string> localize)
    {
        if (SegmentKeys.TryGetValue(segment, out var key)) return localize(key);
        return string.Join(' ', segment.Split('-', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
    }

    private static string Singular(string value) => value.EndsWith('s') ? value[..^1] : value;
}
