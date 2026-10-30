// SPDX-License-Identifier: EUPL-1.2
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using HelpArticlePage = Aetheus.Front.Components.Help.HelpArticle;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>
/// Guards contextual route coverage, bilingual catalog parity, article substance, and the major
/// feature contracts that the help center promises to explain.
/// </summary>
public class HelpArticleCoverageTests
{
    [Fact]
    public void Every_PageMapping_Key_Has_An_Article_In_Both_Languages()
    {
        var mappingKeys = ReadPageMappingKeys();
        Assert.NotEmpty(mappingKeys);

        var (en, fr) = ReadCatalogs();
        var enKeys = en.Keys.ToHashSet(StringComparer.Ordinal);
        var frKeys = fr.Keys.ToHashSet(StringComparer.Ordinal);
        var missingEn = mappingKeys.Where(key => !enKeys.Contains(key)).ToList();
        var missingFr = mappingKeys.Where(key => !frKeys.Contains(key)).ToList();

        Assert.True(missingEn.Count == 0 && missingFr.Count == 0,
            "Every HelpService mapping target must have an article in both catalogs:\n"
            + $"  Missing in help-en.json: {FormatKeys(missingEn)}\n"
            + $"  Missing in help-fr.json: {FormatKeys(missingFr)}");
    }

