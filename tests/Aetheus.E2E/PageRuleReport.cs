// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;

namespace Aetheus.E2E;

/// <summary>Writes the human report of a sweep, in French, grouped by rule: it becomes the lines of the recette.</summary>
internal static class PageRuleReport
{
    private const int ExamplesPerRule = 6;

    private sealed record RuleInfo(string Key, int Number, string Label);

    private static readonly RuleInfo[] Rules =
    [
        new("1.primary-action-outside-header", 1, "un bouton principal est dans le corps de la page alors que l'en-tête n'en porte aucun"),
        new("1.header-button-row", 1, "l'en-tête de page porte une rangée de 2 boutons visibles ou plus"),
        new("1.header-action-not-aligned", 1, "une action de l'en-tête n'est pas alignée avec le titre (écart vertical de plus de 12 px)"),
        new("2.blocks-touching", 2, "deux blocs frères sont collés (0 px)"),
        new("2.block-gap-differs", 2, "l'écart entre deux blocs frères diffère de l'écart de la page"),
        new("3.grid-header-wrapped", 3, "un en-tête de colonne passe sur plusieurs lignes"),
        new("3.grid-header-truncated", 3, "un en-tête de colonne est coupé"),
        new("3.grid-cell-truncated", 3, "une valeur de cellule est coupée"),
        new(PageRuleSweeper.RowHeightRule, 3, "la hauteur de ligne d'une grille diffère entre 1280 et 375 px"),
        new("3.grid-height", 3, "la grille principale occupe moins de 75 % de la hauteur de l'écran"),
        new("3.page-scroll-with-grid", 3, "la page défile alors qu'une grille principale est présente"),
        new("4.text-wrapped-with-room", 4, "un texte passe à la ligne alors que sa ligne ou son bloc a la place de le tenir"),
        new("5.page-overflow", 5, "la page ou un conteneur est plus large que l'écran (défilement horizontal)"),
        new("5.outside-viewport", 5, "un élément visible sort de l'écran hors d'une zone défilante"),
        new("11.side-stripe", 11, "liseré coloré sur un côté (bordure, ombre interne ou barre)"),
        new(PageRuleSweeper.NeverSettlesRule, 0, "un chargeur reste affiché ou la page change de taille sans fin (la page n'est pas mesurée pour les autres règles)")
    ];

    private static readonly (int Number, string Title, string Reason)[] ManualRules =
    [
        (6, "tout en français, aucune expression technique brute, messages compréhensibles avec un « ? »",
            "la langue et la clarté d'un message sont un jugement ; une liste de mots anglais produirait des faux positifs (noms propres, identifiants) et laisserait passer les phrases mal tournées"),
        (7, "boutons : couleur selon l'importance, confirmations « verbe court » et « Revenir »",
            "l'importance d'une action est un jugement ; le garde STD-BTN couvre déjà la forme des boutons dans le code, et les confirmations n'existent que dans des fenêtres que le balayage n'ouvre pas"),
        (8, "champs obligatoires avec une étoile, aide par un « ? », valeur utile préremplie",
            "les formulaires sont surtout dans des fenêtres ou derrière une action ; le caractère obligatoire d'un champ et l'utilité d'une valeur par défaut ne se lisent pas dans le DOM"),
        (9, "notifications utiles seulement, bonne gravité",
            "une notification n'existe qu'après une action ou un événement ; le balayage ne fait que regarder"),
        (10, "action longue : voile sur la zone, pas de loader ajouté",
            "il faut déclencher une action longue pour la voir ; le balayage n'agit sur aucune donnée"),
        (12, "pas de doublon d'information dans un bloc",
            "un doublon est sémantique (même fait écrit deux fois avec des mots différents) ; aucun test fiable sans connaître le sens"),
        (13, "tout nom de librairie, paramètre, pipeline ou serveur affiché est un lien vers son endroit",
            "savoir qu'un texte est un nom d'entité demande le sens de la donnée ; un test sur les liens manquants serait un faux positif sur chaque libellé libre")
    ];

