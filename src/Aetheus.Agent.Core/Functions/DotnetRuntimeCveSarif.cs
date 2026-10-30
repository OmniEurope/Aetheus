// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Functions;

/// <summary>One CVE that an image's .NET runtime is exposed to: the image, the runtime packs that are older than the
/// fix, and what Microsoft says about the CVE.</summary>
internal sealed record DotnetRuntimeCveFinding(string Image, IReadOnlyList<DotnetRuntimePack> Packs, DotnetRuntimeAdvisory Advisory);

/// <summary>The SARIF the Security stage publishes for the .NET runtime CVEs, ingested like any scanner report: a finding
/// per CVE and image, High because Microsoft's release metadata carries no severity and a security release of the runtime
/// is treated as one that matters (the image scanners gate on HIGH and CRITICAL too).
///
/// The identity of a finding is the CVE and the image, never the version: the finding stays the same finding while the
/// image keeps an older runtime and is fixed when the runtime moves to the release that fixes it.
///
/// Built with <see cref="JsonNode"/>, not by serializing objects: <c>aetheus-fn</c>, which runs this function inside a
/// step container, is trimmed and has reflection-based serialization disabled.</summary>
internal static class DotnetRuntimeCveSarif
{
    internal const string ToolName = "Aetheus .NET runtime CVE check";
    internal const string Severity = "High";

    public static string Build(IReadOnlyList<DotnetRuntimeCveFinding> findings)
    {
        var rules = new JsonArray();
        foreach (var advisory in findings.Select(finding => finding.Advisory)
                     .DistinctBy(advisory => advisory.CveId, StringComparer.Ordinal)
                     .OrderBy(advisory => advisory.CveId, StringComparer.Ordinal))
            rules.Add(Rule(advisory));
        var results = new JsonArray();
        foreach (var finding in findings
                     .OrderBy(finding => finding.Advisory.CveId, StringComparer.Ordinal)
                     .ThenBy(finding => finding.Image, StringComparer.Ordinal))
            results.Add(Result(finding));
        var driver = new JsonObject { ["name"] = ToolName, ["version"] = "1", ["rules"] = rules };
        var run = new JsonObject { ["tool"] = new JsonObject { ["driver"] = driver }, ["results"] = results };
        return new JsonObject { ["version"] = "2.1.0", ["runs"] = new JsonArray(run) }.ToJsonString();
    }

    private static JsonObject Rule(DotnetRuntimeAdvisory advisory)
    {
        var rule = new JsonObject
        {
            ["id"] = advisory.CveId,
            ["shortDescription"] = Text($"{advisory.CveId}, fixed in .NET {advisory.FixedIn}"),
            ["fullDescription"] = Text(
                $"Microsoft lists {advisory.CveId} among the CVEs fixed by .NET {advisory.FixedIn}"
                + (advisory.FixedOn is null ? string.Empty : $" (released {advisory.FixedOn})")
                + ". Severity is assumed High: the release metadata gives none.")
        };
        if (advisory.CveUrl is not null)
            rule["helpUri"] = advisory.CveUrl;
        return rule;
    }

    private static JsonObject Result(DotnetRuntimeCveFinding finding)
    {
        var advisory = finding.Advisory;
        var packs = finding.Packs.OrderBy(pack => pack.Package, StringComparer.Ordinal).ToList();
        var installed = string.Join(", ", packs.Select(pack => $"{pack.Package} {pack.Version}"));
        var fixedOn = advisory.FixedOn is null ? string.Empty : $" (released {advisory.FixedOn})";
        var location = new JsonObject
        {
            ["physicalLocation"] = new JsonObject
            {
                ["artifactLocation"] = new JsonObject { ["uri"] = $"{finding.Image}/{packs[0].DepsPath}" }
            },
            ["logicalLocations"] = new JsonArray(new JsonObject { ["fullyQualifiedName"] = finding.Image })
        };
        return new JsonObject
        {
            ["ruleId"] = advisory.CveId,
            ["level"] = "error",
            ["message"] = Text(
                $"{finding.Image} ships a .NET runtime older than {advisory.FixedIn}, the release that fixes {advisory.CveId}{fixedOn}: "
                + $"installed {installed}, fixed in {advisory.FixedIn}. The latest security release of the channel is {advisory.LatestSecurity}. "
                + "Source: Microsoft .NET release metadata; its severity is not published, High is assumed."),
            ["properties"] = new JsonObject { ["severity"] = Severity, ["confidence"] = "High" },
            ["partialFingerprints"] = new JsonObject { ["aetheus/identity"] = $"dotnet-runtime-cve|{advisory.CveId}|{finding.Image}" },
            ["locations"] = new JsonArray(location)
        };
    }

    private static JsonObject Text(string text) => new() { ["text"] = text };
}
