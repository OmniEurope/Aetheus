// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the immutable-image path of the former <c>run-qa-e2e-suite.sh</c> script, run through the
/// real executor against a Docker CLI stand-in whose <c>docker cp</c> delivers the suite container's
/// results: the argument refusals, the image revision check, the network and readiness, the suite
/// container's environment, the classification of the raw exit code against the TRX, the skipped and
/// failed tests, and the containers removed on every path.
/// </summary>
public sealed class RunQaE2eSuiteFunctionTests : IDisposable
{
    private const string Commit = "0123456789abcdef0123456789abcdef01234567";
    private const string Image = "aetheus-browser-smoke:" + Commit;

    private readonly FunctionTestHarness _harness = new("aetheus-qa-e2e");
    private readonly FakeFunctionTools _tools = new();
    private readonly List<string[]> _docker = [];
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["label"] = "qa-42-current",
        ["front"] = "aetheus-qa-rollback-42-front",
        ["back"] = "aetheus-qa-rollback-42-back",
        ["admin_password_file"] = ".qa-admin-pwd",
        ["image"] = Image,
        ["expected_commit"] = Commit,
        ["test_project"] = "tests/Aetheus.E2E/Aetheus.E2E.csproj",
        ["preserve_database"] = "true"
    };

    private string _revision = Commit;
    private string[] _networks = ["aetheus-qa-rollback-42_backend", "aetheus-qa-rollback-42_default"];
    private int _readyExit;
    private int _runnerExit;
    private string? _rawExit = "0\n";
    private string _trx = Trx(("E2E.Home", "Passed"));
    private string _rerunTrx = string.Empty;
    private string? _rerunRawExit;

    public RunQaE2eSuiteFunctionTests()
    {
        _harness.Write("ws/.qa-admin-pwd", "secretpwd\n");
        _tools.OnRun = Answer;
    }

    public void Dispose() => _harness.Dispose();

    private string Results => Path.Combine(_harness.Workspace, ".qa-test-results", "qa-42-current", "e2e");

    private static string Trx(params (string Name, string Outcome)[] results)
    {
        var failed = results.Count(result => result.Outcome == "Failed");
        var skipped = results.Count(result => result.Outcome == "NotExecuted");
        var rows = string.Concat(results.Select(result => $"<UnitTestResult testName=\"{result.Name}\" outcome=\"{result.Outcome}\" />"));
        return $"<TestRun><Results>{rows}</Results><ResultSummary outcome=\"{(failed > 0 ? "Failed" : "Completed")}\">"
               + $"<Counters total=\"{results.Length}\" executed=\"{results.Length - skipped}\" passed=\"{results.Length - failed - skipped}\" "
               + $"failed=\"{failed}\" error=\"0\" timeout=\"0\" aborted=\"0\" disconnected=\"0\" notExecuted=\"{skipped}\" /></ResultSummary></TestRun>";
    }

    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var arguments = process.ArgumentList.ToArray();
        lock (_docker)
            _docker.Add(arguments);
        return Task.FromResult(arguments switch
        {
            ["image", "inspect", "--format", _, Image] => (0, new[] { _revision }),
            ["inspect", "-f", _, "aetheus-qa-rollback-42-front"] => (0, _networks),
            ["run", "--rm", "--name", "aetheus-qa-42-current-ready", ..] => (_readyExit, Array.Empty<string>()),
            ["run", "--name", "aetheus-qa-42-current-tests", ..] => (_runnerExit, new[] { "suite output" }),
            ["cp", "aetheus-qa-42-current-tests:/tmp/aetheus-e2e-results/.", var destination] => Copy(destination),
            ["rm", "-f", ..] or ["ps"] => (0, Array.Empty<string>()),
            _ => (127, Array.Empty<string>())
        });
    }

    private (int, string[]) Copy(string destination)
    {
        // The rerun's results are copied into their own directory beside the first attempt's.
        var rerun = Path.GetFileName(destination.TrimEnd('/', '\\')) == "rerun";
        File.WriteAllText(Path.Combine(destination, "e2e.trx"), rerun ? _rerunTrx : _trx);
        if ((rerun ? _rerunRawExit : _rawExit) is { } rawExit)
            File.WriteAllText(Path.Combine(destination, "raw-exit-code"), rawExit);
        return (0, []);
    }

    private string[][] Runners => _docker.Where(arguments => arguments is ["run", "--name", ..]).ToArray();

    private static string Login(string outcome) =>
        FailedTestRerunTests.Trx(("Aetheus.E2E.AuthTests", "Login", "Login", outcome));

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new RunQaE2eSuiteFunction(_tools), _env);
    }

    private string[]? Runner => _docker.FirstOrDefault(arguments => arguments is ["run", "--name", ..]);

    private bool ContainersRemoved =>
        _docker.Any(arguments => arguments.SequenceEqual(["rm", "-f", "aetheus-qa-42-current-ready", "aetheus-qa-42-current-tests"]));

    [Fact]
    public async Task ACleanSuite_RunsOnTheStackNetworkWithTheScriptsEnvironment()
    {
        _env["AETHEUS_E2E_WORKERS"] = "2";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", variables["E2E_SUITE_STATUS"]);
        var ready = _docker.Single(arguments => arguments is ["run", "--rm", ..]);
        Assert.Equal("aetheus-qa-rollback-42_default", ready[5]);
        Assert.Contains("curl -sf \"http://aetheus-qa-rollback-42-front:8080/\"", ready[^1], StringComparison.Ordinal);
        var runner = Runner!;
        Assert.Contains("E2E_BACKEND_URL=http://aetheus-qa-rollback-42-back:8080", runner);
        Assert.Contains("E2E_ADMIN_PASSWORD=secretpwd", runner);
        Assert.Contains("E2E_PRESERVE_DATABASE=true", runner);
        Assert.Contains("E2E_WORKERS=2", runner);
        Assert.Contains("E2E_PREVIOUS_PAYLOAD_ONLY=false", runner);
        Assert.Contains("E2E_TEST_FILTER=", runner);
        Assert.Contains("E2E_TEST_PROJECT=tests/Aetheus.E2E/Aetheus.E2E.csproj", runner);
        Assert.Equal(Image, runner[^4]);
        Assert.True(ContainersRemoved);
        Assert.Contains(_harness.Log, entry => entry.Line == "E2E suite qa-42-current produced an executed-test proof against aetheus-qa-rollback-42-back with findings status 0, 0 test(s) skipped.");
    }

    /// <summary>QA run 1278: compiled from a Windows checkout, the raw-string script carried CRLF and the
    /// container's dash stopped on "set: Illegal option -". The text handed to <c>sh -c</c> is LF only.</summary>
    [Fact]
    public async Task TheRunnerScript_ReachesShWithoutACarriageReturn()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var script = Runner![^1];
        Assert.Equal("sh", Runner[^3]);
        Assert.Equal("-c", Runner[^2]);
        Assert.StartsWith("set -u\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', script);
    }

    [Fact]
    public async Task FailedAndSkippedTests_AreNamedAndStatusOne()
    {
        _trx = Trx(("E2E.Home", "Passed"), ("E2E.Login", "Failed"), ("E2E.NewFeature", "NotExecuted"));
        _rawExit = "1\r\n";
        _parameters["previous_payload_only"] = "true";
        _parameters["test_filter"] = "Category=RollbackRelevant";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["E2E_SUITE_STATUS"]);
        Assert.Equal("E2E.Login\n", File.ReadAllText(Path.Combine(Results, "failed-tests.txt")));
        Assert.Contains(_harness.Log, entry => entry.Line.EndsWith("with findings status 1, 1 test(s) skipped.", StringComparison.Ordinal));
        Assert.Contains(_harness.Log, entry => entry.Line == "  skipped: testName=\"E2E.NewFeature\"");
        Assert.Contains("E2E_TEST_FILTER=Category=RollbackRelevant", Runner!);
        Assert.Contains("E2E_PREVIOUS_PAYLOAD_ONLY=true", Runner!);
    }

    [Fact]
    public async Task AFailureThatPassesOnItsRerun_IsFlakyNamedAndStillFailsTheSuite()
    {
        _trx = FailedTestRerunTests.Trx(
            ("Aetheus.E2E.HomeTests", "Loads", "Loads", "Passed"),
            ("Aetheus.E2E.AuthTests", "Login", "Login", "Failed"));
        _rawExit = "1\n";
        _rerunTrx = Login("Passed");
        _rerunRawExit = "0\n";
        _parameters["test_filter"] = "Category=RollbackRelevant";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["E2E_SUITE_STATUS"]);
        // The rerun is the same suite container (image, network, environment) with only the filter changed.
        Assert.Equal(2, Runners.Length);
        Assert.Contains("E2E_TEST_FILTER=Category=RollbackRelevant", Runners[0]);
        var filter = "E2E_TEST_FILTER=FullyQualifiedName=Aetheus.E2E.AuthTests.Login";
        Assert.Equal(Runners[0].Select(argument => argument.StartsWith("E2E_TEST_FILTER=", StringComparison.Ordinal) ? filter : argument), Runners[1]);
        Assert.Equal(2, _docker.Count(arguments => arguments is ["run", "--rm", "--name", "aetheus-qa-42-current-ready", ..]));
        var firstRunner = _docker.FindIndex(arguments => arguments is ["run", "--name", ..]);
        var removedBeforeRerun = _docker.FindIndex(arguments => arguments.SequenceEqual(["rm", "-f", "aetheus-qa-42-current-tests"]));
        Assert.InRange(removedBeforeRerun, firstRunner + 1, _docker.FindLastIndex(arguments => arguments is ["run", "--name", ..]) - 1);
        // Both attempts are kept as evidence.
        Assert.Equal(_trx, File.ReadAllText(Path.Combine(Results, "e2e.trx")));
        Assert.Equal(_rerunTrx, File.ReadAllText(Path.Combine(Results, "rerun", "e2e.trx")));
        Assert.Equal("Login\n", File.ReadAllText(Path.Combine(Results, "flaky-tests.txt")));
        Assert.Equal("Login\n", File.ReadAllText(Path.Combine(Results, "failed-tests.txt")));
        Assert.Contains(_harness.Log, entry => entry.Line == "Flaky test in E2E suite qa-42-current: Login failed, then passed on its rerun.");
        Assert.Contains(_harness.Log, entry => entry.Line.EndsWith("with findings status 1, 0 test(s) skipped.", StringComparison.Ordinal));
        Assert.True(ContainersRemoved);
    }

    [Fact]
    public async Task AFailureThatFailsAgainOnItsRerun_StaysAFailureAndIsNotFlaky()
    {
        _trx = Login("Failed");
        _rawExit = "1\n";
        _rerunTrx = Login("Failed");
        _rerunRawExit = "1\n";

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["E2E_SUITE_STATUS"]);
        Assert.Equal(2, Runners.Length);
        Assert.Equal("Login\n", File.ReadAllText(Path.Combine(Results, "failed-tests.txt")));
        Assert.False(File.Exists(Path.Combine(Results, "flaky-tests.txt")));
        Assert.True(File.Exists(Path.Combine(Results, "rerun", "e2e.trx")));
        Assert.True(ContainersRemoved);
    }

    [Fact]
    public async Task ACleanSuite_IsNotRerun()
    {
        _trx = Login("Passed");

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", variables["E2E_SUITE_STATUS"]);
        Assert.Single(Runners);
        Assert.False(Directory.Exists(Path.Combine(Results, "rerun")));
        Assert.False(File.Exists(Path.Combine(Results, "flaky-tests.txt")));
    }

    [Fact]
    public async Task AnotherImageRevision_IsRefusedBeforeAnyContainer()
    {
        _revision = "ffffffffffffffffffffffffffffffffffffffff";

        var (result, _) = await RunAsync();

        Assert.EndsWith($"Immutable E2E image revision {_revision} does not match {Commit}.", result.FailureReason, StringComparison.Ordinal);
        Assert.Null(Runner);
    }

    [Fact]
    public async Task NoStackNetwork_FailsAndRemovesTheContainers()
    {
        _networks = ["aetheus-qa-rollback-42_backend"];

        var (result, _) = await RunAsync();

        Assert.EndsWith("Could not resolve the QA network for aetheus-qa-rollback-42-front.", result.FailureReason, StringComparison.Ordinal);
        Assert.Contains(_docker, arguments => arguments is ["ps"]);
        Assert.True(ContainersRemoved);
    }

    [Theory]
    [InlineData(1, 0, "did not answer on")]
    [InlineData(0, 125, "The E2E suite container aetheus-qa-42-current-tests failed with exit code 125.")]
    public async Task AFailedContainer_FailsTheStep(int readyExit, int runnerExit, string reason)
    {
        _readyExit = readyExit;
        _runnerExit = runnerExit;

        var (result, variables) = await RunAsync();

        Assert.Contains(reason, result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
        Assert.True(ContainersRemoved);
    }

    [Theory]
    [InlineData(null, "The E2E suite recorded no raw exit code")]
    [InlineData("0\n", "The E2E test evidence cannot be graded:")]
    [InlineData("x\n", "The E2E test evidence cannot be graded: The raw dotnet test exit code is invalid.")]
    public async Task UnusableEvidence_FailsTheStep(string? rawExit, string reason)
    {
        _rawExit = rawExit;
        _trx = Trx(("E2E.Login", "Failed"));

        var (result, _) = await RunAsync();

        Assert.Contains(reason, result.FailureReason, StringComparison.Ordinal);
        Assert.True(ContainersRemoved);
    }

    [Theory]
    [InlineData("label", "a b", "Invalid E2E run label.")]
    [InlineData("workers", "5", "AETHEUS_E2E_WORKERS must be 1 to 4.")]
    [InlineData("preserve_database", "yes", "Preserve-database flag must be true or false.")]
    [InlineData("previous_payload_only", "1", "E2E_PREVIOUS_PAYLOAD_ONLY must be true or false.")]
    [InlineData("test_filter", "FullyQualifiedName~X", "E2E_TEST_FILTER must be empty or Category=RollbackRelevant.")]
    [InlineData("expected_commit", "0123456789abcdef", "Expected E2E source commit must contain 40 hexadecimal characters.")]
    [InlineData("expected_commit", "0123456789abcdef0123456789abcdef0123456z", "Expected E2E source commit is not hexadecimal.")]
    [InlineData("admin_password_file", "missing", "The admin password file is missing or empty: ")]
    public async Task Arguments_AreRefusedBeforeDockerRuns(string parameter, string value, string reason)
    {
        _parameters[parameter] = value;

        var (result, _) = await RunAsync();

        Assert.Contains(reason, result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_docker);
    }
}
