// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Aetheus.E2E;

/// <summary>One axe-core violation on one page, independent of the axe library types.</summary>
public sealed record AccessibilityViolation(
    string Page, string RuleId, string? Impact, string Description, string Help, string? HelpUrl,
    IReadOnlyList<string> Targets);

/// <summary>Export every axe-core violation as SARIF 2.1 with a stable page, rule and element
/// fingerprint, including impacts that do not fail the accessibility test. Backend tests parse this
/// exporter output through the real SARIF parser.</summary>
public static class AccessibilitySarif
{
    public const string FileName = "accessibility.sarif";

    public static string Build(IEnumerable<AccessibilityViolation> violations)
    {
        var list = violations.ToList();
        var rules = new JsonArray();
        foreach (var rule in list.GroupBy(violation => violation.RuleId, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var first = rule.First();
            var descriptor = new JsonObject
            {
                ["id"] = rule.Key,
                ["shortDescription"] = new JsonObject { ["text"] = first.Help },
                ["fullDescription"] = new JsonObject { ["text"] = first.Description }
            };
            if (!string.IsNullOrWhiteSpace(first.HelpUrl)) descriptor["helpUri"] = first.HelpUrl;
            rules.Add(descriptor);
        }

        var results = new JsonArray();
        foreach (var violation in list)
        {
            // An element is what a person fixes: one result per page, rule and target.
            var targets = violation.Targets.Count > 0 ? violation.Targets : [string.Empty];
            foreach (var target in targets)
            {
                results.Add(new JsonObject
                {
                    ["ruleId"] = violation.RuleId,
                    ["level"] = Level(violation.Impact),
                    ["message"] = new JsonObject
                    {
                        ["text"] = target.Length == 0
                            ? $"[{violation.Impact ?? "unknown"}] {violation.Help} on {violation.Page}"
                            : $"[{violation.Impact ?? "unknown"}] {violation.Help} on {violation.Page} ({target})"
                    },
                    ["locations"] = new JsonArray(new JsonObject
                    {
                        ["physicalLocation"] = new JsonObject
                        {
                            ["artifactLocation"] = new JsonObject { ["uri"] = PageUri(violation.Page) }
                        },
                        ["logicalLocations"] = new JsonArray(new JsonObject { ["name"] = target })
                    }),
                    ["partialFingerprints"] = new JsonObject
                    {
                        ["aetheusAccessibility/v1"] = $"{violation.Page}|{violation.RuleId}|{target}"
                    },
                    ["properties"] = new JsonObject { ["impact"] = violation.Impact, ["severity"] = Severity(violation.Impact) }
                });
            }
        }

        var sarif = new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = "axe-core",
                        ["informationUri"] = "https://github.com/dequelabs/axe-core",
                        ["rules"] = rules
                    }
                },
                ["results"] = results
            })
        };
        return sarif.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>axe impacts mapped to SARIF levels: critical and serious fail the suite, so they are errors.</summary>
    public static string Level(string? impact) => impact?.ToLowerInvariant() switch
    {
        "critical" or "serious" => "error",
        "moderate" => "warning",
        _ => "note"
    };

    /// <summary>The platform severity, so a critical violation is not flattened into the same High as a serious one.</summary>
    public static string Severity(string? impact) => impact?.ToLowerInvariant() switch
    {
        "critical" => "Critical",
        "serious" => "High",
        "moderate" => "Medium",
        _ => "Low"
    };

    private static string PageUri(string page) => "app" + (page.StartsWith('/') ? page : "/" + page);
}
