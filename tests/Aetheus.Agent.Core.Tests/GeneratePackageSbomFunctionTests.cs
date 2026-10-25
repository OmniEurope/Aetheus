// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>generate-package-sbom.mjs</c> script, run through the real executor on
/// every input the script itself processed (<c>TestData/package-sbom-cases.json</c>,
/// <see cref="ScriptReferenceData"/>): the case of <c>generate-package-sbom.test.js</c>, NuGet inputs with
/// mixed-case duplicates, requested-version fallbacks and values of other types, npm inputs with merged
/// dependency maps, ranges, scoped and non-ASCII names, the argument refusals and the
/// <c>SOURCE_DATE_EPOCH</c> forms. The SPDX document must be the script's byte for byte; a refusal must
/// carry the script's message verbatim.
/// </summary>
public sealed class GeneratePackageSbomFunctionTests : IDisposable
{
    private const string File = "package-sbom-cases.json";
    private const string Prefix = "Function 'generate-package-sbom' failed: ";
    private readonly FunctionTestHarness _harness = new("aetheus-package-sbom");

    public void Dispose() => _harness.Dispose();

    public static TheoryData<string> Cases() => ScriptReferenceData.CaseNames(File);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryInput_GetsTheDocumentOrTheRefusalOfTheScript(string name)
    {
        var testCase = ScriptReferenceData.Case(File, name);
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));

        var result = await RunAsync(testCase, parameters);

        var output = Path.Combine(_harness.Workspace, parameters["output"]);
        if (testCase.TryGetProperty("difference", out _))
        {
            Assert.False(testCase.TryGetProperty("error", out _));
            Assert.StartsWith(Prefix + "The package id", result.FailureReason, StringComparison.Ordinal);
        }
        else if (testCase.TryGetProperty("error", out var error))
        {
            Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
            if (error.TryGetProperty("code", out _))
                Assert.StartsWith(Prefix, result.FailureReason, StringComparison.Ordinal);
            else
                Assert.Equal(Prefix + error.GetProperty("message").GetString(), result.FailureReason);
            Assert.False(System.IO.File.Exists(output));
        }
        else
        {
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(Encoding.UTF8.GetBytes(testCase.GetProperty("output").GetString()!), await System.IO.File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public void TheReferenceData_CoversTheScriptTestAndBothEcosystems()
    {
        var names = ScriptReferenceData.CaseNames(File).Select(row => row.Data).ToHashSet();

        Assert.Subset(names, new HashSet<string>
        {
            // generate-package-sbom.test.js
            "nuget-test-case",
            "nuget-mixed", "nuget-odd-values", "npm-package", "npm-odd-values", "invalid-ecosystem", "invalid-commit-short",
            "epoch-unset", "epoch-zero", "epoch-text", "epoch-max", "epoch-beyond-max", "nuget-non-ascii-id"
        });
    }

    [Fact]
    public async Task TheEpochParameter_StandsForTheVariable()
    {
        var testCase = ScriptReferenceData.Case(File, "nuget-test-case");
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));
        parameters["source_date_epoch"] = "1784793600";

        var result = await RunAsync(testCase, parameters, new Dictionary<string, string>(StringComparer.Ordinal) { ["SOURCE_DATE_EPOCH"] = "0" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            Encoding.UTF8.GetBytes(testCase.GetProperty("output").GetString()!),
            await System.IO.File.ReadAllBytesAsync(Path.Combine(_harness.Workspace, parameters["output"]), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnEmptyEpochParameter_FallsBackToTheVariable()
    {
        var testCase = ScriptReferenceData.Case(File, "nuget-test-case");
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));
        parameters["source_date_epoch"] = "";

        var result = await RunAsync(testCase, parameters, new Dictionary<string, string>(StringComparer.Ordinal) { ["SOURCE_DATE_EPOCH"] = "0" });

        Assert.Equal(Prefix + "SOURCE_DATE_EPOCH must contain the immutable source commit timestamp.", result.FailureReason);
    }

    private async Task<ExecutorResult> RunAsync(JsonElement testCase, Dictionary<string, string> parameters, Dictionary<string, string>? environment = null)
    {
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "out"));
        var env = environment ?? AssuranceReferenceData.Strings(testCase.GetProperty("environment"));
        env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters);
        return (await _harness.RunAsync(new GeneratePackageSbomFunction(), env)).Result;
    }
}
