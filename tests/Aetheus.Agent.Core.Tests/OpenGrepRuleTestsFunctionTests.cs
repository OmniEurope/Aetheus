// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The <c>opengrep-rule-tests</c> function, run through the real executor on a temp directory unique to
/// each test: the refusals before anything runs, the verified binary, the sandboxed <c>docker run</c>
/// it builds, and the verdict it draws from the report of <c>opengrep scan --test --json</c>.
/// </summary>
public sealed class OpenGrepRuleTestsFunctionTests : IDisposable
{
    private static readonly byte[] BinaryBytes = Encoding.UTF8.GetBytes("fake opengrep binary");
    private const string RuntimeImage = "debian:bookworm-slim@sha256:7b140f374b289a7c2befc338f42ebe6441b7ea838a042bbd5acbfca6ec875818";

    private readonly FunctionTestHarness _harness = new("aetheus-opengrep-tests");
    private readonly FakeFunctionTools _tools = new() { IsLinuxX64 = true, DownloadContent = BinaryBytes };
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private ScannerManifestEntry? _scanner = Scanner(Convert.ToHexStringLower(SHA256.HashData(BinaryBytes)), RuntimeImage);
    private string[] _stdout = [];
    private int _exitCode;
    private readonly List<string> _stagedFilesSeen = [];

    public OpenGrepRuleTestsFunctionTests()
    {
        _harness.Executable("bin/docker");
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["AETHEUS_AGENT_WORK_DIRECTORY"] = Directory.CreateDirectory(Path.Combine(_harness.Root, "work")).FullName;
        _harness.Write("ws/.aetheus/security-rules/opengrep/aetheus-example.yml", "rules: []\n");
        _harness.Write("ws/tests/security-rules/aetheus-example.cs", "// ruleid: aetheus.example\n");
        _tools.OnRun = (process, _) =>
        {
            foreach (var mount in Mounts(process))
                _stagedFilesSeen.AddRange(Directory.EnumerateFiles(mount.Source, "*", SearchOption.AllDirectories).Select(Path.GetFileName)!);
            return Task.FromResult((_exitCode, _stdout));
        };
    }

    public void Dispose() => _harness.Dispose();

    private static ScannerManifestEntry Scanner(string sha, string? runtimeImage) => new()
    {
        Key = "opengrep",
        Version = "1.22.0",
        Execution = "binary",
        DownloadUriLinuxAmd64 = "https://example.invalid/opengrep",
        Sha256LinuxAmd64 = sha,
        RuntimeImage = runtimeImage,
        Memory = "2g",
        Cpus = "2.0"
    };

    private string BinaryPath => Path.Combine(_harness.Root, "work", "scanners", "opengrep", "1.22.0", "opengrep");

    private Task<(ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(Dictionary<string, string>? with = null)
    {
        var env = new Dictionary<string, string>(_env, StringComparer.Ordinal);
        if (with is not null)
            env[PipelineFunctionVariables.ParametersJson] = System.Text.Json.JsonSerializer.Serialize(with);
        return _harness.RunAsync(new OpenGrepRuleTestsFunction(_tools, () => _scanner, () => "1000:1000"), env);
    }

    private static string FailureReason(ExecutorResult result)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    private static IEnumerable<(string Source, string Target, bool ReadOnly)> Mounts(ProcessStartInfo process)
    {
        var args = process.ArgumentList;
        for (var i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != "--mount" || !args[i + 1].Contains("dst=/rules", StringComparison.Ordinal) && !args[i + 1].Contains("dst=/tests", StringComparison.Ordinal))
                continue;
            var parts = args[i + 1].Split(',');
            yield return (parts[1]["src=".Length..], parts[2]["dst=".Length..], parts.Contains("readonly"));
        }
    }

    private static string Report(bool passed, string missingTests = "", string errors = "") =>
        $$"""
        {
          "config_missing_tests": [{{missingTests}}],
          "config_missing_fixtests": [],
          "config_with_errors": [{{errors}}],
          "results": {
            "/rules/aetheus-example.yml": {
              "checks": {
                "aetheus.example.one": {
                  "passed": true,
                  "matches": { "/tests/aetheus-example.cs": { "expected_lines": [4], "reported_lines": [4] } },
                  "errors": []
                },
                "aetheus.example.two": {
                  "passed": {{(passed ? "true" : "false")}},
                  "matches": { "/tests/aetheus-example.cs": { "expected_lines": [8, 12], "reported_lines": {{(passed ? "[8, 12]" : "[12, 15]")}} } },
                  "errors": []
                }
              }
            }
          }
        }
        """;

