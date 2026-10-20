// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/record-nightly-qualification.mjs</c>, run through the real executor
/// on every input the script itself processed (<c>TestData/nightly-qualification-cases.json</c>,
/// <see cref="ScriptReferenceData"/>): the cases of <c>nightly-qualification.test.mjs</c>, nested,
/// duplicated, unreadable and falsy summaries, odd grades, run ids and severities, the TypeErrors and
/// the refusals. The driver ran the script with the global <c>Date</c> replaced for the run by a subclass
/// whose argument-less constructor returns the case's instant; the port takes the same instant from a
/// <see cref="FakeTimeProvider"/>, so <c>generatedAt</c> and the seal over it must match byte for byte.
/// </summary>
public sealed class RecordNightlyQualificationFunctionTests : IDisposable
{
    private const string File = "nightly-qualification-cases.json";
    private const string Prefix = "Function 'record-nightly-qualification' failed: ";
    private readonly FunctionTestHarness _harness = new("aetheus-nightly-qualification");

    public void Dispose() => _harness.Dispose();

    public static TheoryData<string> Cases() => ScriptReferenceData.CaseNames(File);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryInput_GetsTheRecordOrTheRefusalOfTheScript(string name)
    {
        var testCase = ScriptReferenceData.Case(File, name);
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));
        var instant = DateTimeOffset.Parse(testCase.GetProperty("instant").GetString()!, CultureInfo.InvariantCulture);

        var result = await RunAsync(parameters, AssuranceReferenceData.Strings(testCase.GetProperty("environment")), instant);

        var output = Path.Combine(_harness.Workspace, parameters["output"]);
        if (testCase.TryGetProperty("error", out var error))
        {
            Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
            Assert.Equal(Prefix + error.GetProperty("message").GetString(), result.FailureReason);
            Assert.False(System.IO.File.Exists(output));
            return;
        }
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Encoding.UTF8.GetBytes(testCase.GetProperty("output").GetString()!), await System.IO.File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        // Compared as JSON text, so a lone surrogate in a status is compared as the escape both wrote.
        var logged = _harness.Log.Where(entry => entry.Line.StartsWith("Nightly qualification recorded", StringComparison.Ordinal)).ToList();
        Assert.Equal(
            testCase.GetProperty("stdout").EnumerateArray().Select(line => line.GetRawText()),
            logged.Select(entry => JsJson.Stringify(entry.Line)!));
        Assert.All(logged, entry => Assert.Equal(TaskLogLevel.Info, entry.Level));
    }

    [Fact]
    public void TheReferenceData_CoversTheScriptTest()
    {
        var names = ScriptReferenceData.CaseNames(File).Select(row => row.Data).ToHashSet();

        Assert.Subset(names, new HashSet<string>
        {
            // nightly-qualification.test.mjs
            "clean", "measured", "unmeasured", "other-commit", "extensions-and-grade", "unsealed",
            "all-sections", "unavailable-shapes", "odd-grades", "severity-forms", "measures-not-iterable-object", "other-instant"
        });
    }

    [Fact]
    public async Task TheSeal_IsTheDigestOfTheRecordWithoutIt()
    {
        var testCase = ScriptReferenceData.Case(File, "severity-forms");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));

        var result = await RunAsync(parameters, [], new DateTimeOffset(2031, 2, 3, 4, 5, 6, 789, TimeSpan.Zero));

        Assert.Equal(0, result.ExitCode);
        // nightly-qualification.test.mjs's sealOf: the digest of JSON.stringify of the record without its seal.
        var value = (JsObject)JsJson.Parse(await System.IO.File.ReadAllTextAsync(Path.Combine(_harness.Workspace, parameters["output"]), TestContext.Current.CancellationToken))!;
        var seal = (JsObject)value["seal"]!;
        value.Remove("seal");
        Assert.Equal("2031-02-03T04:05:06.789Z", value["generatedAt"]);
        Assert.Equal("sha256", seal["algorithm"]);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsJson.Stringify(value)!))), seal["digest"]);
    }

    [Fact]
    public async Task TheAssuranceGradeParameter_StandsForTheVariable()
    {
        var testCase = ScriptReferenceData.Case(File, "extensions-and-grade");
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);
        var parameters = AssuranceReferenceData.Strings(testCase.GetProperty("parameters"));
        parameters["assurance_grade"] = "C";
        var instant = DateTimeOffset.Parse(testCase.GetProperty("instant").GetString()!, CultureInfo.InvariantCulture);

        var result = await RunAsync(parameters, new() { ["CANDIDATE_ASSURANCE_GRADE"] = "A" }, instant);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            Encoding.UTF8.GetBytes(testCase.GetProperty("output").GetString()!),
            await System.IO.File.ReadAllBytesAsync(Path.Combine(_harness.Workspace, parameters["output"]), TestContext.Current.CancellationToken));
    }

    private async Task<ExecutorResult> RunAsync(Dictionary<string, string> parameters, Dictionary<string, string> environment, DateTimeOffset instant)
    {
        environment[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters);
        return (await _harness.RunAsync(new RecordNightlyQualificationFunction(new FakeTimeProvider(instant)), environment)).Result;
    }
}
