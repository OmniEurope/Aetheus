// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net.Http.Json;

namespace Aetheus.Front.Components.Shared;

public sealed class HelpService
{
    private readonly HttpClient _http;
    private readonly Dictionary<string, IReadOnlyList<HelpArticle>> _cache = [];

    public HelpService(HttpClient http)
    {
        _http = http;
    }

    public async Task<HelpArticle?> GetArticleForRouteAsync(string route, CancellationToken ct = default)
    {
        var pageKey = ResolvePageKey(route);
        if (pageKey is null)
            return null;

        return await GetArticleAsync(pageKey, ct).ConfigureAwait(false);
    }

    public string? ResolvePageKey(string route)
    {
        var path = route.Split(['?', '#'], 2)[0].Trim('/').ToLowerInvariant();

        if (string.IsNullOrEmpty(path))
            return "dashboard";

        var scopedKey = ResolveScopedPageKey(path);
        if (scopedKey is not null)
            return scopedKey;

        foreach (var mapping in PageMappings)
        {
            if (path == mapping.Route || path.StartsWith(mapping.Prefix, StringComparison.Ordinal))
                return mapping.Key;
        }

        return null;
    }

    private static string? ResolveScopedPageKey(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || !int.TryParse(segments[1], out _))
            return null;

        var mappings = segments[0] switch
        {
            "projects" => ProjectSectionMappings,
            "servers" => ServerSectionMappings,
            _ => null
        };

        return mappings?.GetValueOrDefault(segments[2]);
    }

    public async Task<HelpArticle?> GetArticleAsync(string pageKey, CancellationToken ct = default)
    {
        var articles = await GetAllArticlesAsync(ct).ConfigureAwait(false);
        return articles.FirstOrDefault(a => a.Key == pageKey);
    }

    public async Task<IReadOnlyList<HelpArticle>> GetAllArticlesAsync(CancellationToken ct = default)
    {
        var lang = GetLanguageKey();

        if (_cache.TryGetValue(lang, out var cached))
            return cached;

        var articles = await LoadArticlesAsync(lang, ct).ConfigureAwait(false);
        _cache[lang] = articles;
        return articles;
    }

    public async Task<IReadOnlyList<HelpArticle>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return await GetAllArticlesAsync(ct).ConfigureAwait(false);

        var articles = await GetAllArticlesAsync(ct).ConfigureAwait(false);
        return articles
            .Where(a => a.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || a.Description.Contains(query, StringComparison.OrdinalIgnoreCase)
                     || a.Sections.Any(s =>
                            s.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                         || s.Content.Contains(query, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    private async Task<IReadOnlyList<HelpArticle>> LoadArticlesAsync(string lang, CancellationToken ct)
    {
        try
        {
            var articles = await _http.GetFromJsonAsync<List<HelpArticle>>(
                $"help/help-{lang}.json", JsonOptions.Web, ct).ConfigureAwait(false);
            return articles ?? [];
        }
        catch (HttpRequestException)
        {
            return [];
        }
    }

    private static string GetLanguageKey()
    {
        var culture = CultureInfo.CurrentUICulture.Name;
        return culture.StartsWith("fr", StringComparison.OrdinalIgnoreCase) ? "fr" : "en";
    }

    private static readonly PageMapping[] PageMappings =
    [
        new("analysis", "analysis/", "analysis"),
        new("backups", "backups/", "backups"),
        new("service-connections", "service-connections/", "service-connections"),
        new("ai-tasks", "ai-tasks/", "ai-tasks"),
        new("git", "git/", "git-repositories"),
        new("servers", "servers/", "servers"),
        new("projects", "projects/", "projects"),
        new("artifacts", "artifacts/", "artifacts"),
        new("git-repositories", "git-repositories", "git-repositories"),
        new("environments", "environments/", "environments"),
        new("pipelines/runs", "pipelines/runs/", "pipelines"),
        new("pipelines", "pipelines/", "pipelines"),
        new("templates", "templates/", "templates"),
        new("variable-libraries", "variable-libraries/", "variable-libraries"),
        new("vaults", "vaults/", "vaults"),
        new("releases", "releases/", "releases"),
        new("tasks", "tasks/", "tasks"),
        new("logs", "logs/", "logs"),
        new("alerts", "alerts/", "alerts"),
        new("monitoring", "monitoring/", "supervision"),
        // Recette R1-41: the "Messagerie" page lists the mailboxes of the servers; the servers article explains them.
        new("mail", "mail/", "servers"),
        new("dashboards", "dashboards/", "dashboards"),
        new("plugins", "plugins/", "plugins"),
        new("admin/dashboards", "admin/dashboards", "dashboards"),
        new("admin/plugins", "admin/plugins", "plugins"),
        new("admin/organizations", "admin/organizations", "organizations"),
        new("admin/roles", "admin/roles", "roles"),
        new("admin/notifications", "admin/notifications", "notification-rules"),
        new("admin/ai-profiles", "admin/ai-profiles", "ai-runner-profiles"),
        new("admin/performance", "admin/performance", "performance"),
        new("admin/package-feeds", "admin/package-feeds", "package-feeds"),
        new("admin/package-registry", "admin/package-registry", "package-registry"),
        new("admin/system-logs", "admin/system-logs", "system-logs"),
        new("admin/users", "admin/users", "users"),
        new("admin/audit", "admin/audit", "audit"),
        new("admin/api-reference", "admin/api-reference", "api-reference"),
        new("admin/settings", "admin/settings", "platform-settings"),
        new("admin", "admin/", "administration"),
        new("users", "users/", "users"),
        new("audit", "audit/", "audit"),
        new("api-reference", "api-reference", "api-reference"),
        new("settings", "settings/", "settings"),
        // The notification preferences that decide what reaches /notifications live in the settings
        // article.
        new("notifications", "notifications/", "settings"),
    ];

    private static readonly IReadOnlyDictionary<string, string> ProjectSectionMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["pipelines"] = "pipelines",
            ["quality"] = "analysis",
            ["backups"] = "backups",
            ["servers"] = "servers",
            ["releases"] = "releases",
            ["artifacts"] = "artifacts",
            ["libraries"] = "variable-libraries",
            ["vaults"] = "vaults",
            ["environments"] = "environments",
            ["ai-tasks"] = "ai-tasks",
            ["tasks"] = "tasks",
            ["logs"] = "logs",
            ["external-repo"] = "git-repositories"
        };

    private static readonly IReadOnlyDictionary<string, string> ServerSectionMappings =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["projects"] = "projects",
            ["pipelines"] = "pipelines",
            ["libraries"] = "variable-libraries",
            ["vaults"] = "vaults",
            ["releases"] = "releases",
            ["tasks"] = "tasks",
            ["logs"] = "logs"
        };

    private sealed record PageMapping(string Route, string Prefix, string Key);
}

public sealed record HelpArticle(string Key, string Title, string Description, string Icon, IReadOnlyList<HelpSection> Sections);

public sealed record HelpSection(string Title, string Content);
