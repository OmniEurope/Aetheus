// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/verify-candidate-assurance-contract.mjs</c>, run through the real
/// executor on every contract the script itself verified (<see cref="AssuranceReferenceData"/>), each
/// sealed by the JS generator: every case of <c>assurance-contract.test.mjs</c> and of
/// <c>CandidateAssuranceContractTests</c>, plus the JavaScript corner cases of the parse and the seal
/// (duplicate and integer keys, escapes, number forms, values of another type). Each verdict must be
/// the script's: the same success line, or the same refusal message. Where the script crashed, the port
/// must fail too: a TypeError with V8's message, a missing file with the .NET one, and an unreadable
/// contract with the script's prefix followed by the .NET cause.
/// </summary>
public sealed class VerifyAssuranceContractFunctionTests : IDisposable
{
    private const string Prefix = "Function 'verify-assurance-contract' failed: ";
    private const string InvalidContract = "Assurance contract is missing or invalid: ";
    private readonly FunctionTestHarness _harness = new("aetheus-assurance-verify");

    public void Dispose() => _harness.Dispose();

    public static TheoryData<string> Cases() => AssuranceReferenceData.VerifierCaseNames();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryContract_GetsTheVerdictOfTheScript(string name)
    {
        var testCase = AssuranceReferenceData.VerifierCase(name);
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);

