// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# ports of <c>deploy/scripts/qa/qa-stack-identity.sh</c> and of the <c>ensure-node-runtime.sh</c>
/// entry point, run through the real executor: the slug, role, database and stack names the QA steps
/// derived from the project and the run, their refusals, and the pinned Node.js published as
/// <c>NODE</c>.
/// </summary>
public sealed class QaStackIdentityFunctionTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-qa-identity");

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(
        string? project, string? run, Dictionary<string, string>? parameters = null)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        if (project is not null)
            env["BUILD_PROJECTNAME"] = project;
        if (run is not null)
            env["BUILD_BUILDID"] = run;
        if (parameters is not null)
            env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(parameters);
        return _harness.RunAsync(new QaStackIdentityFunction(), env);
    }

    [Theory]
    [InlineData("Aetheus", "aetheus", "aetheus", "aetheus_e2e", "aetheus-qa-rollback-42")]
    [InlineData("My Shop.API", "my-shop-api", "my_shop_api", "my_shop_api_e2e", "my-shop-api-qa-rollback-42")]
    [InlineData("--Edge  Case--", "edge-case", "edge_case", "edge_case_e2e", "edge-case-qa-rollback-42")]
    [InlineData("2048", "2048", "p_2048", "p_2048_e2e", "2048-qa-rollback-42")]
    [InlineData("9-lives", "9-lives", "p_9_lives", "p_9_lives_e2e", "9-lives-qa-rollback-42")]
    public async Task Names_AreTheScriptsOwn(string project, string app, string user, string database, string stack)
    {
        var (result, variables) = await RunAsync(project, "42");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(app, variables["QA_APP"]);
        Assert.Equal(user, variables["QA_DB_USER"]);
        Assert.Equal(database, variables["QA_DB_NAME"]);
        Assert.Equal(stack, variables["QA_STACK"]);
    }

    [Fact]
    public async Task Parameters_OverrideTheRunVariables()
    {
        var (_, variables) = await RunAsync("Ignored", "1", new() { ["project_name"] = "Other", ["run_id"] = "7" });

        Assert.Equal("other-qa-rollback-7", variables["QA_STACK"]);
    }

    [Theory]
    [InlineData(null, "42", "BUILD_PROJECTNAME is missing or has no usable character; the QA stack cannot be named.")]
    [InlineData("--", "42", "BUILD_PROJECTNAME is missing or has no usable character; the QA stack cannot be named.")]
    [InlineData("Aetheus", null, "BUILD_BUILDID is invalid.")]
    [InlineData("Aetheus", "12a", "BUILD_BUILDID is invalid.")]
    public async Task Refusals_PublishNothing(string? project, string? run, string reason)
    {
        var (result, variables) = await RunAsync(project, run);

        Assert.Equal($"Function 'qa-stack-identity' failed: {reason}", result.FailureReason);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task EnsureNodeRuntime_PublishesTheHostNodeOfThePinnedVersion()
    {
        var node = _harness.Executable("bin/node");
        var tools = new FakeFunctionTools
        {
            OnRun = (process, _) => Task.FromResult(process.FileName == node ? (0, new[] { "v" + PinnedNodeRuntime.Version }) : (127, Array.Empty<string>()))
        };
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = Path.GetDirectoryName(node)! };

        var (result, variables) = await _harness.RunAsync(new EnsureNodeRuntimeFunction(tools), env);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(node, variables["NODE"]);
    }

    [Fact]
    public async Task EnsureNodeRuntime_RefusesAnotherVersionOffLinux()
    {
        var node = _harness.Executable("bin/node");
        var tools = new FakeFunctionTools
        {
            IsLinuxX64 = false,
            OnRun = (_, _) => Task.FromResult((0, new[] { "v22.0.0" }))
        };
        var env = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = Path.GetDirectoryName(node)! };

        var (result, variables) = await _harness.RunAsync(new EnsureNodeRuntimeFunction(tools), env);

        Assert.Equal($"Function 'ensure-node-runtime' failed: Pinned Node.js {PinnedNodeRuntime.Version} bootstrap supports Linux x86_64 only", result.FailureReason);
        Assert.Empty(variables);
    }
}
