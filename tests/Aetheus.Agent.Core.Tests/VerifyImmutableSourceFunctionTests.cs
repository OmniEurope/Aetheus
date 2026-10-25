// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>verify-immutable-source.sh</c> script, run through the real
/// executor with Git answered by a fake: the HEAD check, the unshallow in full mode and the refusal of
/// a history that stays shallow, with the Git commands in the script's order.
/// </summary>
public sealed class VerifyImmutableSourceFunctionTests : IDisposable
{
    private const string Head = "2222222222222222222222222222222222222222";

    private readonly FunctionTestHarness _harness = new("aetheus-immutable-source");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public VerifyImmutableSourceFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["BUILD_SOURCEVERSION"] = Head;
        _env["AETHEUS_GITLEAKS_MODE"] = "";
        _tools.OnRun = Answer;
    }

    public void Dispose() => _harness.Dispose();

    private string ActualHead { get; set; } = Head;
    private bool Shallow { get; set; }
    private int FetchExit { get; set; }
    private bool FetchDeepens { get; set; } = true;

    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var arguments = string.Join(' ', process.ArgumentList);
        (int, string[]) answer = arguments switch
        {
            "rev-parse HEAD" => (0, [ActualHead]),
            "rev-parse --is-shallow-repository" => (0, [Shallow ? "true" : "false"]),
            "fetch --unshallow origin" => Fetch(),
            _ => (127, [])
        };
        return Task.FromResult(answer);
    }

    private (int, string[]) Fetch()
    {
        if (FetchExit == 0 && FetchDeepens)
            Shallow = false;
        return (FetchExit, ["fetched"]);
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new VerifyImmutableSourceFunction(_tools), _env);
    }

    private IEnumerable<string> GitCommands => _tools.Runs.Select(run => string.Join(' ', run.ArgumentList));

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    [Fact]
    public async Task ReleaseRangeMode_OnlyChecksHead()
    {
        Shallow = true;

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["rev-parse HEAD"], GitCommands);
        Assert.All(_tools.Runs, run => Assert.Equal(_harness.Workspace, run.WorkingDirectory));
    }

    [Fact]
    public async Task FullModeOnAShallowCheckout_UnshallowsThenVerifies()
    {
        Shallow = true;
        _env["AETHEUS_GITLEAKS_MODE"] = "full";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["rev-parse HEAD", "rev-parse --is-shallow-repository", "fetch --unshallow origin", "rev-parse --is-shallow-repository"],
            GitCommands);
    }

    [Fact]
    public async Task FullModeOnACompleteCheckout_FetchesNothing()
    {
        _parameters["mode"] = "full";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["rev-parse HEAD", "rev-parse --is-shallow-repository", "rev-parse --is-shallow-repository"], GitCommands);
    }

    [Fact]
    public async Task ModeOtherThanFull_IsTreatedAsReleaseRangeLikeTheScript()
    {
        Shallow = true;
        _env["AETHEUS_GITLEAKS_MODE"] = "FULL";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("fetch --unshallow origin", GitCommands);
    }

    [Fact]
    public async Task HeadOtherThanTheRequestedRevision_IsRefused()
    {
        ActualHead = "3333333333333333333333333333333333333333";

        Assert.EndsWith(
            $"The checkout is at {ActualHead}, not at the requested revision {Head}.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HeadParameter_WinsOverBuildSourceVersion()
    {
        ActualHead = "4444444444444444444444444444444444444444";
        _parameters["head"] = ActualHead;

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task MissingRevision_IsRefusedBeforeAnyGitCommand()
    {
        _env["BUILD_SOURCEVERSION"] = "";

        Assert.EndsWith("BUILD_SOURCEVERSION is not set: the revision to verify is unknown.", await FailureAsync(), StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public async Task FailedUnshallow_FailsTheStep()
    {
        Shallow = true;
        FetchExit = 128;
        _env["AETHEUS_GITLEAKS_MODE"] = "full";

        Assert.EndsWith("git fetch --unshallow origin failed with exit code 128.", await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryStillShallowAfterTheFetch_IsRefused()
    {
        Shallow = true;
        FetchDeepens = false;
        _env["AETHEUS_GITLEAKS_MODE"] = "full";

        Assert.EndsWith(
            "Full mode requires the complete history, but the checkout is still shallow.", await FailureAsync(), StringComparison.Ordinal);
    }
}
