// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/pipelines/toto-conformance-fixture/common/deploy/scripts/generate-candidate-assurance-contract.mjs</c>, run through the real
/// executor on every case the script itself was run on (<see cref="AssuranceReferenceData"/>): the
/// contract and the evidence files must be the script's bytes, the published grade and the log line the
/// script's, and a refusal the script's message. A case where the script crashed (a TypeError, a
/// provenance that is missing or not JSON) must fail too; the message is compared exactly for a
/// TypeError, whose V8 text the port reproduces.
/// </summary>
public sealed class GenerateAssuranceContractFunctionTests : IDisposable
{
    private const string Prefix = "Function 'generate-assurance-contract' failed: ";
    private readonly FunctionTestHarness _harness = new("aetheus-assurance-generate");

    public void Dispose() => _harness.Dispose();

    public static TheoryData<string> Cases() => AssuranceReferenceData.GeneratorCaseNames();

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryCase_WritesTheBytesAndGivesTheVerdictOfTheScript(string name)
    {
        var testCase = AssuranceReferenceData.GeneratorCase(name);
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);

        var (result, variables) = await RunAsync(
            AssuranceReferenceData.Strings(testCase.GetProperty("parameters")),
            AssuranceReferenceData.Strings(testCase.GetProperty("environment")));