        foreach (var verdict in testCase.GetProperty("verdicts").EnumerateArray())
        {
            _harness.Log.Clear();
            var result = await RunAsync(verdict.GetProperty("minimumGrade").GetString()!, verdict.GetProperty("requirement").GetString()!);
            if (verdict.TryGetProperty("error", out var error))
                AssertFailure(result, error);
            else
            {
                Assert.Equal(0, result.ExitCode);
                Assert.Contains(_harness.Log, entry => entry.Line == verdict.GetProperty("log").GetString());
            }
        }
    }

    [Fact]
    public void TheReferenceData_HoldsACounterpartOfEveryScriptAndBackendCase()
    {
        var names = AssuranceReferenceData.VerifierCaseNames().Select(row => row.Data).ToHashSet();

        Assert.Subset(names, new HashSet<string>
        {
            // assurance-contract.test.mjs
            "light-previous-e2e-failed", "light-rollback-smoke-failed", "light-deployment-ready", "full-claims-nightly-exemption",
            "light-excuses-previous-integration", "schema-1-as-full", "full-nightly-extensions", "light-with-extensions",
            // CandidateAssuranceContractTests
            "grade-f-visible-but-not-deployable", "incomplete-analysis-graded-f", "malformed-analysis-graded-f",
            "deployment-ready-b-c-b", "altered-contract", "altered-evidence", "bootstrap-unauthorized", "bootstrap-authorized",
            "error-gate-not-ready", "missing-summaries", "missing-required-test-output"
        });
    }

    [Theory]
    [InlineData("--require-evidence")]
    [InlineData("Evidence")]
    [InlineData("readiness")]
    public async Task AnUnsupportedRequirement_IsRefused(string requirement)
    {
        var result = await RunAsync("F", requirement);

        Assert.Equal(Prefix + $"Unsupported assurance verification mode '{requirement}'.", result.FailureReason);
    }

    [Fact]
    public async Task AMissingContract_IsRefusedWithTheScriptPrefix()
    {
        var result = await RunAsync("F", "none");

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix + InvalidContract, result.FailureReason);
    }

    [Fact]
    public async Task AContractTheGeneratorPortWrote_IsAcceptedByTheVerifierPort()
    {
        var testCase = AssuranceReferenceData.GeneratorCase("full-nightly-with-extensions");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var env = new Dictionary<string, string>(AssuranceReferenceData.Strings(testCase.GetProperty("environment")), StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(AssuranceReferenceData.Strings(testCase.GetProperty("parameters")))
        };
        Assert.Equal(0, (await _harness.RunAsync(new GenerateAssuranceContractFunction(), env)).Result.ExitCode);
        await File.WriteAllTextAsync(
            _harness.Artifact("source-commit"),
            testCase.GetProperty("parameters").GetProperty("source_sha").GetString() + "\n",
            TestContext.Current.CancellationToken);

        var result = await RunAsync("D", "deployment-readiness");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_harness.Log, entry =>
            entry.Line == "Candidate deployment readiness verified with all required evidence Passed or Available at grade D.");
    }

    /// <summary>The candidate runs on after failed unit tests and seals them (UNIT_TEST_GATE_STATUS=1); the
    /// deploy boundary must still refuse that candidate. No generator parameter declares <c>unit</c>
    /// inapplicable here, which is the only way a required test could be left out of the contract.</summary>
    [Theory]
    [InlineData("light-passing")]
    [InlineData("full-nightly-with-extensions")]
    public async Task ACandidateSealedWithFailedUnitTests_IsKeptByTheCandidateButRefusedForDeployment(string generatorCase)
    {
        var testCase = AssuranceReferenceData.GeneratorCase(generatorCase);
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));
        Assert.False(parameters.ContainsKey("inapplicable_tests"));
        var env = new Dictionary<string, string>(AssuranceReferenceData.Strings(testCase.GetProperty("environment")), StringComparer.Ordinal)
        {
            ["UNIT_TEST_GATE_STATUS"] = "1",
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters)
        };
        env.Remove("AETHEUS_ASSURANCE_INAPPLICABLE_TESTS");
        Assert.Equal(0, (await _harness.RunAsync(new GenerateAssuranceContractFunction(), env)).Result.ExitCode);
        await File.WriteAllTextAsync(
            _harness.Artifact("source-commit"), parameters["source_sha"] + "\n", TestContext.Current.CancellationToken);
        using (var contract = JsonDocument.Parse(await File.ReadAllTextAsync(
                   _harness.Artifact("assurance-contract.json"), TestContext.Current.CancellationToken)))
        {
            var unit = contract.RootElement.GetProperty("tests").GetProperty("unit");
            Assert.True(unit.GetProperty("required").GetBoolean());
            Assert.Equal("Failed", unit.GetProperty("status").GetString());
        }

        // The candidate keeps the evidence it sealed: a failed required test is graded, not hidden.
        Assert.Equal(0, (await RunAsync("F", "evidence")).ExitCode);

        _harness.Log.Clear();
        var deployment = await RunAsync("E", "deployment-readiness");

        Assert.NotEqual(0, deployment.ExitCode);
        Assert.Contains("Deployment readiness requires unit to be Passed, not Failed.", deployment.FailureReason, StringComparison.Ordinal);
    }

    private Task<Executors.ExecutorResult> RunAsync(string minimumGrade, string requirement)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["contract"] = ".pipeline-artifacts/assurance-contract.json",
            ["source_commit"] = ".pipeline-artifacts/source-commit",
            ["minimum_grade"] = minimumGrade,
            ["requirement"] = requirement
        };
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters)
        };
        return RunCoreAsync(env);
    }

    private async Task<Executors.ExecutorResult> RunCoreAsync(Dictionary<string, string> env) =>
        (await _harness.RunAsync(new VerifyAssuranceContractFunction(), env)).Result;

    private static void AssertFailure(Executors.ExecutorResult result, JsonElement error)
    {
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        var message = error.GetProperty("message").GetString()!;
        switch (error.GetProperty("kind").GetString())
        {
            case "Error" when message.StartsWith(InvalidContract, StringComparison.Ordinal):
                Assert.StartsWith(Prefix + InvalidContract, result.FailureReason);
                break;
            case "Error" or "TypeError":
                Assert.Equal(Prefix + message, result.FailureReason);
                break;
            default:
                Assert.StartsWith(Prefix, result.FailureReason);
                break;
        }
    }
}
