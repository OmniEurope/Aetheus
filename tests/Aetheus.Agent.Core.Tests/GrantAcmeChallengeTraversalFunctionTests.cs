// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/deploy-prod/grant-acme-challenge-traversal.sh</c>, run through the
/// real executor as a deployment function with chmod answered by a fake: the traversal bit only when the
/// web root lives under the agent work directory, and a best-effort pass on the web root itself.
/// </summary>
public sealed class GrantAcmeChallengeTraversalFunctionTests : IDisposable
{
    private const string WorkDirectory = "/var/lib/aetheus-agent/work";

    private readonly FunctionTestHarness _harness = new("aetheus-acme-traversal");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public GrantAcmeChallengeTraversalFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["AETHEUS_AGENT_WORK_DIRECTORY"] = WorkDirectory;
        _env["ACME_WEBROOT"] = WorkDirectory + "/acme";
        _tools.OnRun = (_, _) => Task.FromResult((0, Array.Empty<string>()));
    }

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new GrantAcmeChallengeTraversalFunction(_tools), _env);
    }

    private IEnumerable<string> Commands => _tools.Runs.Select(run => string.Join(' ', run.ArgumentList));

    private IEnumerable<string> LogLines => _harness.Log.Select(entry => entry.Line);

    [Fact]
    public async Task WebRootUnderTheWorkDirectory_GrantsTraversalThenOpensTheWebRoot()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["o+x " + WorkDirectory, "-R o+rX " + WorkDirectory + "/acme"], Commands);
        Assert.Contains(">>> Traversal granted; Apache can reach the ACME challenge directory.", LogLines);
    }

    [Fact]
    public async Task WebRootElsewhere_GrantsNothingOnTheWorkDirectory()
    {
        _parameters["webroot"] = "/var/www/acme";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["-R o+rX /var/www/acme"], Commands);
        Assert.Contains(">>> The ACME web root is not under the agent work directory; no traversal to grant.", LogLines);
    }

    [Fact]
    public async Task AFailedTraversalGrantFailsTheStep()
    {
        _tools.OnRun = (process, _) => Task.FromResult(process.ArgumentList[0] == "o+x" ? (1, Array.Empty<string>()) : (0, Array.Empty<string>()));

        var (result, _) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.EndsWith($"chmod o+x {WorkDirectory} failed with exit code 1.", result.FailureReason);
        Assert.Single(_tools.Runs);
    }

    [Fact]
    public async Task AFailedWebRootPassIsIgnoredAndSilent()
    {
        _tools.OnRun = (process, _) => Task.FromResult(process.ArgumentList[0] == "-R" ? (1, new[] { "denied" }) : (0, Array.Empty<string>()));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("denied", LogLines);
    }

    [Theory]
    [InlineData("AETHEUS_AGENT_WORK_DIRECTORY")]
    [InlineData("ACME_WEBROOT")]
    public async Task EachVariableTheScriptRequiredIsRequired(string variable)
    {
        _env.Remove(variable);

        var (result, _) = await RunAsync();

        Assert.EndsWith($"{variable} is required", result.FailureReason);
        Assert.Empty(_tools.Runs);
    }
}