    [Fact]
    public void Every_Relevant_Razor_Route_Resolves_To_A_Bilingual_Help_Article()
    {
        var help = new HelpService(new HttpClient { BaseAddress = new Uri("http://localhost/") });
        var (en, fr) = ReadCatalogs();
        var failures = new List<string>();

        foreach (var (pages, file) in RepositoryScan.EnumerateUnion(RepositoryScan.PageRoots, "*.razor"))
        {
            var relative = Path.GetRelativePath(pages, file).Replace('\\', '/');
            if (IsContextualHelpExempt(relative))
                continue;

            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, "@page\\s+\"([^\"]+)\""))
            {
                var template = match.Groups[1].Value;
                var route = MaterializeRoute(template);
                var key = help.ResolvePageKey(route);
                if (key is null)
                {
                    failures.Add($"{relative}: {template} resolves to no help key");
                    continue;
                }

                if (!en.ContainsKey(key) || !fr.ContainsKey(key))
                    failures.Add($"{relative}: {template} resolves to missing bilingual article '{key}'");
            }
        }

        Assert.Empty(failures);
    }

    [Fact]
    public void Help_Catalogs_Are_Parallel_Unique_And_Substantive()
    {
        var enArticles = ReadArticles(CatalogPath("help-en.json"));
        var frArticles = ReadArticles(CatalogPath("help-fr.json"));
        var failures = new List<string>();

        ValidateCatalog("EN", enArticles, failures);
        ValidateCatalog("FR", frArticles, failures);

        var enKeys = enArticles.Select(article => article.Key).ToHashSet(StringComparer.Ordinal);
        var frKeys = frArticles.Select(article => article.Key).ToHashSet(StringComparer.Ordinal);
        if (!enKeys.SetEquals(frKeys))
        {
            failures.Add($"Missing in FR: {FormatKeys(enKeys.Except(frKeys).ToList())}");
            failures.Add($"Missing in EN: {FormatKeys(frKeys.Except(enKeys).ToList())}");
        }

        Assert.True(enArticles.Count >= 33, "The complete help inventory unexpectedly shrank.");
        Assert.Empty(failures);
    }

    [Fact]
    public void Help_Articles_Have_The_Same_Number_Of_Sections_In_Both_Languages()
    {
        var (en, fr) = ReadCatalogs();
        var mismatches = en
            .Where(pair => fr.TryGetValue(pair.Key, out var french) && french.Sections.Count != pair.Value.Sections.Count)
            .Select(pair => $"{pair.Key}: EN {pair.Value.Sections.Count}, FR {fr[pair.Key].Sections.Count}")
            .ToList();

        Assert.True(mismatches.Count == 0,
            "Each help article must carry the same sections in both languages:\n  " + string.Join("\n  ", mismatches));
    }

    [Fact]
    public void Every_Article_Has_A_Canonical_Go_To_Page_Route()
    {
        var field = typeof(HelpArticlePage).GetField(
            "PageRoutes", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var routes = (Dictionary<string, string>)field!.GetValue(null)!;
        var (en, _) = ReadCatalogs();
        var staticRoutes = RepositoryScan.EnumerateUnion(RepositoryScan.PageRoots, "*.razor")
            .Select(entry => entry.File)
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), "@page\\s+\"([^\"]+)\"")
                .Select(match => match.Groups[1].Value))
            .Where(route => !route.Contains('{', StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        Assert.Equal(en.Keys.Order(), routes.Keys.Order());
        Assert.All(routes.Values, route => Assert.Contains(route, staticRoutes));
    }

    [Fact]
    public void Major_Feature_Contracts_Are_Explained_In_Both_Languages()
    {
        var (en, fr) = ReadCatalogs();
        var failures = new List<string>();
        ValidateFeatureContracts("EN", en, EnglishFeatureContracts, failures);
        ValidateFeatureContracts("FR", fr, FrenchFeatureContracts, failures);
        Assert.Empty(failures);
    }

    private static bool IsContextualHelpExempt(string relativePath) =>
        relativePath.StartsWith("Auth/", StringComparison.Ordinal)
        || relativePath is "Help/HelpCenter.razor" or "Help/HelpArticle.razor" or "Shared/NotFound.razor";

    private static string MaterializeRoute(string template) =>
        Regex.Replace(template, "\\{([^}:]+)(?::[^}]+)?\\}", match =>
            match.Groups[1].Value.Equals("Sha", StringComparison.OrdinalIgnoreCase)
                ? "abc123"
                : match.Groups[1].Value.Equals("Tab", StringComparison.OrdinalIgnoreCase)
                    ? "profile"
                    : "1");

    private static void ValidateCatalog(
        string language,
        IReadOnlyList<CatalogArticle> articles,
        ICollection<string> failures)
    {
        foreach (var duplicate in articles.GroupBy(article => article.Key).Where(group => group.Count() > 1))
            failures.Add($"{language}: duplicate key {duplicate.Key}");

        foreach (var article in articles)
        {
            if (string.IsNullOrWhiteSpace(article.Key)
                || string.IsNullOrWhiteSpace(article.Title)
                || string.IsNullOrWhiteSpace(article.Icon))
                failures.Add($"{language}: {article.Key} has missing metadata");
            if (article.Description.Length < 40)
                failures.Add($"{language}: {article.Key} description is too short");
            if (article.Sections.Count < 3)
                failures.Add($"{language}: {article.Key} has fewer than three sections");
            foreach (var section in article.Sections)
            {
                if (section.Title.Length < 3 || section.Content.Length < 80)
                    failures.Add($"{language}: {article.Key}/{section.Title} is not substantive");
            }
        }
    }

    private static void ValidateFeatureContracts(
        string language,
        IReadOnlyDictionary<string, CatalogArticle> articles,
        IReadOnlyDictionary<string, string[]> contracts,
        ICollection<string> failures)
    {
        foreach (var (key, requiredTerms) in contracts)
        {
            if (!articles.TryGetValue(key, out var article))
            {
                failures.Add($"{language}: missing contracted article {key}");
                continue;
            }

            var text = string.Join('\n',
                [article.Title, article.Description, .. article.Sections.SelectMany(section => new[] { section.Title, section.Content })]);
            foreach (var term in requiredTerms.Where(
                         term => !text.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                failures.Add($"{language}: {key} does not explain '{term}'");
            }
        }
    }

    private static IReadOnlyCollection<string> ReadPageMappingKeys()
    {
        var field = typeof(HelpService).GetField("PageMappings", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(field);
        var mappings = (System.Collections.IEnumerable)field!.GetValue(null)!;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        PropertyInfo? keyProperty = null;
        foreach (var mapping in mappings)
        {
            keyProperty ??= mapping.GetType().GetProperty("Key");
            Assert.NotNull(keyProperty);
            keys.Add((string)keyProperty!.GetValue(mapping)!);
        }
        return keys;
    }

    private static (IReadOnlyDictionary<string, CatalogArticle> En, IReadOnlyDictionary<string, CatalogArticle> Fr)
        ReadCatalogs() =>
        (ReadArticles(CatalogPath("help-en.json")).ToDictionary(article => article.Key, StringComparer.Ordinal),
         ReadArticles(CatalogPath("help-fr.json")).ToDictionary(article => article.Key, StringComparer.Ordinal));

    private static IReadOnlyList<CatalogArticle> ReadArticles(string path)
    {
        Assert.True(File.Exists(path), $"Help file not found: {path}");
        return JsonSerializer.Deserialize<List<CatalogArticle>>(
            File.ReadAllText(path), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
    }

    private static string CatalogPath(string fileName) =>
        Path.Combine(FindRepoRoot(), "src", "Aetheus.Front", "wwwroot", "help", fileName);

    private static string FormatKeys(IReadOnlyCollection<string> keys) =>
        keys.Count == 0 ? "(none)" : string.Join(", ", keys);

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;

    private sealed record CatalogArticle(
        string Key,
        string Title,
        string Description,
        string Icon,
        IReadOnlyList<CatalogSection> Sections);

    private sealed record CatalogSection(string Title, string Content);

    private static readonly IReadOnlyDictionary<string, string[]> EnglishFeatureContracts =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["dashboard"] = ["Monitored applications", "real time", "custom dashboard"],
            ["servers"] = ["heartbeat", "compatibility", "Docker", "Cron", "permissions"],
            ["projects"] = ["board", "Monitoring", "Backups", "External repository"],
            ["git-repositories"] = ["pull requests", "force-push", "Blame", "real time"],
            ["environments"] = ["approval", "DAST", "allowed hosts"],
            ["pipelines"] = ["dry run", "webhook", "rerun", "coverage", "real time"],
            ["templates"] = ["changelog", "Extract", "orphaned overrides"],
            ["variable-libraries"] = ["Scope", "Import", "export", "history"],
            ["vaults"] = ["encrypted", "expiration", "Rotation", "values"],
            ["releases"] = ["Provenance", "deliverables", "rollback"],
            ["artifacts"] = ["checksum", "Provenance", "Retention"],
            ["tasks"] = ["assigned", "not automatically redistributed", "real-time"],
            ["logs"] = ["numeric identifier", "masked", "does not offer global"],
            ["alerts"] = ["sustained", "Clear recent alerts", "Notification rules"],
            ["analysis"] = ["SBOM", "Decisions", "AI prompt", "Pipeline gates"],
            ["backups"] = ["cron", "Restore check", "retention"],
            ["service-connections"] = ["credentials", "Test connection", "permissions"],
            ["ai-tasks"] = ["External data", "scheduled", "Results"],
            ["dashboards"] = ["Widgets", "Preview", "default"],
            ["organizations"] = ["Owner", "Quality gates", "Deletion"],
            ["roles"] = ["Permission matrix", "Bulk", "Audit"],
            ["users"] = ["Must change password", "TOTP", "Effective permissions"],
            ["notification-rules"] = ["Channels", "event type", "Troubleshooting"],
            ["ai-runner-profiles"] = ["binary", "timeout", "External data"],
            ["package-feeds"] = ["upstream", "rate limits", "Synchronization"],
            ["package-registry"] = ["NuGet", ".npmrc", "Versions"],
            ["plugins"] = ["entry point", "dependencies", "unregister"],
            ["audit"] = ["Verify chain", "cryptographic", "Details"],
            ["system-logs"] = ["correlation", "Markdown", "purge"],
            ["api-reference"] = ["OpenAPI", "authentication", "Search"],
            ["settings"] = ["Personal access tokens", "TOTP", "recovery codes", "Notification preferences"],
            ["platform-settings"] = ["Registration tokens", "Secrets", "Quality gates"],
            ["administration"] = ["Identity and access", "Integrations", "API"]
        };

    private static readonly IReadOnlyDictionary<string, string[]> FrenchFeatureContracts =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["dashboard"] = ["Applications supervisées", "temps réel", "tableau personnalisé"],
            ["servers"] = ["heartbeat", "compatibilité", "Docker", "Cron", "permissions"],
            ["projects"] = ["tableau", "Monitoring", "Sauvegardes", "Dépôt externe"],
            ["git-repositories"] = ["pull requests", "force-push", "Blame", "temps réel"],
            ["environments"] = ["Approbation", "DAST", "hôtes autorisés"],
            ["pipelines"] = ["dry-run", "webhook", "relancée", "couverture", "temps réel"],
            ["templates"] = ["changelog", "extraction", "overrides devenus orphelins"],
            ["variable-libraries"] = ["Portée", "Import", "export", "historique"],
            ["vaults"] = ["chiffrées", "expiration", "rotation", "valeurs"],
            ["releases"] = ["Provenance", "livrables", "rollback"],
            ["artifacts"] = ["checksum", "Provenance", "rétention"],
            ["tasks"] = ["assignée", "n’est pas redistribuée automatiquement", "temps réel"],
            ["logs"] = ["identifiant numérique", "masqué", "ne fournit pas de filtres globaux"],
            ["alerts"] = ["durée soutenue", "Effacer les alertes récentes", "Règles de notification"],
            ["analysis"] = ["SBOM", "Décisions", "prompt IA", "Gates de pipeline"],
            ["backups"] = ["cron", "Contrôle de restauration", "rétention"],
            ["service-connections"] = ["identifiants", "Tester la connexion", "permissions"],
            ["ai-tasks"] = ["Données externes", "planifiée", "Résultats"],
            ["dashboards"] = ["Widgets", "aperçu", "défaut"],
            ["organizations"] = ["Owner", "Quality gates", "Suppression"],
            ["roles"] = ["Matrice de permissions", "masse", "Audit"],
            ["users"] = ["Doit changer le mot de passe", "TOTP", "Permissions effectives"],
            ["notification-rules"] = ["Canaux", "type d’événement", "Dépannage"],
            ["ai-runner-profiles"] = ["binaire", "timeout", "Données externes"],
            ["package-feeds"] = ["amont", "limitation de débit", "Synchronisation"],
            ["package-registry"] = ["NuGet", ".npmrc", "Versions"],
            ["plugins"] = ["point d’entrée", "dépendances", "Désenregistrer"],
            ["audit"] = ["Vérifier la chaîne", "cryptographiques", "Détails"],
            ["system-logs"] = ["corrélation", "Markdown", "purge"],
            ["api-reference"] = ["OpenAPI", "authentification", "recherche"],
            ["settings"] = ["Jetons d’accès personnels", "TOTP", "codes de récupération", "Préférences de notification"],
            ["platform-settings"] = ["Jetons d’enregistrement", "Secrets", "Quality gates"],
            ["administration"] = ["Identités et accès", "Intégrations", "API"]
        };
}
