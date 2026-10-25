// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// What the tests of the production deploy functions share: a temp directory, the step's
/// <c>with:</c> parameters and environment, the real <see cref="PipelineFunctionOperationExecutor"/>, and
/// the clock the dated copies are named after (2026-10-22 10:15:00 UTC, so <c>-20261022T101500Z</c>).
/// </summary>
public abstract class ProductionFunctionTestBase : IDisposable
{
    protected const string DatedSuffix = "-20261022T101500Z";

    private readonly FunctionTestHarness _harness;
    private readonly string _functionName;

    internal ProductionFunctionTestBase(string functionName)
    {
        _functionName = functionName;
        _harness = new FunctionTestHarness("aetheus-" + functionName);
    }

    protected Dictionary<string, string> Parameters { get; } = new(StringComparer.Ordinal);

    protected Dictionary<string, string> Env { get; } = new(StringComparer.Ordinal);

    protected FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 10, 22, 10, 15, 0, TimeSpan.Zero));

    internal FunctionTestHarness Harness => _harness;

    protected string Root => _harness.Root;

    protected string Workspace => _harness.Workspace;

    protected IEnumerable<string> LogLines => _harness.Log.Select(entry => entry.Line);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
            _harness.Dispose();
    }

    /// <summary>A path under the temp directory, with forward slashes like the paths the scripts saw.</summary>
    protected string PathIn(string relative) => Path.Combine(Root, relative).Replace('\\', '/');

    protected static string Read(string path) => File.ReadAllText(path);

    internal async Task<(ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(IPipelineFunction function)
    {
        Env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(Parameters);
        return await _harness.RunAsync(function, Env);
    }

    /// <summary>Runs the function, requires it to fail as a function and returns its failure reason.</summary>
    internal async Task<string> FailureAsync(IPipelineFunction function)
    {
        var (result, _) = await RunAsync(function);
        var prefix = $"Function '{_functionName}' failed: ";
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(prefix, result.FailureReason);
        return result.FailureReason![prefix.Length..];
    }

    /// <summary>Runs the function and requires it to succeed.</summary>
    internal async Task SucceedAsync(IPipelineFunction function)
    {
        var (result, _) = await RunAsync(function);
        Assert.True(result.ExitCode == 0, result.FailureReason);
    }

    /// <summary>The dated copies of a file in its directory, <c>&lt;name&gt;-YYYYMMDDTHHMMSSZ</c>.</summary>
    protected static string[] DatedCopies(string envFile)
    {
        var directory = Path.GetDirectoryName(envFile)!;
        var prefix = Path.GetFileName(envFile) + "-";
        return Directory.GetFiles(directory)
            .Select(Path.GetFileName)
            .Where(name => name!.StartsWith(prefix, StringComparison.Ordinal) && name.Length == prefix.Length + 16)
            .Select(name => name!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }
}
