// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/qa/persist-compatibility-mode.sh</c>, run through the real executor
/// on a temp workspace unique to each test: where each status is read from, how it is normalized, the
/// mode and verdict in bootstrap and N/N-1, and the refusals on a missing V-1 commit.
/// </summary>
public sealed class PersistCompatibilityModeFunctionTests : IDisposable
{
    private const string Previous = "0123456789abcdef0123456789abcdef01234567";

    private static readonly string[] Gates =
        ["current-integration", "current-e2e", "previous-smoke", "previous-integration", "previous-e2e"];

    private readonly FunctionTestHarness _harness = new("aetheus-compat-mode");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public void Dispose() => _harness.Dispose();

    private void Status(string gate, string content) => _harness.Write($"ws/.qa-{gate}-gate-status", content);

    private void AllStatuses(string content)
    {
        foreach (var gate in Gates)
            Status(gate, content);
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync() =>
        _harness.RunAsync(new PersistCompatibilityModeFunction(), _env);

    private async Task<Dictionary<string, string>> SuccessAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.Equal(0, result.ExitCode);
        return variables;
    }

    private async Task<string> FailureAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Empty(variables);
        return result.FailureReason!;
    }

    [Fact]
    public async Task Bootstrap_AttestsTheCurrentVersionAlone()
    {
        AllStatuses("0\n");
        _harness.Write("ws/.qa-bootstrap-mode", "1\n");

        var variables = await SuccessAsync();

        Assert.Equal("Bootstrap", variables["COMPATIBILITY_MODE"]);
        Assert.Equal("false", variables["PREVIOUS_VERSION_TESTED"]);
        Assert.Equal("CurrentVersionVerified", variables["COMPATIBILITY_VERDICT"]);
    }

    [Theory]
    [InlineData("current-integration")]
    [InlineData("current-e2e")]
    public async Task Bootstrap_WithACurrentFinding_IsDegraded(string gate)
    {
        AllStatuses("0\n");
        Status(gate, "1\n");
        _harness.Write("ws/.qa-bootstrap-mode", "1\n");

        Assert.Equal("CurrentVersionDegraded", (await SuccessAsync())["COMPATIBILITY_VERDICT"]);
    }

    [Fact]
    public async Task NMinusOne_AllPassing_IsVerifiedAndNamesTheV1Commit()
    {
        AllStatuses("0\n");
        _harness.Write("ws/.qa-bootstrap-mode", "0\n");
        _harness.Write("ws/.qa-previous-commit", Previous + "\n");

        var variables = await SuccessAsync();

        Assert.Equal("NMinusOne", variables["COMPATIBILITY_MODE"]);
        Assert.Equal(Previous, variables["PREVIOUS_VERSION_TESTED"]);
        Assert.Equal("NMinusOneVerified", variables["COMPATIBILITY_VERDICT"]);
        Assert.All(Gates, gate => Assert.Equal("0", variables[$"QA_{gate.Replace('-', '_').ToUpperInvariant()}_GATE_STATUS"]));
    }

    [Theory]
    [InlineData("current-integration")]
    [InlineData("current-e2e")]
    [InlineData("previous-smoke")]
    [InlineData("previous-integration")]
    [InlineData("previous-e2e")]
    public async Task NMinusOne_AnyFinding_IsDegraded(string gate)
    {
        AllStatuses("0\n");
        Status(gate, "1\n");
        _harness.Write("ws/.qa-bootstrap-mode", "0\n");
        _harness.Write("ws/.qa-previous-commit", Previous + "\n");

        Assert.Equal("NMinusOneDegraded", (await SuccessAsync())["COMPATIBILITY_VERDICT"]);
    }

    [Theory]
    [InlineData("0", "0")]
    [InlineData("1", "1")]
    [InlineData("2", "1")]
    [InlineData("0\r", "1")]
    [InlineData(" 0", "1")]
    [InlineData("\n", "1")]
    public async Task Status_IsZeroOnlyForExactlyZero(string content, string expected)
    {
        AllStatuses("0\n");
        Status("current-e2e", content);
        _harness.Write("ws/.qa-bootstrap-mode", "1\n");

        Assert.Equal(expected, (await SuccessAsync())["QA_CURRENT_E2E_GATE_STATUS"]);
    }

    [Fact]
    public async Task Status_FallsBackToTheVariableThenToFailure()
    {
        _harness.Write("ws/.qa-bootstrap-mode", "1\n");
        _harness.Write("ws/.qa-current-integration-gate-status", string.Empty);
        _env["QA_CURRENT_INTEGRATION_GATE_STATUS"] = "0";
        _env["QA_CURRENT_E2E_GATE_STATUS"] = "0";
        _env["QA_PREVIOUS_SMOKE_GATE_STATUS"] = string.Empty;
        Status("previous-integration", "1\n");
        _env["QA_PREVIOUS_INTEGRATION_GATE_STATUS"] = "0";

        var variables = await SuccessAsync();

        Assert.Equal("0", variables["QA_CURRENT_INTEGRATION_GATE_STATUS"]);
        Assert.Equal("0", variables["QA_CURRENT_E2E_GATE_STATUS"]);
        Assert.Equal("1", variables["QA_PREVIOUS_SMOKE_GATE_STATUS"]);
        Assert.Equal("1", variables["QA_PREVIOUS_INTEGRATION_GATE_STATUS"]);
        Assert.Equal("1", variables["QA_PREVIOUS_E2E_GATE_STATUS"]);
        Assert.Equal("CurrentVersionVerified", variables["COMPATIBILITY_VERDICT"]);
    }

    [Fact]
    public async Task MissingBootstrapMarker_ReadsAsNMinusOne()
    {
        AllStatuses("0\n");
        _harness.Write("ws/.qa-previous-commit", Previous);

        var variables = await SuccessAsync();

        Assert.Equal("NMinusOne", variables["COMPATIBILITY_MODE"]);
        Assert.Contains(_harness.Log, entry => entry.Level == TaskLogLevel.Warning && entry.Line.Contains(".qa-bootstrap-mode is missing", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NMinusOne_WithoutTheV1CommitFile_FailsAndPublishesNothing()
    {
        _harness.Write("ws/.qa-bootstrap-mode", "0\n");

        Assert.EndsWith(".qa-previous-commit is missing: the V-1 commit this run tested is unknown.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n")]
    public async Task NMinusOne_WithAnEmptyV1Commit_Fails(string content)
    {
        _harness.Write("ws/.qa-bootstrap-mode", "0\n");
        _harness.Write("ws/.qa-previous-commit", content);

        Assert.EndsWith(".qa-previous-commit is empty: the V-1 commit this run tested is unknown.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NMinusOne_WithAV1CommitOnTwoLines_Fails()
    {
        _harness.Write("ws/.qa-bootstrap-mode", "0\n");
        _harness.Write("ws/.qa-previous-commit", "abc\ndef\n");

        Assert.EndsWith("holds more than one line; it must name one V-1 commit.", await FailureAsync(), StringComparison.Ordinal);
    }
}