        var expected = testCase.GetProperty("expected");
        AssertOutputs(expected.GetProperty("outputs"));
        if (expected.TryGetProperty("error", out var error))
        {
            AssertFailure(result, error);
            return;
        }
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(expected.GetProperty("grade").GetString(), variables["CANDIDATE_ASSURANCE_GRADE"]);
        Assert.Contains(_harness.Log, entry => entry.Line == expected.GetProperty("log").GetString());
    }

    [Fact]
    public void TheReferenceData_CoversEveryScenarioTheTaskNames()
    {
        var names = AssuranceReferenceData.GeneratorCaseNames().Select(row => row.Data).ToList();

        Assert.Subset(names.ToHashSet(), new HashSet<string>
        {
            "light-passing", "full-nightly-with-extensions", "failing-gates", "legacy-pascalcase-attestation",
            "bootstrap-authorized", "inapplicable-tests", "unavailable-summaries", "checkpoint-identities"
        });
    }

    [Fact]
    public async Task TheSettingParameters_StandForTheirVariables()
    {
        var testCase = AssuranceReferenceData.GeneratorCase("full-nightly-with-extensions");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));
        var environment = AssuranceReferenceData.Strings(testCase.GetProperty("environment"));
        parameters["profile"] = environment["AETHEUS_ASSURANCE_PROFILE"];
        parameters["performance_enabled"] = environment["AETHEUS_ASSURANCE_PERFORMANCE_ENABLED"];
        parameters["allow_bootstrap"] = environment["AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP"];
        parameters["inapplicable_tests"] = "nothing-real";
        // The variables now say otherwise: the parameters must win.
        environment["AETHEUS_ASSURANCE_PROFILE"] = "light";
        environment["AETHEUS_ASSURANCE_PERFORMANCE_ENABLED"] = "false";
        environment["AETHEUS_ASSURANCE_ALLOW_BOOTSTRAP"] = "true";
        environment["AETHEUS_ASSURANCE_INAPPLICABLE_TESTS"] = "unit";

        var (result, _) = await RunAsync(parameters, environment);

        Assert.Equal(0, result.ExitCode);
        AssertOutputs(testCase.GetProperty("expected").GetProperty("outputs"));
    }

    [Fact]
    public async Task AnEmptyProfileVariable_IsRefusedLikeTheScriptRefusedIt()
    {
        // process.env.AETHEUS_ASSURANCE_PROFILE ?? "full": only an unset profile defaults.
        var testCase = AssuranceReferenceData.GeneratorCase("light-passing");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var environment = AssuranceReferenceData.Strings(testCase.GetProperty("environment"));
        environment["AETHEUS_ASSURANCE_PROFILE"] = string.Empty;

        var (result, _) = await RunAsync(AssuranceReferenceData.Strings(testCase.GetProperty("parameters")), environment);

        Assert.Equal(Prefix + "AETHEUS_ASSURANCE_PROFILE must be 'light' or 'full', not ''.", result.FailureReason);
    }

    [Fact]
    public async Task AnUnsetProfile_DefaultsToFull()
    {
        var testCase = AssuranceReferenceData.GeneratorCase("light-passing");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var environment = AssuranceReferenceData.Strings(testCase.GetProperty("environment"));
        environment.Remove("AETHEUS_ASSURANCE_PROFILE");

        var (result, _) = await RunAsync(AssuranceReferenceData.Strings(testCase.GetProperty("parameters")), environment);

        Assert.Equal(0, result.ExitCode);
        var contract = await File.ReadAllTextAsync(
            _harness.Artifact("assurance-contract.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"profile\": \"full\"", contract, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("security_history")]
    [InlineData("extended_qa")]
    public async Task OneExtensionDirectoryAlone_IsRefused(string given)
    {
        var testCase = AssuranceReferenceData.GeneratorCase("light-passing");
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));
        parameters[given] = ".assurance-input/qa";

        var (result, _) = await RunAsync(parameters, AssuranceReferenceData.Strings(testCase.GetProperty("environment")));

        Assert.Equal(Prefix + "security_history and extended_qa must be given together.", result.FailureReason);
        Assert.False(Directory.Exists(_harness.ArtifactsDirectory));
    }

    [Fact]
    public async Task AMissingRequiredParameter_IsRefusedByTheCatalog()
    {
        var parameters = AssuranceReferenceData.Strings(AssuranceReferenceData.GeneratorCase("light-passing").GetProperty("parameters"));
        parameters.Remove("provenance");

        var (result, _) = await RunAsync(parameters, new Dictionary<string, string>(StringComparer.Ordinal));

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
    }

    [PlatformFact("linux")]
    public async Task ASymbolicLinkToASummary_IsNeitherFollowedNorMatched()
    {
        var testCase = AssuranceReferenceData.GeneratorCase("light-passing");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var quality = Path.Combine(_harness.Workspace, ".assurance-input", "quality");
        File.Move(Path.Combine(quality, "quality-summary.json"), Path.Combine(_harness.Root, "quality-summary.json"));
        File.CreateSymbolicLink(Path.Combine(quality, "quality-summary.json"), Path.Combine(_harness.Root, "quality-summary.json"));
        Directory.CreateSymbolicLink(Path.Combine(quality, "linked"), _harness.Root);

        var (result, _) = await RunAsync(
            AssuranceReferenceData.Strings(testCase.GetProperty("parameters")),
            AssuranceReferenceData.Strings(testCase.GetProperty("environment")));

        Assert.Equal(0, result.ExitCode);
        var contract = await File.ReadAllTextAsync(
            _harness.Artifact("assurance-contract.json"), TestContext.Current.CancellationToken);
        Assert.Contains("\"reason\": \"summary-missing\"", contract, StringComparison.Ordinal);
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(
        Dictionary<string, string> parameters, Dictionary<string, string> environment)
    {
        var env = new Dictionary<string, string>(environment, StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters)
        };
        return _harness.RunAsync(new GenerateAssuranceContractFunction(), env);
    }

    private void AssertOutputs(JsonElement expected)
    {
        var actual = AssuranceReferenceData.Snapshot(_harness.Workspace, ".pipeline-artifacts");
        Assert.Equal(
            expected.EnumerateObject().Select(file => file.Name).Order(StringComparer.Ordinal),
            actual.Keys.Order(StringComparer.Ordinal));
        foreach (var file in expected.EnumerateObject())
            Assert.True(
                AssuranceReferenceData.Bytes(file.Value).AsSpan().SequenceEqual(actual[file.Name]),
                $"{file.Name} differs from the script's output.");
    }

    private static void AssertFailure(Executors.ExecutorResult result, JsonElement error)
    {
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        var kind = error.GetProperty("kind").GetString();
        if (kind is "Error" or "TypeError")
            Assert.Equal(Prefix + error.GetProperty("message").GetString(), result.FailureReason);
        else
            Assert.StartsWith(Prefix, result.FailureReason);
    }
}
