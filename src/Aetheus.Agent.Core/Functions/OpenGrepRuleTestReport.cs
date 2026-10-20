// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The JSON report of <c>opengrep scan --test --json</c>, reduced to what decides the step: each rule
/// check and whether it passed (with the lines it missed and the lines it should not have reported),
/// the rule files that have no test, and the rule files OpenGrep could not load.
/// </summary>
internal sealed record OpenGrepRuleTestReport(
    IReadOnlyList<OpenGrepRuleCheck> Checks,
    IReadOnlyList<string> RuleFilesWithoutTests,
    IReadOnlyList<string> RuleFilesWithErrors)
{
    public IReadOnlyList<OpenGrepRuleCheck> Failed => Checks.Where(check => !check.Passed).ToList();

    /// <summary>Whether the rules are proven: at least one check ran, every check passed, every rule
    /// file has its test and none failed to load.</summary>
    public bool Passed =>
        Checks.Count > 0 && Failed.Count == 0 && RuleFilesWithoutTests.Count == 0 && RuleFilesWithErrors.Count == 0;

    /// <summary>The report OpenGrep printed, or null when <paramref name="json"/> is not one (OpenGrep
    /// stops before writing it on some errors, an annotation naming an unknown rule among them).</summary>
    public static OpenGrepRuleTestReport? TryParse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("results", out var results)
                || results.ValueKind != JsonValueKind.Object)
                return null;
            return new OpenGrepRuleTestReport(
                ReadChecks(results),
                ReadNames(root, "config_missing_tests"),
                ReadNames(root, "config_with_errors"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The lines of the step log that explain a failed report, one per problem.</summary>
    public IEnumerable<string> DescribeProblems()
    {
        foreach (var check in Failed)
            yield return $"FAILED {check.RuleId} ({check.RuleFile}): " + string.Join("; ", check.Targets.Select(target =>
                $"{target.File} missed lines [{string.Join(", ", target.Missed)}], unexpected lines [{string.Join(", ", target.Unexpected)}]"))
                + (check.Errors.Count > 0 ? "; errors: " + string.Join(" | ", check.Errors) : string.Empty);
        foreach (var file in RuleFilesWithoutTests)
            yield return $"NO TEST {file}: no test file carries its rules.";
        foreach (var file in RuleFilesWithErrors)
            yield return $"INVALID {file}: OpenGrep could not load this rule file.";
        if (Checks.Count == 0)
            yield return "NO CHECK: OpenGrep ran no rule test at all.";
    }

    private static List<OpenGrepRuleCheck> ReadChecks(JsonElement results)
    {
        var checks = new List<OpenGrepRuleCheck>();
        foreach (var ruleFile in results.EnumerateObject())
        {
            if (ruleFile.Value.ValueKind != JsonValueKind.Object
                || !ruleFile.Value.TryGetProperty("checks", out var ruleChecks)
                || ruleChecks.ValueKind != JsonValueKind.Object)
                continue;
            foreach (var check in ruleChecks.EnumerateObject())
                checks.Add(ReadCheck(ruleFile.Name, check.Name, check.Value));
        }
        return checks;
    }

    private static OpenGrepRuleCheck ReadCheck(string ruleFile, string ruleId, JsonElement check)
    {
        var passed = check.TryGetProperty("passed", out var value) && value.ValueKind == JsonValueKind.True;
        var targets = new List<OpenGrepRuleCheckTarget>();
        if (check.TryGetProperty("matches", out var matches) && matches.ValueKind == JsonValueKind.Object)
        {
            foreach (var target in matches.EnumerateObject())
            {
                var expected = ReadLines(target.Value, "expected_lines");
                var reported = ReadLines(target.Value, "reported_lines");
                targets.Add(new OpenGrepRuleCheckTarget(
                    target.Name, expected.Except(reported).Order().ToList(), reported.Except(expected).Order().ToList()));
            }
        }
        var errors = check.TryGetProperty("errors", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(error => error.ToString()).ToList()
            : [];
        return new OpenGrepRuleCheck(ruleFile, ruleId, passed, targets, errors);
    }

    private static List<int> ReadLines(JsonElement target, string name) =>
        target.TryGetProperty(name, out var lines) && lines.ValueKind == JsonValueKind.Array
            ? lines.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.Number).Select(line => line.GetInt32()).ToList()
            : [];

    private static List<string> ReadNames(JsonElement root, string name) =>
        root.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : item.ToString()).ToList()
            : [];
}

/// <summary>One rule of one rule file, as <c>opengrep scan --test</c> checked it.</summary>
internal sealed record OpenGrepRuleCheck(
    string RuleFile, string RuleId, bool Passed, IReadOnlyList<OpenGrepRuleCheckTarget> Targets, IReadOnlyList<string> Errors);

/// <summary>The lines a rule should have reported in a test file and did not, and the lines it
/// reported where the file expects nothing.</summary>
internal sealed record OpenGrepRuleCheckTarget(string File, IReadOnlyList<int> Missed, IReadOnlyList<int> Unexpected);