    [Fact]
    public async Task PassingRuleTests_RunTheVerifiedBinaryInTheSandboxAndSucceed()
    {
        _stdout = Report(passed: true).Split('\n');

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_harness.Log, entry => entry.Line == ">>> 2 OpenGrep rule tests passed across 1 rule files.");
        var process = Assert.Single(_tools.Runs);
        Assert.Equal(Path.Combine(_harness.Root, "bin", "docker"), process.FileName);
        var args = process.ArgumentList.ToList();
        Assert.Equal(["--network", "none"], args.Skip(args.IndexOf("--network")).Take(2));
        Assert.Equal(["--cap-drop", "ALL"], args.Skip(args.IndexOf("--cap-drop")).Take(2));
        Assert.Contains("--read-only", args);
        Assert.Equal(["--user", "1000:1000"], args.Skip(args.IndexOf("--user")).Take(2));
        Assert.Contains($"type=bind,src={BinaryPath},dst=/scanner/opengrep,readonly", args);
        Assert.Equal(["--", RuntimeImage, "scan", "--test", "--json", "--config", "/rules", "/tests"], args.TakeLast(8));
        var mounts = Mounts(process).ToList();
        Assert.Equal(["/rules", "/tests"], mounts.Select(mount => mount.Target));
        Assert.All(mounts, mount => Assert.True(mount.ReadOnly));
        // The copies live under the work directory, which the Docker daemon sees, and are gone after.
        Assert.All(mounts, mount => Assert.StartsWith(Path.Combine(_harness.Root, "work", "scans"), mount.Source, StringComparison.Ordinal));
        Assert.Equal(["aetheus-example.yml", "aetheus-example.cs"], _stagedFilesSeen);
        Assert.All(mounts, mount => Assert.False(Directory.Exists(mount.Source)));
        Assert.Equal(BinaryBytes, await File.ReadAllBytesAsync(BinaryPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AVerifiedBinaryAlreadyInTheWorkDirectory_IsNotDownloadedAgain()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(BinaryPath)!);
        await File.WriteAllBytesAsync(BinaryPath, BinaryBytes, TestContext.Current.CancellationToken);
        _stdout = Report(passed: true).Split('\n');

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task ADownloadThatDoesNotMatchThePinnedDigest_IsRefusedBeforeAnythingRuns()
    {
        _tools.DownloadContent = Encoding.UTF8.GetBytes("tampered");

        var (result, _) = await RunAsync();

        Assert.Equal("Function 'opengrep-rule-tests' failed: OpenGrep 1.22.0 is unavailable: Downloaded scanner binary failed SHA-256 verification.", FailureReason(result));
        Assert.Empty(_tools.Runs);
        Assert.False(File.Exists(BinaryPath));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(BinaryPath)!));
    }

    [Fact]
    public async Task AFailedRuleTest_FailsTheStepAndNamesTheLines()
    {
        _exitCode = 1;
        _stdout = Report(passed: false).Split('\n');

        var (result, _) = await RunAsync();

        Assert.Equal(
            "Function 'opengrep-rule-tests' failed: 1 of 2 OpenGrep rule tests failed, 0 rule file(s) without a test, 0 invalid rule file(s).",
            FailureReason(result));
        Assert.Contains(_harness.Log, entry => entry.Level == TaskLogLevel.Error && entry.Line ==
            "FAILED aetheus.example.two (/rules/aetheus-example.yml): /tests/aetheus-example.cs missed lines [8], unexpected lines [15]");
    }

    [Fact]
    public async Task ARuleFileWithoutTest_FailsTheStep()
    {
        _stdout = Report(passed: true, missingTests: "\"/rules/aetheus-untested.yml\"").Split('\n');

        var (result, _) = await RunAsync();

        Assert.Contains("1 rule file(s) without a test", FailureReason(result), StringComparison.Ordinal);
        Assert.Contains(_harness.Log, entry => entry.Line == "NO TEST /rules/aetheus-untested.yml: no test file carries its rules.");
    }

    [Fact]
    public async Task AnInvalidRuleFile_FailsTheStep()
    {
        _stdout = Report(passed: true, errors: "\"/rules/aetheus-broken.yml\"").Split('\n');

        var (result, _) = await RunAsync();

        Assert.Contains("1 invalid rule file(s)", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoRuleTestAtAll_IsAFailureNotAPass()
    {
        _stdout = ["""{ "config_missing_tests": [], "config_with_errors": [], "results": {} }"""];

        var (result, _) = await RunAsync();

        Assert.Contains("0 of 0 OpenGrep rule tests failed", FailureReason(result), StringComparison.Ordinal);
        Assert.Contains(_harness.Log, entry => entry.Line == "NO CHECK: OpenGrep ran no rule test at all.");
    }

    [Fact]
    public async Task OpenGrepStoppingWithoutAReport_FailsWithItsLastErrorLines()
    {
        _exitCode = 2;
        _stdout =
        [
            "[stderr] Found rule id mismatch - file=/tests/aetheus-example.cs 'ruleid' annotation with no YAML rule={'aetheus.example.three'}",
            "[stderr] Failing due to rule id mismatch."
        ];

        var (result, _) = await RunAsync();

        Assert.Equal(
            "Function 'opengrep-rule-tests' failed: OpenGrep exited with code 2 without a rule test report: Found rule id mismatch - file=/tests/aetheus-example.cs 'ruleid' annotation with no YAML rule={'aetheus.example.three'} Failing due to rule id mismatch.",
            FailureReason(result));
    }

    [Fact]
    public async Task APassingReportWithANonZeroExit_IsNotTrusted()
    {
        _exitCode = 3;
        _stdout = [.. Report(passed: true).Split('\n'), "[stderr] unexpected crash"];

        var (result, _) = await RunAsync();

        Assert.Equal(
            "Function 'opengrep-rule-tests' failed: OpenGrep reported every rule test passed but exited with code 3: unexpected crash",
            FailureReason(result));
    }

    [Fact]
    public async Task CustomDirectories_AreTheOnesMounted()
    {
        _harness.Write("ws/rules-x/one.yml", "rules: []\n");
        _harness.Write("ws/corpus/one.cs", "// ok\n");
        _stdout = Report(passed: true).Split('\n');

        var (result, _) = await RunAsync(new() { ["rules"] = "rules-x", ["tests"] = "corpus" });

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["one.yml", "one.cs"], _stagedFilesSeen);
    }

    [Theory]
    [InlineData("rules", "../outside", "The rules directory '../outside' must stay inside the workspace.")]
    [InlineData("tests", "../outside", "The tests directory '../outside' must stay inside the workspace.")]
    [InlineData("tests", "missing", "The tests directory 'missing' does not exist in the workspace.")]
    public async Task ADirectoryOutsideTheWorkspaceOrMissing_IsRefused(string parameter, string value, string reason)
    {
        Directory.CreateDirectory(Path.Combine(_harness.Root, "outside"));

        var (result, _) = await RunAsync(new() { [parameter] = value });

        Assert.Equal($"Function 'opengrep-rule-tests' failed: {reason}", FailureReason(result));
        Assert.Empty(_tools.Runs);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task AnAbsoluteDirectory_IsRefused()
    {
        var absolute = Directory.CreateDirectory(Path.Combine(_harness.Root, "abs")).FullName;

        var (result, _) = await RunAsync(new() { ["rules"] = absolute });

        Assert.Equal($"Function 'opengrep-rule-tests' failed: The rules directory '{absolute}' must stay inside the workspace.", FailureReason(result));
    }

    [Fact]
    public async Task AnAgentThatIsNotLinuxX64_IsRefused()
    {
        _tools.IsLinuxX64 = false;

        var (result, _) = await RunAsync();

        Assert.Contains("only on a Linux x86_64 agent", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task AManifestWithoutOpenGrep_IsRefused()
    {
        _scanner = null;

        var (result, _) = await RunAsync();

        Assert.Contains("OpenGrep is absent from the scanner manifest", FailureReason(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("abc", RuntimeImage, "OpenGrep refused: Binary scanner manifest entry is incomplete.")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "debian:bookworm-slim", "OpenGrep refused: Binary scanner runtime image is not pinned by digest.")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, "OpenGrep refused: Binary scanner runtime image is not pinned by digest.")]
    public async Task AnUnpinnedManifestEntry_IsRefusedBeforeAnyDownload(string sha, string? image, string reason)
    {
        _scanner = Scanner(sha, image);

        var (result, _) = await RunAsync();

        Assert.Equal($"Function 'opengrep-rule-tests' failed: {reason}", FailureReason(result));
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task NoWorkDirectory_IsRefused()
    {
        _env.Remove("AETHEUS_AGENT_WORK_DIRECTORY");

        var (result, _) = await RunAsync();

        Assert.Contains("AETHEUS_AGENT_WORK_DIRECTORY is missing or unavailable", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoDockerOnThePath_IsRefused()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "empty");

        var (result, _) = await RunAsync();

        Assert.Equal("Function 'opengrep-rule-tests' failed: docker is required to run OpenGrep in its sandbox.", FailureReason(result));
    }

    [Fact]
    public void TheCatalogDeclaresTheFunctionAsABuildFunctionWithTwoOptionalDirectories()
    {
        var function = PipelineFunctionCatalog.Find("opengrep-rule-tests");

        Assert.NotNull(function);
        Assert.Equal(PipelineFunctionAccess.Build, function.Access);
        Assert.Equal(["rules", "tests"], function.Parameters.Select(parameter => parameter.Name));
        Assert.DoesNotContain(function.Parameters, parameter => parameter.Required);
        Assert.Empty(function.Outputs);
        Assert.Contains(PipelineFunctions.CreateAll(), implementation => implementation.Name == function.Name);
    }
}
