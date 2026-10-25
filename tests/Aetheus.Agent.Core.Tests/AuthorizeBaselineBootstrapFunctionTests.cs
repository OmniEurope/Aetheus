// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>authorize-baseline-bootstrap.sh</c> script, run through the real
/// executor: a bootstrap is refused unless explicitly allowed, anything else is accepted, with the
/// script's messages.
/// </summary>
public sealed class AuthorizeBaselineBootstrapFunctionTests : IDisposable
{
    private const string Refusal =
        "No rollback-capable deployed baseline exists and allowBootstrap is false; refusing bootstrap before CI.";

    private readonly FunctionTestHarness _harness = new("aetheus-authorize-bootstrap");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal)
    {
        ["DELIVERY_BASELINE_BOOTSTRAP"] = "",
        ["ALLOW_BOOTSTRAP"] = ""
    };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal);

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new AuthorizeBaselineBootstrapFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    private async Task AcceptedAsync()
    {
        var (result, variables) = await RunAsync();
        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
        Assert.Contains(_harness.Log, entry => entry.Line == "Baseline authorization accepted before CI.");
    }

    [Theory]
    [InlineData("true", "false")]
    [InlineData("", "false")]
    [InlineData("true", "TRUE")]
    [InlineData("true", "yes")]
    public async Task Bootstrap_NotExplicitlyAllowed_IsRefused(string bootstrap, string allow)
    {
        _env["DELIVERY_BASELINE_BOOTSTRAP"] = bootstrap;
        _env["ALLOW_BOOTSTRAP"] = allow;

        Assert.EndsWith(Refusal, await FailureAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bootstrap_WithAllowUnset_NamesTheMissingVariable()
    {
        _env["DELIVERY_BASELINE_BOOTSTRAP"] = "true";

        Assert.EndsWith(
            "No rollback-capable deployed baseline exists and ALLOW_BOOTSTRAP is not set; refusing bootstrap before CI.",
            await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("true", "true")]
    [InlineData("", "true")]
    [InlineData("false", "")]
    [InlineData("false", "false")]
    [InlineData("TRUE", "")]
    public async Task Baseline_OrAllowedBootstrap_IsAccepted(string bootstrap, string allow)
    {
        _env["DELIVERY_BASELINE_BOOTSTRAP"] = bootstrap;
        _env["ALLOW_BOOTSTRAP"] = allow;

        await AcceptedAsync();
    }

    [Fact]
    public async Task Parameters_WinOverTheScriptVariables()
    {
        _env["DELIVERY_BASELINE_BOOTSTRAP"] = "false";
        _env["ALLOW_BOOTSTRAP"] = "true";
        _parameters["bootstrap"] = "true";
        _parameters["allow_bootstrap"] = "false";

        Assert.EndsWith(Refusal, await FailureAsync(), StringComparison.Ordinal);

        _parameters["allow_bootstrap"] = "true";
        await AcceptedAsync();
    }
}
