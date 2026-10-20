// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The runner detection and the two command lines of the <c>dotnet-test</c> step, on real files.
/// </summary>
public sealed class DotnetTestCommandLineTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "aetheus-dotnet-test-cli-" + Guid.NewGuid().ToString("N"));

    public DotnetTestCommandLineTests() => Directory.CreateDirectory(_root);

    private PipelineDotnetTestRequest Request(string dotnetPath = "dotnet", bool coverage = false)
    {
        Assert.True(PipelineDotnetTestRequest.TryCreate(
            "tests/Sample.Tests",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = _root,
                [PipelineDotnetTestVariables.ResultsDirectory] = "coverage/backend",
                [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS",
                [PipelineDotnetTestVariables.TrxName] = "backend.trx",
                [PipelineDotnetTestVariables.CollectCoverage] = coverage ? "true" : "false",
                [PipelineDotnetTestVariables.DotnetPath] = dotnetPath
            },
            out var request,
            out var rejection), rejection);
        return request!;
    }

    [Fact]
    public void AGlobalJsonAboveTheWorkingDirectory_SelectsTheTestingPlatform()
    {
        // The SDK walks up from its working directory; a checkout nested below the repository root
        // still sees the root's global.json.
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "src", "app")).FullName;

        Assert.True(DotnetTestCommandLine.UsesTestingPlatform(nested));
    }

    [Fact]
    public void TheNearestGlobalJsonWins_EvenWithoutARunner()
    {
        File.WriteAllText(Path.Combine(_root, "global.json"), """{ "test": { "runner": "Microsoft.Testing.Platform" } }""");
        var nested = Directory.CreateDirectory(Path.Combine(_root, "legacy")).FullName;
        File.WriteAllText(Path.Combine(nested, "global.json"), """{ "sdk": { "version": "10.0.100" } }""");

        Assert.False(DotnetTestCommandLine.UsesTestingPlatform(nested));
    }

    [Fact]
    public void TheTestingPlatformCommandLine_IsTheOneRunUnitSuiteUses()
    {
        var arguments = DotnetTestCommandLine.Build(Request(coverage: true), testingPlatform: true).ArgumentList.ToList();

        Assert.Equal(
            [
                "test", "--project", "tests/Sample.Tests", "--configuration", "Release", "--no-build", "--no-progress",
                "--results-directory", Path.Combine(_root, "coverage/backend"),
                "--report-trx", "--report-trx-filename", "backend.trx", "--coverlet"
            ],
            arguments);
    }

    [Fact]
    public void TheVsTestCommandLine_IsUnchanged()
    {
        var arguments = DotnetTestCommandLine.Build(Request(coverage: true), testingPlatform: false).ArgumentList.ToList();

        Assert.Equal(
            [
                "test", "tests/Sample.Tests", "--configuration", "Release", "--no-build",
                "--collect:XPlat Code Coverage", "--results-directory", Path.Combine(_root, "coverage/backend"),
                "--logger", "console;verbosity=minimal", "--logger", "trx;LogFileName=backend.trx"
            ],
            arguments);
    }

    /// <summary>
    /// A pinned SDK passed by path is not the runtime the test host finds on its own; run-unit-suite.sh
    /// exports DOTNET_ROOT for that reason. A bare "dotnet" leaves the host's environment alone.
    /// </summary>
    [Fact]
    public void AnAbsoluteDotnetPath_SetsDotnetRootAndPath()
    {
        var sdk = Path.Combine(_root, "sdk");
        var startInfo = DotnetTestCommandLine.Build(Request(Path.Combine(sdk, "dotnet")), testingPlatform: true);
        var bare = DotnetTestCommandLine.Build(Request(), testingPlatform: true);

        Assert.Equal(sdk, startInfo.Environment["DOTNET_ROOT"]);
        Assert.StartsWith(sdk + Path.PathSeparator, startInfo.Environment["PATH"], StringComparison.Ordinal);
        Assert.Equal(
            System.Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            bare.Environment.TryGetValue("DOTNET_ROOT", out var inherited) ? inherited : null);
    }

    [Fact]
    public void ToolDirectories_GoFirstOnPath_BeforeThePinnedSdk()
    {
        // run-unit-suite.sh put the pinned Node.js ahead of everything: a test that starts `node`
        // must run the delivery toolchain, not the host's.
        var sdk = Path.Combine(_root, "sdk");
        var node = Path.Combine(_root, "node", "bin");
        var tools = Path.Combine(_root, "tools");
        Assert.True(PipelineDotnetTestRequest.TryCreate(
            "tests/Sample.Tests",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["WORKSPACE"] = _root,
                [PipelineDotnetTestVariables.ResultsDirectory] = "coverage/backend",
                [PipelineDotnetTestVariables.StatusVariable] = "BACKEND_STATUS",
                [PipelineDotnetTestVariables.DotnetPath] = Path.Combine(sdk, "dotnet"),
                [PipelineDotnetTestVariables.ToolPaths] = node + "\n" + tools
            },
            out var request,
            out var rejection), rejection);

        var startInfo = DotnetTestCommandLine.Build(request!, testingPlatform: true);

        Assert.StartsWith(
            node + Path.PathSeparator + tools + Path.PathSeparator + sdk + Path.PathSeparator,
            startInfo.Environment["PATH"], StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutToolDirectories_PathIsTheInheritedOne()
    {
        var startInfo = DotnetTestCommandLine.Build(Request(), testingPlatform: true);

        Assert.Equal(System.Environment.GetEnvironmentVariable("PATH"), startInfo.Environment["PATH"]);
    }

    [Fact]
    public void NormalizeCoverageReports_MovesOnlyStampedReports()
    {
        var results = Directory.CreateDirectory(Path.Combine(_root, "results")).FullName;
        File.WriteAllText(Path.Combine(results, "coverage.cobertura.20260912101500123.xml"), "<coverage />");
        File.WriteAllText(Path.Combine(results, "coverage.cobertura.notastamp.xml"), "<coverage />");

        var moved = DotnetTestCommandLine.NormalizeCoverageReports(results);

        Assert.Equal([Path.Combine(results, "20260912101500123", "coverage.cobertura.xml")], moved);
        Assert.True(File.Exists(moved[0]));
        Assert.True(File.Exists(Path.Combine(results, "coverage.cobertura.notastamp.xml")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
