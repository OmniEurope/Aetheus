// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>nightly-evidence.mjs</c> script, run through the real executor on every
/// sequence of runs the script itself performed (<c>TestData/payload-evidence-cases.json</c>,
/// <see cref="ScriptReferenceData"/>): the case of <c>nightly-evidence.test.mjs</c>, the collation of
/// names, duplicate, empty, directory and missing artifacts, the usage refusals and evidence of every
/// shape on verify. Each create must write the script's evidence byte for byte, which is what lets the
/// script's verify (still run by the demo prepare step of <c>aetheus-demo.yaml</c>) accept it; each run must log the
/// script's line or fail with its message verbatim.
/// </summary>
public sealed class PayloadEvidenceFunctionTests : IDisposable
{
    private const string File = "payload-evidence-cases.json";
    private const string Prefix = "Function 'payload-evidence' failed: ";
    private const string Source = "0123456789abcdef0123456789abcdef01234567";
    private readonly FunctionTestHarness _harness = new("aetheus-payload-evidence");

    public void Dispose() => _harness.Dispose();

    public static TheoryData<string> Cases() => ScriptReferenceData.CaseNames(File);

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task EveryRun_WritesLogsOrRefusesLikeTheScript(string name)
    {
        var testCase = ScriptReferenceData.Case(File, name);
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "out"));
        AssuranceReferenceData.LayOut(testCase.GetProperty("files"), _harness.Workspace);

        foreach (var step in testCase.GetProperty("steps").EnumerateArray())
        {
            if (!step.TryGetProperty("arguments", out var arguments))
            {
                AssuranceReferenceData.LayOut(step.GetProperty("write"), _harness.Workspace);
                continue;
            }
            var values = arguments.EnumerateArray().Select(value => value.GetString()!).ToArray();
            _harness.Log.Clear();
            var result = await RunAsync(Parameters(values));
            AssertRun(testCase, step, values, result);
        }
    }

    [Fact]
    public void TheReferenceData_CoversTheScriptTest()
    {
        var names = ScriptReferenceData.CaseNames(File).Select(row => row.Data).ToHashSet();

        Assert.Subset(names, new HashSet<string>
        {
            // nightly-evidence.test.mjs
            "nightly-evidence-test",
            "locale-order", "duplicate-name", "empty-artifact", "directory-artifact", "missing-artifact", "usage",
            "verify-kind-schema", "verify-shapes", "binary-content"
        });
    }

    [Fact]
    public async Task EvidenceTheScriptCreated_IsVerified()
    {
        var create = ScriptReferenceData.Case(File, "locale-order").GetProperty("steps")[0];
        AssuranceReferenceData.LayOut(ScriptReferenceData.Case(File, "locale-order").GetProperty("files"), _harness.Workspace);
        _harness.Write("ws/script-evidence.json", create.GetProperty("evidence").GetString()!);

        var result = await RunAsync(new()
        {
            ["mode"] = "verify",
            ["source_sha"] = Source,
            ["evidence"] = "script-evidence.json",
            ["artifacts"] = "Z a~b.bin y/ab.dat x/Ab.txt 9.bin 10.bin a-b.bin _x.bin a.bin B.bin"
        });

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task TheKindParameter_IsWrittenAndRequired()
    {
        _harness.Write("ws/a.bin", "first");
        Dictionary<string, string> Run(string mode, string? kind)
        {
            var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["mode"] = mode,
                ["source_sha"] = Source,
                ["evidence"] = "evidence.json",
                ["artifacts"] = "a.bin"
            };
            if (kind is not null)
                parameters["kind"] = kind;
            return parameters;
        }

        Assert.Equal(0, (await RunAsync(Run("create", "release-payload"))).ExitCode);
        var written = JsonDocument.Parse(await System.IO.File.ReadAllBytesAsync(Path.Combine(_harness.Workspace, "evidence.json"), TestContext.Current.CancellationToken));
        Assert.Equal("release-payload", written.RootElement.GetProperty("kind").GetString());
        Assert.Equal(0, (await RunAsync(Run("verify", "release-payload"))).ExitCode);
        Assert.Equal(Prefix + "The nightly evidence schema or kind is invalid.", (await RunAsync(Run("verify", null))).FailureReason);
    }

    [Fact]
    public async Task CreatedEvidence_HasMode0644OnUnix()
    {
        if (OperatingSystem.IsWindows())
            return;
        _harness.Write("ws/a.bin", "first");

        var result = await RunAsync(new() { ["mode"] = "create", ["source_sha"] = Source, ["evidence"] = "evidence.json", ["artifacts"] = "a.bin" });

        Assert.Equal(0, result.ExitCode);
        // 0644 under the umask, as open(2) applies it to the script's { mode: 0o644 }: never more.
        var mode = System.IO.File.GetUnixFileMode(Path.Combine(_harness.Workspace, "evidence.json"));
        Assert.Equal((UnixFileMode)0, mode & ~(UnixFileMode)0b110_100_100);
        Assert.True(mode.HasFlag(UnixFileMode.UserRead) && mode.HasFlag(UnixFileMode.UserWrite));
    }

    private void AssertRun(JsonElement testCase, JsonElement step, string[] values, ExecutorResult result)
    {
        if (values.Length < 4)
        {
            // No artifact: the request validation refuses the empty required parameter before the function runs.
            Assert.Equal("Function 'payload-evidence' requires the parameter 'artifacts'.", result.FailureReason);
            return;
        }
        if (testCase.TryGetProperty("difference", out _))
        {
            Assert.StartsWith(Prefix + "localeCompare is only reproduced for ASCII text", result.FailureReason, StringComparison.Ordinal);
            return;
        }
        if (step.TryGetProperty("error", out var error))
        {
            Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
            AssertError(error, values, result.FailureReason!);
            return;
        }
        Assert.Equal(0, result.ExitCode);
        Assert.Contains((step.GetProperty("stdout").GetString()!.TrimEnd('\n'), TaskLogLevel.Info), _harness.Log);
        if (step.TryGetProperty("evidence", out var evidence))
            Assert.Equal(
                Encoding.UTF8.GetBytes(evidence.GetString()!),
                System.IO.File.ReadAllBytes(Path.Combine(_harness.Workspace, values[2])));
    }

    private static void AssertError(JsonElement error, string[] values, string reason)
    {
        var message = error.GetProperty("message").GetString()!;
        if (error.TryGetProperty("code", out _) && message.Contains(", stat '", StringComparison.Ordinal))
        {
            // Node named the resolved path (recorded on Windows); the port names the path as given.
            var missing = values.Skip(3).Single(path => message.EndsWith(path + "'", StringComparison.Ordinal));
            Assert.Equal($"{Prefix}ENOENT: no such file or directory, stat '{missing}'", reason);
        }
        else if (error.TryGetProperty("code", out _))
        {
            // The evidence could not be opened: the .NET message, not Node's.
            Assert.StartsWith(Prefix, reason, StringComparison.Ordinal);
        }
        else if (error.GetProperty("name").GetString() == "SyntaxError")
        {
            // JSON.parse refused the evidence; the parser's message is not V8's.
            Assert.StartsWith(Prefix, reason, StringComparison.Ordinal);
        }
        else
        {
            Assert.Equal(Prefix + message, reason);
        }
    }

    private static Dictionary<string, string> Parameters(string[] values) => new(StringComparer.Ordinal)
    {
        ["mode"] = values[0],
        ["source_sha"] = values[1],
        ["evidence"] = values[2],
        ["artifacts"] = string.Join(' ', values.Skip(3))
    };

    private async Task<ExecutorResult> RunAsync(Dictionary<string, string> parameters)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters)
        };
        return (await _harness.RunAsync(new PayloadEvidenceFunction(), env)).Result;
    }
}