    public static string Build(PageRuleSweep sweep, IReadOnlyList<int> widths, PageRuleRatchet ratchet)
    {
        var builder = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;
        var violations = sweep.Violations.Where(violation => violation.Rule != PageRuleSweeper.UnreachableRule).ToList();
        var unreachable = sweep.Violations.Where(violation => violation.Rule == PageRuleSweeper.UnreachableRule)
            .OrderBy(violation => violation.Page, StringComparer.Ordinal).ToList();

        builder.AppendLine("# Contrôle des règles de présentation sur toutes les pages (R-30)").AppendLine();
        builder.AppendLine(culture, $"Mesure du {DateTime.Now:yyyy-MM-dd HH:mm}, durée {sweep.Elapsed:mm\\:ss}. Largeurs : {string.Join(" px et ", widths)} px.").AppendLine();

        builder.AppendLine("## Couverture").AppendLine();
        builder.AppendLine(culture, $"- Pages inventoriées (directives `@page` de `Aetheus.Front`) : {sweep.Targets.Count}");
        foreach (var width in widths)
        {
            var measured = sweep.Measurements.Count(measurement => measurement.Width == width && measurement.Measured);
            builder.AppendLine(culture, $"- Pages mesurées à {width} px : {measured}");
        }
        builder.AppendLine(culture, $"- Pages qui ne peuvent pas être mises à l'écran : {unreachable.Count} (détail plus bas)");
        var intermittent = sweep.Measurements.Where(measurement => measurement.Intermittent.Count > 0).ToList();
        builder.AppendLine(culture, $"- Constats intermittents écartés (vus par une seule des deux lectures d'une page) : {intermittent.Sum(measurement => measurement.Intermittent.Count)} sur {intermittent.Count} pages");
        builder.AppendLine(culture, $"- Constats retenus : {violations.Count} (dont {violations.Select(violation => violation.Page).Distinct().Count()} pages en défaut)");
        builder.AppendLine(culture, $"- Écart avec la référence enregistrée : {ratchet.New.Count} constats nouveaux, {ratchet.Stale.Count} lignes de la référence qui n'ont plus lieu");
        builder.AppendLine(culture, $"- Écart informatif (règle qui dépend des données affichées, ne fait pas échouer le test) : {ratchet.DataNew.Count} valeurs coupées absentes de la référence, {ratchet.DataGone.Count} lignes de la référence non revues");
        builder.AppendLine();
        AppendDataDependentDifferences(builder, ratchet);

        builder.AppendLine("## Synthèse par règle").AppendLine();
        foreach (var group in Rules.GroupBy(rule => rule.Number))
        {
            var keys = group.Select(rule => rule.Key).ToHashSet(StringComparer.Ordinal);
            var inGroup = violations.Where(violation => keys.Contains(violation.Rule)).ToList();
            builder.AppendLine(culture, $"- {Heading(group.Key)} : {Count(inGroup.Count, "constat")} sur {Count(inGroup.Select(violation => violation.Page).Distinct().Count(), "page")}");
        }
        builder.AppendLine();

        builder.AppendLine("## Détail par règle").AppendLine();
        foreach (var number in Rules.Select(rule => rule.Number).Distinct())
        {
            builder.AppendLine(culture, $"### {Heading(number)}").AppendLine();
            foreach (var rule in Rules.Where(rule => rule.Number == number))
                AppendRule(builder, rule, violations.Where(violation => violation.Rule == rule.Key).ToList());
        }

        builder.AppendLine("## Pages qui ne peuvent pas être mises à l'écran").AppendLine();
        builder.AppendLine("Les raisons sont écrites en anglais, telles qu'elles figurent dans la référence enregistrée.").AppendLine();
        foreach (var violation in unreachable)
            builder.AppendLine(culture, $"- `{violation.Page}` : {violation.Element}");
        builder.AppendLine();

        AppendManualRules(builder);
        AppendLimits(builder);
        return builder.ToString();
    }

    private static string Heading(int number) => number == 0 ? "Pages dont le chargement ne se termine pas" : $"Règle {number}";

    private static string Count(int count, string noun) => $"{count} {noun}{(count > 1 ? "s" : string.Empty)}";

