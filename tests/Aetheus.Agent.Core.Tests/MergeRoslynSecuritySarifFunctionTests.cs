// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/merge-roslyn-security-sarif.mjs</c>, with the cases of its
/// <c>merge-roslyn-security-sarif.test.mjs</c>: only the security families, only open findings, rules
/// carried across without their stale index, and no report at all refused rather than published empty.
/// </summary>
public sealed class MergeRoslynSecuritySarifFunctionTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-roslyn-sarif");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public void Dispose() => _harness.Dispose();

    private static JsonObject Result(string ruleId, JsonObject? extra = null)
    {
        var result = new JsonObject { ["ruleId"] = ruleId, ["message"] = new JsonObject { ["text"] = $"{ruleId} finding" } };
        foreach (var (key, value) in extra ?? [])
            result[key] = value?.DeepClone();
        return result;
    }

    private static JsonObject Report(JsonArray results, JsonArray? rules = null) => new()
    {
        ["version"] = "2.1.0",
        ["runs"] = new JsonArray(new JsonObject
        {
            ["tool"] = new JsonObject { ["driver"] = new JsonObject { ["name"] = "x", ["rules"] = rules ?? [] } },
            ["results"] = results
        })
    };

    private static JsonArray Results(JsonObject merged) => merged["runs"]![0]!["results"]!.AsArray();

    private static string[] RuleIds(JsonObject merged) =>
        [.. Results(merged).Select(result => (string)result!["ruleId"]!)];

    [Fact]
    public void SecurityFamilies_AreKeptAndNothingElse()
    {
        foreach (var id in new[] { "CA2100", "CA2101", "CA2300", "CA3001", "CA5359", "CA5392", "SEC004", "SEC1" })
            Assert.True(MergeRoslynSecuritySarifFunction.IsSecurityRule(JsonValue.Create(id)), id);
        // Style, performance and compiler diagnostics are the thousand-odd results this filter keeps out.
        foreach (var id in new[] { "CA1859", "CA1816", "SYSLIB1045", "CS0164", "PRM004", "CA1068", "", "CA5359\n" })
            Assert.False(MergeRoslynSecuritySarifFunction.IsSecurityRule(JsonValue.Create(id)), id);
        Assert.False(MergeRoslynSecuritySarifFunction.IsSecurityRule(JsonValue.Create(5359)));
        Assert.False(MergeRoslynSecuritySarifFunction.IsSecurityRule(null));
    }

    [Theory]
    [InlineData("""{"suppressions":[]}""", false)]
    [InlineData("""{}""", false)]
    [InlineData("""{"suppressions":[{"kind":"inSource"}]}""", true)]
    [InlineData("""{"suppressions":[{"state":"accepted"}]}""", true)]
    [InlineData("""{"suppressions":[{"state":"Accepted"}]}""", true)]
    [InlineData("""{"suppressions":[{"state":"rejected"}]}""", false)]
    [InlineData("""{"suppressions":[{"state":"underReview"}]}""", false)]
    [InlineData("""{"suppressions":[{"state":null}]}""", false)]
    [InlineData("""{"suppressions":[null]}""", true)]
    public void Suppression_CountsOnlyWhenAcceptedOrStateless(string result, bool suppressed) =>
        Assert.Equal(suppressed, MergeRoslynSecuritySarifFunction.IsSuppressed(JsonNode.Parse(result)));

    [Fact]
    public void Merge_KeepsOnlyOpenSecurityFindingsAcrossProjects()
    {
        var merged = MergeRoslynSecuritySarifFunction.Merge(
        [
            Report([Result("CA5359"), Result("CA1859"), Result("CS0164")]),
            Report([
                Result("CA2100"),
                Result("SEC004", new JsonObject { ["suppressions"] = new JsonArray(new JsonObject { ["kind"] = "inSource", ["justification"] = "documented" }) })
            ])
        ]);

        Assert.Equal(["CA5359", "CA2100"], RuleIds(merged));
        Assert.Equal("2.1.0", (string)merged["version"]!);
    }

    [Fact]
    public void Merge_CarriesEachRuleAcrossAndDropsTheStaleIndex()
    {
        var merged = MergeRoslynSecuritySarifFunction.Merge(
        [
            Report([Result("CA5359", new JsonObject { ["ruleIndex"] = 0 })],
                [new JsonObject { ["id"] = "CA5359", ["helpUri"] = "https://example.test" }])
        ]);

        var finding = Assert.Single(Results(merged))!.AsObject();
        Assert.False(finding.ContainsKey("ruleIndex"));
        Assert.Equal("""[{"id":"CA5359","helpUri":"https://example.test"}]""",
            merged["runs"]![0]!["tool"]!["driver"]!["rules"]!.ToJsonString());
    }

    [Fact]
    public void Merge_DeclaresARuleOnceWhenSeveralProjectsReportIt()
    {
        var merged = MergeRoslynSecuritySarifFunction.Merge(
        [
            Report([Result("CA5392", new JsonObject { ["ruleIndex"] = 0 })], [new JsonObject { ["id"] = "CA5392" }]),
            Report([Result("CA5392", new JsonObject { ["ruleIndex"] = 0 })], [new JsonObject { ["id"] = "CA5392" }])
        ]);

        Assert.Equal(2, Results(merged).Count);
        Assert.Single(merged["runs"]![0]!["tool"]!["driver"]!["rules"]!.AsArray());
    }

    [Fact]
    public void Merge_OfACleanSolutionIsAValidEmptyReport()
    {
        var merged = MergeRoslynSecuritySarifFunction.Merge([Report([Result("CA1859")])]);

        Assert.Empty(Results(merged));
        Assert.Equal("Roslyn security analyzers", (string)merged["runs"]![0]!["tool"]!["driver"]!["name"]!);
    }

    [Fact]
    public void Merge_ToleratesAReportWithNoRunsOrNoResults() =>
        Assert.Empty(Results(MergeRoslynSecuritySarifFunction.Merge(
            [new JsonObject(), new JsonObject { ["runs"] = new JsonArray() }, new JsonObject { ["runs"] = new JsonArray(new JsonObject()) }])));

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(
            new Dictionary<string, string> { ["input"] = "analysis/roslyn", ["output"] = "analysis/out/roslyn-security.sarif" });
        return _harness.RunAsync(new MergeRoslynSecuritySarifFunction(), _env);
    }

    [Fact]
    public async Task Function_WritesOneLineOfMergedSarifAndCountsTheFindings()
    {
        _harness.Write("ws/analysis/roslyn/Back.sarif", Report([Result("CA5359"), Result("CA1859")]).ToJsonString());
        _harness.Write("ws/analysis/roslyn/Front.sarif", Report([Result("SEC001")]).ToJsonString());
        _harness.Write("ws/analysis/roslyn/notes.txt", "not a report");

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var written = await File.ReadAllTextAsync(Path.Combine(_harness.Workspace, "analysis/out/roslyn-security.sarif"), TestContext.Current.CancellationToken);
        Assert.EndsWith("\n", written, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', written.TrimEnd('\n'));
        Assert.Equal(["CA5359", "SEC001"], RuleIds(JsonNode.Parse(written)!.AsObject()));
        Assert.Contains(_harness.Log, entry => entry.Line == "2 open security findings from 2 projects");
    }

    [Fact]
    public async Task Function_RefusesADirectoryWithoutReport()
    {
        _harness.Write("ws/analysis/roslyn/notes.txt", "not a report");

        var (result, _) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("No SARIF report found in", result.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_harness.Workspace, "analysis/out/roslyn-security.sarif")));
    }

    [Fact]
    public async Task Function_RefusesAMissingDirectory()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("does not exist", result.FailureReason, StringComparison.Ordinal);
    }
}
