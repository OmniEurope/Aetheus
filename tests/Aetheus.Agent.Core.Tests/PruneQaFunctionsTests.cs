// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# ports of the former <c>prune-qa-buildx-cache.sh</c> script and
/// the former <c>prune-stale-qa-compose.sh</c> script, run through the real executor against an in-memory
/// Docker daemon: the exact prune argv and its refusals, and the removal of earlier runs' Compose
/// resources with the current and newer runs protected.
/// </summary>
public sealed class PruneQaFunctionsTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-prune-qa");
    private readonly FakeFunctionTools _tools = new();
    private readonly FakeDockerHost _docker = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public PruneQaFunctionsTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _tools.OnRun = _docker.AnswerAsync;
    }

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(IPipelineFunction function)
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(function, _env);
    }

    private async Task<string> FailureAsync(IPipelineFunction function)
    {
        var (result, _) = await RunAsync(function);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        var prefix = $"Function '{function.Name}' failed: ";
        Assert.StartsWith(prefix, result.FailureReason);
        return result.FailureReason![prefix.Length..];
    }

    private IEnumerable<string> LogLines => _harness.Log.Select(entry => entry.Line);

    [Fact]
    public async Task BuildxPrune_UsesTheScriptDefaultsOnTheNamedBuilder()
    {
        _env["AETHEUS_BUILDX_BUILDER"] = "qa-builder";

        var (result, _) = await RunAsync(new PruneQaBuildxCacheFunction(_tools));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["buildx prune --builder qa-builder --force --reserved-space 20GB --max-used-space 32GB --min-free-space 20GB"],
            _docker.Commands);
        Assert.Contains("Applying bounded QA Buildx maintenance: builder=qa-builder max=32Go reserved=20Go minFree=20Go", LogLines);
        Assert.Contains("Bounded QA Buildx maintenance completed for 'qa-builder'.", LogLines);
    }

    [Fact]
    public async Task BuildxPrune_ParametersWinOverTheVariables()
    {
        _env["BUILDX_BUILDER"] = "fallback";
        _env["AETHEUS_QA_BUILDX_MAX_CACHE_GIB"] = "64";
        _parameters["max_cache_gib"] = "40";
        _parameters["reserved_cache_gib"] = "10";
        _parameters["min_free_gib"] = "5";

        var (result, _) = await RunAsync(new PruneQaBuildxCacheFunction(_tools));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            ["buildx prune --builder fallback --force --reserved-space 10GB --max-used-space 40GB --min-free-space 5GB"],
            _docker.Commands);
    }

    [Fact]
    public async Task BuildxPrune_RefusesAnUnscopedPrune()
    {
        Assert.Equal(
            "AETHEUS_BUILDX_BUILDER is missing; refusing an unscoped Buildx prune.",
            await FailureAsync(new PruneQaBuildxCacheFunction(_tools)));
        Assert.Empty(_docker.Commands);
    }

    [Theory]
    [InlineData("max_cache_gib", "0")]
    [InlineData("reserved_cache_gib", "-1")]
    [InlineData("min_free_gib", "2GB")]
    public async Task BuildxPrune_RefusesALimitThatIsNotAPositiveInteger(string parameter, string value)
    {
        _env["AETHEUS_BUILDX_BUILDER"] = "qa-builder";
        _parameters[parameter] = value;

        Assert.Equal("QA Buildx cache limits must be positive integers.", await FailureAsync(new PruneQaBuildxCacheFunction(_tools)));
        Assert.Empty(_docker.Commands);
    }

    [Fact]
    public async Task BuildxPrune_RefusesAMaximumBelowTheReservedCache()
    {
        _env["AETHEUS_BUILDX_BUILDER"] = "qa-builder";
        _parameters["max_cache_gib"] = "8";

        Assert.Equal(
            "QA Buildx maximum cache must be greater than or equal to its reserved cache.",
            await FailureAsync(new PruneQaBuildxCacheFunction(_tools)));
    }

    [Fact]
    public async Task BuildxPrune_AFailedPruneFailsTheStep()
    {
        _env["AETHEUS_BUILDX_BUILDER"] = "qa-builder";
        _docker.Override = _ => (1, ["ERROR: no builder"]);

        Assert.Equal("docker buildx prune failed with exit code 1.", await FailureAsync(new PruneQaBuildxCacheFunction(_tools)));
        Assert.DoesNotContain(LogLines, line => line.StartsWith("Bounded QA Buildx maintenance completed", StringComparison.Ordinal));
    }

    private void SeedQaStacks()
    {
        _docker.Containers.Add(new FakeContainer("c-old", "app-qa-7-back", "app-qa-7", "img"));
        _docker.Containers.Add(new FakeContainer("c-rollback", "app-qa-rollback-8-back", "app-qa-rollback-8", "img"));
        _docker.Containers.Add(new FakeContainer("c-current", "app-qa-9-back", "app-qa-9", "img"));
        _docker.Containers.Add(new FakeContainer("c-other", "other-qa-1-back", "other-qa-1", "img"));
        _docker.Networks.Add(("n-old", "app-qa-7"));
        _docker.Networks.Add(("n-newer", "app-qa-10"));
        _docker.Volumes.Add(("app-qa-7_db", "app-qa-7"));
        _docker.Volumes.Add(("app-qa-rollback-8_db", "app-qa-rollback-8"));
        _docker.Volumes.Add(("app-qa-x_db", "app-qa-x"));
    }

    [Fact]
    public async Task StaleCompose_RemovesEarlierRunsAndProtectsCurrentAndNewer()
    {
        SeedQaStacks();
        _env["BUILD_BUILDID"] = "9";
        _parameters["project_prefix"] = "app-qa";

        var (result, _) = await RunAsync(new PruneStaleQaComposeFunction(_tools));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["c-current", "c-other"], _docker.Containers.Select(container => container.Id));
        Assert.Equal(["n-newer"], _docker.Networks.Select(network => network.Id));
        Assert.Equal(["app-qa-x_db"], _docker.Volumes.Select(volume => volume.Name));
        Assert.Equal(
            [
                "Protected current or newer QA Compose project 'app-qa-10'.",
                "Removing stale QA Compose project 'app-qa-7' before storage measurement.",
                "Protected current or newer QA Compose project 'app-qa-9'.",
                "Removing stale QA Compose project 'app-qa-rollback-8' before storage measurement.",
                "Stale QA Compose resource maintenance completed."
            ],
            LogLines.Where(line => line.Contains("QA Compose", StringComparison.Ordinal)));
        Assert.Contains("rm -f c-old", _docker.Commands);
        Assert.True(
            _docker.Commands.IndexOf("rm -f c-old") < _docker.Commands.IndexOf("network rm n-old")
            && _docker.Commands.IndexOf("network rm n-old") < _docker.Commands.IndexOf("volume rm app-qa-7_db"),
            "Containers, then networks, then volumes, as the script removes them.");
    }

    [Fact]
    public async Task StaleCompose_TheRunIdParameterWinsOverBuildBuildId()
    {
        SeedQaStacks();
        _env["BUILD_BUILDID"] = "100";
        _parameters["project_prefix"] = "app-qa";
        _parameters["run_id"] = "8";

        var (result, _) = await RunAsync(new PruneStaleQaComposeFunction(_tools));

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain(_docker.Containers, container => container.Id == "c-old");
        Assert.Contains(_docker.Containers, container => container.Id == "c-rollback");
    }

    [Theory]
    [InlineData("")]
    [InlineData("12a")]
    public async Task StaleCompose_RefusesAMissingOrInvalidRunId(string runId)
    {
        _env["BUILD_BUILDID"] = runId;
        _parameters["project_prefix"] = "app-qa";

        Assert.Equal("Current QA run id is missing or invalid.", await FailureAsync(new PruneStaleQaComposeFunction(_tools)));
        Assert.Empty(_docker.Commands);
    }

    [Fact]
    public async Task StaleCompose_RefusesAPrefixThatIsNotAProjectName()
    {
        _env["BUILD_BUILDID"] = "9";
        _parameters["project_prefix"] = "App QA";

        Assert.Equal("Not a Compose project prefix: 'App QA'.", await FailureAsync(new PruneStaleQaComposeFunction(_tools)));
    }

    [Fact]
    public async Task StaleCompose_AResourceThatSurvivesFailsTheStep()
    {
        SeedQaStacks();
        _env["BUILD_BUILDID"] = "9";
        _parameters["project_prefix"] = "app-qa";
        // Docker answers the removal without removing anything: only the final listing can tell.
        _docker.Override = command => command == "volume rm app-qa-7_db" ? (0, ["app-qa-7_db"]) : null;

        Assert.Equal(
            "The volumes of QA Compose project 'app-qa-7' survived the removal.",
            await FailureAsync(new PruneStaleQaComposeFunction(_tools)));
    }

    [Fact]
    public async Task StaleCompose_ARefusedRemovalStopsTheStepAsTheScriptsSetEDid()
    {
        SeedQaStacks();
        _env["BUILD_BUILDID"] = "9";
        _parameters["project_prefix"] = "app-qa";
        _docker.Override = command => command == "rm -f c-old" ? (1, ["container in use"]) : null;

        Assert.Equal(
            "docker rm -f c-old failed with exit code 1.",
            await FailureAsync(new PruneStaleQaComposeFunction(_tools)));
        Assert.DoesNotContain("network rm n-old", _docker.Commands);
        Assert.DoesNotContain(_docker.Commands, command => command.Contains("app-qa-rollback-8", StringComparison.Ordinal) && command.StartsWith("rm", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaleCompose_AListingDockerCannotAnswerIsAFailureNotAnEmptyHost()
    {
        _env["BUILD_BUILDID"] = "9";
        _parameters["project_prefix"] = "app-qa";
        _docker.Override = command => command.StartsWith("network ls", StringComparison.Ordinal) ? (1, []) : null;

        Assert.Equal(
            "Docker could not list the Compose projects of the host.",
            await FailureAsync(new PruneStaleQaComposeFunction(_tools)));
    }
}