    private static void AppendRule(StringBuilder builder, RuleInfo rule, IReadOnlyList<PageRuleViolation> found)
    {
        var culture = CultureInfo.InvariantCulture;
        builder.AppendLine(culture, $"#### `{rule.Key}` : {rule.Label}").AppendLine();
        if (found.Count == 0)
        {
            builder.AppendLine("Aucun constat.").AppendLine();
            return;
        }

        var pages = found.GroupBy(violation => violation.Page, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToList();
        builder.AppendLine(culture, $"{Count(found.Count, "constat")} sur {Count(pages.Count, "page")} (largeur entre parenthèses) :").AppendLine();
        foreach (var page in pages)
        {
            var widths = string.Join(", ", page.Select(violation => violation.Width).Distinct().Order());
            builder.AppendLine(culture, $"- `{page.Key}` ({widths}) : {Count(page.Select(violation => violation.Element).Distinct(StringComparer.Ordinal).Count(), "élément")}");
        }
        builder.AppendLine().AppendLine("Exemples :").AppendLine();
        foreach (var example in found.OrderBy(violation => violation.Page, StringComparer.Ordinal).ThenBy(violation => violation.Width).Take(ExamplesPerRule))
            builder.AppendLine(culture, $"- `{example.Page}` à {example.Width} px, `{example.Element}` : {example.Detail}");
        builder.AppendLine();
    }

    private static void AppendDataDependentDifferences(StringBuilder builder, PageRuleRatchet ratchet)
    {
        if (ratchet.DataNew.Count == 0 && ratchet.DataGone.Count == 0) return;
        var culture = CultureInfo.InvariantCulture;
        builder.AppendLine("## Valeurs coupées qui diffèrent de la référence (règle 3, dépend des données)").AppendLine();
        builder.AppendLine("Une cellule n'est coupée que si une valeur est assez longue : les lignes ajoutées par les autres tests de la base E2E (journal d'audit, coffres) la font apparaître. Ces écarts sont listés ici et ne font pas échouer le test.").AppendLine();
        if (ratchet.DataNew.Count > 0)
        {
            builder.AppendLine("Valeurs coupées qui ne sont pas dans la référence :").AppendLine();
            foreach (var key in ratchet.DataNew) builder.AppendLine(culture, $"- `{key}`");
            builder.AppendLine();
        }
        if (ratchet.DataGone.Count > 0)
        {
            builder.AppendLine("Lignes de la référence qui n'ont pas été revues (données plus courtes dans cette base) :").AppendLine();
            foreach (var key in ratchet.DataGone) builder.AppendLine(culture, $"- `{key}`");
            builder.AppendLine();
        }
    }

    private static void AppendManualRules(StringBuilder builder)
    {
        builder.AppendLine("## Règles qui ne se mesurent pas automatiquement (à contrôler à la main)").AppendLine();
        foreach (var (number, title, reason) in ManualRules)
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Règle {number}, {title} : {reason}.");
        builder.AppendLine();
    }

    private static void AppendLimits(StringBuilder builder)
    {
        builder.AppendLine("## Limites de la mesure").AppendLine();
        builder.AppendLine("- Les mesures dépendent de la police et du moteur de rendu : la référence a été établie sur la machine de développement (Windows) ; le conteneur Linux de la recette peut mesurer des largeurs de texte légèrement différentes.");
        builder.AppendLine("- Règle 4 : le test compare la largeur nécessaire au texte sur une ligne avec la place du bloc ou de la rangée ; il ne voit pas un texte qui passe à la ligne parce qu'un voisin de la rangée est large, ni un texte dans une zone de code ou de grille.");
        builder.AppendLine("- Règle 2 : seuls les écarts verticaux entre blocs frères sont mesurés ; les écarts horizontaux ne le sont pas.");
        builder.AppendLine("- Règle 5 : les fenêtres (dialogues) ne sont pas ouvertes par le balayage ; seules les pages le sont, dans leur onglet par défaut.");
        builder.AppendLine("- Règle 3 : une grille est « principale » quand elle occupe au moins la moitié de la hauteur du contenu de la page.");
        builder.AppendLine("- Les données sont celles de la base E2E, telle que les autres tests la laissent : une valeur plus longue en production peut couper une cellule que le balayage ne voit pas couper.");
        builder.AppendLine("- La règle `3.grid-cell-truncated` dépend de ces données : ses écarts avec la référence sont rapportés mais ne font pas échouer le test (sur toute la suite E2E, 3 valeurs coupées de plus apparaissent dans le journal d'audit et dans les coffres). Les autres règles ont donné les mêmes constats sur la base initiale et sur la suite complète.");
        builder.AppendLine();
    }
}
