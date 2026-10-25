// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/verify-delivery-promotion.mjs</c>, run through the real executor
/// on every contract the script itself verified (<c>TestData/delivery-promotion-cases.json</c>,
/// <see cref="ScriptReferenceData"/>), each sealed by the real JS generator: every case of
/// <c>delivery-contract.test.mjs</c>, the refusals of the candidate (schema 1, invalid, identity,
/// release version, artifact digests), bootstrap and pre/post migration states, the previous-release
/// branch, malformed baselines, and keys whose <c>localeCompare</c> order differs from the ordinal one.
/// A success must log the script's lines, in order; a refusal must carry the script's message verbatim
/// (multi-line ones included); where the script failed on a file it could not read, the port must fail
/// too, with the .NET message.
/// </summary>
public sealed class VerifyDeliveryPromotionFunctionTests : IDisposable
{
    private const string File = "delivery-promotion-cases.json";
    private const string Prefix = "Function 'verify-delivery-promotion' failed: ";
    private readonly FunctionTestHarness _harness = new("aetheus-delivery-verify");

    public void Dispose() => _harness.Dispose();

    public static TheoryData<string> Cases() => ScriptReferenceData.CaseNames(File);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRun_GetsTheVerdictOfTheScript(string name)
    {
        var testCase = ScriptReferenceData.Case(File, name);
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var refusedByThePort = testCase.TryGetProperty("difference", out _);

        foreach (var run in testCase.GetProperty("runs").EnumerateArray())
        {
            _harness.Log.Clear();
            var result = await RunAsync(run.GetProperty("args").EnumerateArray().Select(arg => arg.GetString()!).ToArray());
            if (refusedByThePort)
            {
                Assert.False(run.TryGetProperty("error", out _));
                Assert.StartsWith(Prefix + "localeCompare is only reproduced for ASCII text", result.FailureReason, StringComparison.Ordinal);
            }
            else if (run.TryGetProperty("error", out var error))
                AssertFailure(result, error);
            else
            {
                Assert.Equal(0, result.ExitCode);
                Assert.Equal(run.GetProperty("stdout").EnumerateArray().Select(line => line.GetString()!), FunctionLines());
            }
        }
    }

    [Fact]
    public void TheReferenceData_CoversTheScriptTestsAndEveryRequiredPath()
    {
        var names = ScriptReferenceData.CaseNames(File).Select(row => row.Data).ToHashSet();

        Assert.Subset(names, new HashSet<string>
        {
            // delivery-contract.test.mjs
            "bootstrap-verifies-exact-bytes", "different-manifest-different-candidate", "schema-states",
            "schema-1-refused", "invalid-contracts", "invalid-identity", "release-version", "artifact-digests",
            "bootstrap-database-states", "baseline-malformed", "previous-release", "previous-release-non-prefix",
            "previous-release-crafted", "locale-ordered-keys", "non-ascii-key"
        });
    }

    [Fact]
    public async Task TheNeitherStateRefusal_IsTheScriptsMultiLineMessage()
    {
        var testCase = ScriptReferenceData.Case(File, "schema-states");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var expected = testCase.GetProperty("runs")[2].GetProperty("error").GetProperty("message").GetString()!;

        var result = await RunAsync(".pipeline-artifacts/contract.json", "schema-rogue", ".pipeline-artifacts");

        Assert.Equal(Prefix + expected, result.FailureReason);
        Assert.Matches(
            "^The production schema is neither state this candidate was proven against\\.\n"
            + "  live         : [0-9a-f]{64} \\(3 migrations\\)\n"
            + "  schemaBefore : [0-9a-f]{64} \\(1 migrations\\)\n"
            + "  schemaAfter  : [0-9a-f]{64} \\(2 migrations\\)\n"
            + "Requalify the candidate against the current production schema\\.\\z",
            expected);
    }

    [Fact]
    public async Task KeysOrderedByLocaleCompare_SealADifferentIdentityThanOrdinalOrder()
    {
        var testCase = ScriptReferenceData.Case(File, "locale-ordered-keys");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var contract = (JsObject)JsJson.Parse(await System.IO.File.ReadAllTextAsync(
            _harness.Artifact("contract.json"), TestContext.Current.CancellationToken))!;
        var identity = contract.Clone();
        identity.Remove("candidateId");
        identity.Remove("candidateVersion");

        Assert.True(DeliveryContractIdentity.HasValidIdentity(contract));
        Assert.NotEqual(contract["candidateId"], DeliveryContractIdentity.Sha256(JsJson.Stringify(OrdinalCanonical(identity))!));
    }

    [Fact]
    public async Task AnEmptyLiveContract_ReadsAsNone()
    {
        var testCase = ScriptReferenceData.Case(File, "schema-states");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);

        var result = await RunAsync(".pipeline-artifacts/contract.json", "schema-after", ".pipeline-artifacts", "");

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task AMissingCandidate_Fails()
    {
        var result = await RunAsync(".pipeline-artifacts/absent.json", "-", ".pipeline-artifacts");

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix, result.FailureReason, StringComparison.Ordinal);
    }

    private static object? OrdinalCanonical(object? value) => value switch
    {
        List<object?> array => array.Select(OrdinalCanonical).ToList(),
        JsObject obj => obj.Properties.OrderBy(property => property.Key, StringComparer.Ordinal)
            .Aggregate(new JsObject(), (copy, property) =>
            {
                copy.Set(property.Key, OrdinalCanonical(property.Value));
                return copy;
            }),
        _ => value
    };

    /// <summary>The lines the function itself logged, between the executor's start and success lines.</summary>
    private List<string> FunctionLines() => _harness.Log
        .Select(entry => entry.Line)
        .SkipWhile(line => !line.StartsWith("Running Aetheus function", StringComparison.Ordinal))
        .Skip(1)
        .TakeWhile(line => !line.StartsWith("Function 'verify-delivery-promotion'", StringComparison.Ordinal))
        .ToList();

    private async Task<ExecutorResult> RunAsync(params string[] args)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["candidate_contract"] = args[0],
            ["production_schema"] = args[1],
            ["artifact_directory"] = args[2]
        };
        if (args.Length > 3)
            parameters["live_contract"] = args[3];
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters)
        };
        return (await _harness.RunAsync(new VerifyDeliveryPromotionFunction(), env)).Result;
    }

    private static void AssertFailure(ExecutorResult result, JsonElement error)
    {
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        if (error.TryGetProperty("code", out _))
            Assert.StartsWith(Prefix, result.FailureReason, StringComparison.Ordinal);
        else
            Assert.Equal(Prefix + error.GetProperty("message").GetString(), result.FailureReason);
    }
}
