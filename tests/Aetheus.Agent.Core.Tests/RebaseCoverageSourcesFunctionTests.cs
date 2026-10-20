// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/rebase-quality-coverage-sources.mjs</c>, with the two cases of its
/// <c>.test.mjs</c> (a <c>src</c> root and a single-project root rebased, a root outside any
/// <c>src</c> refused) and the script's other refusals and output.
/// </summary>
public sealed partial class RebaseCoverageSourcesFunctionTests : IDisposable
{
    private readonly FunctionTestHarness _harness = new("aetheus-coverage-rebase");

    public void Dispose() => _harness.Dispose();

    [GeneratedRegex("<source>([^<]*)</source>")]
    private static partial Regex Source();

    private string SourceRoot => Path.Combine(_harness.Workspace, "src").Replace('\\', '/');

    private void Report(string name, string source) =>
        _harness.Write($"ws/coverage/{name}/coverage.cobertura.xml", $"<coverage><sources><source>{source}</source></sources></coverage>");

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(new Dictionary<string, string> { ["output"] = "rebased" })
        };
        return _harness.RunAsync(new RebaseCoverageSourcesFunction(), env);
    }

    private string Rebased(string name) =>
        Source().Match(File.ReadAllText(Path.Combine(_harness.Workspace, "rebased", name, "coverage.cobertura.xml"))).Groups[1].Value;

    [Fact]
    public async Task RebasesASrcRootAndASingleProjectRootOntoThisWorkspace()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "src"));
        Report("backend", "/var/lib/aetheus-agent/w/0632e383/s/src/");
        Report("agent", "/var/lib/aetheus-agent/w/0632e383/s/src/Aetheus.Agent.Core/");

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{SourceRoot}/", Rebased("backend"));
        Assert.Equal($"{SourceRoot}/Aetheus.Agent.Core/", Rebased("agent"));
        var expected = new[] { "agent", "backend" }
            .Select(name => Path.Combine(_harness.Workspace, "rebased", name, "coverage.cobertura.xml"));
        Assert.Equal(string.Join(';', expected), variables["COVERAGE_REPORTS"]);
        Assert.Contains("/s/src/</source>", File.ReadAllText(Path.Combine(_harness.Workspace, "coverage", "backend", "coverage.cobertura.xml")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWindowsRootWithoutTrailingSlash_IsRebased()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "src"));
        Report("front", @"C:\agent\w\s\src\Aetheus.Front");

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal($"{SourceRoot}/Aetheus.Front/", Rebased("front"));
    }

    [Theory]
    [InlineData("/var/lib/aetheus-agent/w/0632e383/s/tests/")]
    [InlineData("relative/src/Project/")]
    public async Task RefusesASourceRootOutsideAnyAbsoluteSrcDirectory(string source)
    {
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "src"));
        Report("agent", source);

        var (result, variables) = await RunAsync();

        Assert.Contains($"Unexpected Cobertura source root in {Path.Combine(_harness.Workspace, "coverage", "agent", "coverage.cobertura.xml")}: {source}",
            result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task RefusesAReportWithoutSourceRoot()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "src"));
        _harness.Write("ws/coverage/x/coverage.cobertura.xml", "<coverage />");

        var (result, _) = await RunAsync();

        Assert.Contains("Cobertura report has no source root:", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAWorkspaceWithoutReports()
    {
        Directory.CreateDirectory(Path.Combine(_harness.Workspace, "src"));

        var (result, _) = await RunAsync();

        Assert.EndsWith("No raw Cobertura report was found under coverage/.", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefusesAWorkspaceWithoutSources()
    {
        Report("agent", "/s/src/");

        var (result, _) = await RunAsync();

        Assert.Contains("Source directory is unavailable:", result.FailureReason, StringComparison.Ordinal);
    }
}
