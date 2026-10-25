// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>merge-roslyn-security-sarif</c> function, port of
/// the former <c>merge-roslyn-security-sarif.mjs</c> script: merges the per-project Roslyn SARIF reports of
/// a build (<c>-p:AetheusRoslynSarif=true</c>) into the one report the pipeline publishes.
///
/// It keeps ONLY the security rules: a solution-wide build emits well over a thousand analyzer results,
/// almost all style and performance advice, and publishing those buries the ones that concern security.
/// The families kept are the .NET security category (CA21xx injection and P/Invoke marshaling, CA23xx
/// unsafe deserialization, CA3xxx XML/XSS/SQL injection, CA5xxx weak crypto, certificate validation,
/// TLS) plus the repository's own SEC analyzers. It drops the results already suppressed in source, so
/// the artifact lists what is still open.
///
/// No report at all is a failure, not an empty report: it means the build did not run with the SARIF
/// flag, and an empty log would assert "no security findings" about an analysis nobody performed.
///
/// Differences, deliberate: the reports are read in ordinal file-name order (the script took the
/// directory's order), and a <c>runs</c> or <c>results</c> value that is not an array fails the step
/// where the script threw or iterated it as it came.
/// </summary>
public sealed partial class MergeRoslynSecuritySarifFunction : IPipelineFunction
{
    internal const string ToolName = "Roslyn security analyzers";
    internal const string InformationUri = "https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/security-warnings";

    [GeneratedRegex("^(SEC[0-9]+|CA21[0-9]{2}|CA23[0-9]{2}|CA3[0-9]{3}|CA5[0-9]{3})\\z")]
    private static partial Regex SecurityRule();

    public string Name => PipelineFunctionCatalog.MergeRoslynSecuritySarif.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var input = Path.Combine(context.Workspace, context.Parameters["input"]);
        var output = Path.Combine(context.Workspace, context.Parameters["output"]);
        if (!Directory.Exists(input))
            return PipelineFunctionResult.Failure($"The SARIF input directory {input} does not exist.");

        var files = Directory.GetFiles(input, "*.sarif", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToList();
        if (files.Count == 0)
            return PipelineFunctionResult.Failure($"No SARIF report found in {input}; the build did not produce any.");

        var reports = new List<JsonNode?>();
        foreach (var file in files)
            reports.Add(JsonNode.Parse(await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false)));

        JsonObject merged;
        try
        {
            merged = Merge(reports);
        }
        catch (InvalidDataException ex)
        {
            return PipelineFunctionResult.Failure(ex.Message);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        await File.WriteAllTextAsync(output, ScriptJson.Compact(merged) + "\n", cancellationToken).ConfigureAwait(false);
        var count = merged["runs"]![0]!["results"]!.AsArray().Count;
        await context.Log($"{count} open security findings from {files.Count} projects").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>A rule id belongs to the security surface. A whitelist of families rather than a tag
    /// lookup: Roslyn SARIF carries no category on the rule, so the id is the only signal.</summary>
    internal static bool IsSecurityRule(JsonNode? ruleId) =>
        ruleId is JsonValue value && value.TryGetValue<string>(out var id) && SecurityRule().IsMatch(id);

    /// <summary>SARIF 2.1.0 §3.27.23. An EMPTY suppressions array is the tool saying it found none, the
    /// opposite of a suppression; only an <c>accepted</c> or absent state counts.</summary>
    internal static bool IsSuppressed(JsonNode? result) =>
        result?["suppressions"] is JsonArray { Count: > 0 } suppressions
        && suppressions.Any(IsAcceptedSuppression);

    /// <summary>The script's <c>state === undefined || String(state).toLowerCase() === "accepted"</c>: a
    /// suppression that is not an object, or has no state, counts; a JSON <c>null</c> state does not.</summary>
    private static bool IsAcceptedSuppression(JsonNode? suppression)
    {
        if (suppression is not JsonObject entry || !entry.TryGetPropertyValue("state", out var state))
            return true;
        return state is JsonValue value && value.TryGetValue<string>(out var text)
            && string.Equals(text, "accepted", StringComparison.OrdinalIgnoreCase);
    }

    internal static JsonObject Merge(IEnumerable<JsonNode?> reports)
    {
        var results = new JsonArray();
        var rules = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var run in reports.SelectMany(report => ArrayOf(report?["runs"], "runs")))
            MergeRun(run, results, rules);

        return new JsonObject
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new JsonArray(new JsonObject
            {
                ["tool"] = new JsonObject
                {
                    ["driver"] = new JsonObject
                    {
                        ["name"] = ToolName,
                        ["informationUri"] = InformationUri,
                        ["rules"] = new JsonArray([.. rules.Values.Select(rule => rule.DeepClone())])
                    }
                },
                ["results"] = results
            })
        };
    }

    private static void MergeRun(JsonNode? run, JsonArray results, Dictionary<string, JsonNode> rules)
    {
        var declared = run?["tool"]?["driver"]?["rules"] as JsonArray;
        foreach (var result in ArrayOf(run?["results"], "results"))
        {
            if (result is not JsonObject entry || !IsSecurityRule(entry["ruleId"]) || IsSuppressed(entry))
                continue;
            // ruleIndex points into THIS run's rule table, meaningless once merged: the rule itself is
            // carried across and the index dropped rather than left pointing elsewhere.
            if (DeclaredRule(declared, entry["ruleIndex"]) is { } rule && RuleId(rule) is { } id)
                rules.TryAdd(id, rule);
            var copy = entry.DeepClone().AsObject();
            copy.Remove("ruleIndex");
            results.Add(copy);
        }
    }

    private static JsonNode? DeclaredRule(JsonArray? declared, JsonNode? index) =>
        declared is not null && index is JsonValue value && value.TryGetValue<int>(out var position)
        && position >= 0 && position < declared.Count
            ? declared[position]
            : null;

    /// <summary>The script's truthy <c>rule?.id</c>, as the key it deduplicates on.</summary>
    private static string? RuleId(JsonNode rule) =>
        rule["id"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;

    /// <summary><c>value ?? []</c>: absent or null is empty, an array is iterated, anything else is not a
    /// SARIF log.</summary>
    private static IEnumerable<JsonNode?> ArrayOf(JsonNode? value, string name) => value switch
    {
        null => [],
        JsonArray array => array,
        _ => throw new InvalidDataException($"A SARIF report's {name} is not an array.")
    };
}
