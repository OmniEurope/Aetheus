// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/run-qa-integration-suite.sh</c>, run through the real executor with
/// a <c>dotnet</c> stand-in that reports the pinned SDK and writes the TRX each case asks for: the runner
/// the tested source selects, the classification (findings versus unusable evidence), the failed names
/// recorded (the cases of <c>failed-tests-publication.test.mjs</c>), the restore guard, and the static
/// web assets rebase (the cases of <c>rebase-static-web-assets.test.mjs</c>).
/// </summary>
public sealed class RunQaIntegrationSuiteFunctionTests : IDisposable
{
    private const string Sdk = "10.0.100";
    private const string Project = "tests/Aetheus.Back.IntegrationTests";

    private readonly FunctionTestHarness _harness = new("aetheus-qa-integration");
    private readonly FakeFunctionTools _tools = new();
    private readonly List<string[]> _dotnet = [];
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["source_root"] = ".",
        ["label"] = "current",
        ["test_project"] = Project
    };
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    private string? _trx;
    private int _testExit;
    private int _restoreExit;

    public RunQaIntegrationSuiteFunctionTests()
    {
        var dotnet = _harness.Executable("bin/dotnet");
        _env["PATH"] = Path.GetDirectoryName(dotnet)!;
        _env["HOME"] = Path.Combine(_harness.Root, "home");
        // The version line on its own line: ensure-dotnet-sdk read it line by line.
        _harness.Write("ws/global.json", $$"""
            {
              "sdk": {
                "version": "{{Sdk}}"
              },
              "test": { "runner": "Microsoft.Testing.Platform" }
            }
            """);
        Source(".");
        _tools.OnRun = Answer;
    }

    public void Dispose() => _harness.Dispose();

    private string Results => Path.Combine(_harness.Workspace, ".qa-test-results", "current");

    /// <summary>A restored source tree: the test project and an obj/ whose package root is an MSBuild
    /// reference, which needs no restore.</summary>
    private void Source(string root)
    {
        _harness.Write($"ws/{root}/{Project}/Aetheus.Back.IntegrationTests.csproj", "<Project />");
        PackageRoot(root, "$(UserProfile)/.nuget/packages/");
    }

    private void PackageRoot(string root, string value) =>
        _harness.Write($"ws/{root}/{Project}/obj/Aetheus.Back.IntegrationTests.csproj.nuget.g.props",
            $"<Project>\n  <PropertyGroup>\n    <NuGetPackageRoot Condition=\" '$(NuGetPackageRoot)' == '' \">{value}</NuGetPackageRoot>\n  </PropertyGroup>\n</Project>\n");

    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var arguments = process.ArgumentList.ToArray();
        if (arguments is ["--version"])
            return Task.FromResult((0, new[] { Sdk }));
        _dotnet.Add(arguments);
        if (arguments[0] == "restore")
            return Task.FromResult((_restoreExit, Array.Empty<string>()));
        var results = arguments[Array.IndexOf(arguments, "--results-directory") + 1];
        if (_trx is not null)
            File.WriteAllText(Path.Combine(results, "integration.trx"), _trx);
        return Task.FromResult((_testExit, new[] { "test output" }));
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new RunQaIntegrationSuiteFunction(_tools), _env);
    }

    [Fact]
    public async Task ACleanSuite_IsStatusZeroWithoutAFailedList()
    {
        _trx = FailedTestNamesTests.Trx(("Integration.Passes", "Passed"));

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("0", variables["INTEGRATION_SUITE_STATUS"]);
        Assert.False(File.Exists(Path.Combine(Results, "failed-tests.txt")));
        var test = Assert.Single(_dotnet);
        Assert.Equal(
            ["test", "--project", Project, "--configuration", "Release", "--no-build", "--no-restore", "--no-progress",
             "--report-trx", "--report-trx-filename", "integration.trx", "--results-directory", Results], test);
        Assert.Contains(_harness.Log, entry => entry.Line == "==> Integration tests (current) started");
        Assert.Contains(_harness.Log, entry => entry.Line == "Integration suite current produced an executed-test proof with findings status 0.");
    }

    [Fact]
    public async Task FailedTests_AreStatusOneWithTheirNamesRecorded()
    {
        _trx = FailedTestNamesTests.Trx(("Integration.Passes", "Passed"), ("Integration.LoginFails", "Failed"), ("Integration.RoleFails", "Failed"));
        _testExit = 2;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["INTEGRATION_SUITE_STATUS"]);
        Assert.Equal("Integration.LoginFails\nIntegration.RoleFails\n", File.ReadAllText(Path.Combine(Results, "failed-tests.txt")));
        Assert.Contains(_harness.Log, entry => entry.Line == "Failed tests (2): Integration.LoginFails, Integration.RoleFails");
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(0, true)]
    public async Task UnusableEvidence_FailsTheStep(int exitCode, bool writeTrx)
    {
        // A crashed host without a TRX, or a clean exit contradicted by a recorded failure.
        _trx = writeTrx ? FailedTestNamesTests.Trx(("A", "Failed")) : null;
        _testExit = exitCode;

        var (result, variables) = await RunAsync();

        Assert.Contains("The integration test evidence cannot be graded:", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task AV1WithoutTheTestingPlatform_RunsVSTest()
    {
        _parameters["source_root"] = ".nminus1-source";
        _parameters["label"] = "previous";
        Source(".nminus1-source");
        _harness.Write("ws/.nminus1-source/global.json", """{ "sdk": { "version": "10.0.100" } }""");
        _trx = FailedTestNamesTests.Trx(("A", "Passed"));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var test = Assert.Single(_dotnet);
        Assert.Equal("trx;LogFileName=integration.trx", test[Array.LastIndexOf(test, "--logger") + 1]);
        Assert.Equal(Path.Combine(_harness.Workspace, ".qa-test-results", "previous"), test[^1]);
    }

    [Fact]
    public async Task AnAbsentPackageFolder_IsRestoredFirst()
    {
        PackageRoot(".", Path.Combine(_harness.Root, "absent-cache"));
        _trx = FailedTestNamesTests.Trx(("A", "Passed"));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["restore", Project, "-p:Configuration=Release", "--locked-mode", "--verbosity", "minimal"], _dotnet[0]);
        Assert.Equal("test", _dotnet[1][0]);
    }

    [Fact]
    public async Task APresentPackageFolder_IsNotRestored()
    {
        PackageRoot(".", Directory.CreateDirectory(Path.Combine(_harness.Root, "cache")).FullName);
        _trx = FailedTestNamesTests.Trx(("A", "Passed"));

        await RunAsync();

        Assert.Equal("test", Assert.Single(_dotnet)[0]);
    }

    [Fact]
    public async Task AFailedRestore_FailsBeforeTheTests()
    {
        PackageRoot(".", string.Empty);
        _restoreExit = 1;

        var (result, _) = await RunAsync();

        Assert.Contains("dotnet restore of tests/Aetheus.Back.IntegrationTests failed with exit code 1.", result.FailureReason, StringComparison.Ordinal);
        Assert.Single(_dotnet);
    }

    [Fact]
    public async Task StaticWebAssets_AreRebasedOntoTheSourceAndTheirDirectoriesCreated()
    {
        const string Manifest = Project + "/bin/Release/net10.0/Aetheus.Back.staticwebassets.runtime.json";
        _harness.Write("ws/" + Manifest, """{"ContentRoots":["/tmp/ci-source/src/Aetheus.Back/wwwroot/","C:\\ci\\src\\aetheus.back\\obj\\Release\\net10.0\\compressed\\"],"Root":{"Children":null,"Asset":null,"Patterns":null}}""");
        _parameters["static_web_assets_manifest"] = Manifest;
        _parameters["static_web_assets_project"] = "src/Aetheus.Back";
        _trx = FailedTestNamesTests.Trx(("A", "Passed"));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var expected = new[]
        {
            Path.Combine(_harness.Workspace, "src", "Aetheus.Back", "wwwroot") + Path.DirectorySeparatorChar,
            Path.Combine(_harness.Workspace, "src", "aetheus.back", "obj", "Release", "net10.0", "compressed") + Path.DirectorySeparatorChar
        };
        using var rewritten = JsonDocument.Parse(File.ReadAllText(Path.Combine(_harness.Workspace, Manifest)));
        Assert.Equal(expected, rewritten.RootElement.GetProperty("ContentRoots").EnumerateArray().Select(root => root.GetString()));
        Assert.True(rewritten.RootElement.TryGetProperty("Root", out _));
        Assert.All(expected, root => Assert.True(Directory.Exists(root)));
    }

    /// <summary>QA run 1278: the backend references the OmniEurope.Blazor package, so the manifest names
    /// the CI runner's NuGet cache. That root is rebased onto the same package in this host's cache.</summary>
    [Fact]
    public async Task APackageContentRoot_IsRebasedOntoThisHostsNuGetCache()
    {
        const string Manifest = "manifest.json";
        var cache = Directory.CreateDirectory(Path.Combine(_harness.Root, "host-cache")).FullName;
        var package = Directory.CreateDirectory(Path.Combine(cache, "omnieurope.blazor", "1.8.1", "staticwebassets")).FullName;
        PackageRoot(".", cache);
        _harness.Write("ws/" + Manifest, """{"ContentRoots":["/tmp/ci-source/src/Aetheus.Back/wwwroot/","/var/lib/aetheus-agent/.nuget/packages/omnieurope.blazor/1.8.1/staticwebassets/"]}""");
        _parameters["static_web_assets_manifest"] = Manifest;
        _parameters["static_web_assets_project"] = "src/Aetheus.Back";
        _trx = FailedTestNamesTests.Trx(("A", "Passed"));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        using var rewritten = JsonDocument.Parse(File.ReadAllText(Path.Combine(_harness.Workspace, Manifest)));
        Assert.Equal(
            [Path.Combine(_harness.Workspace, "src", "Aetheus.Back", "wwwroot") + Path.DirectorySeparatorChar, package + Path.DirectorySeparatorChar],
            rewritten.RootElement.GetProperty("ContentRoots").EnumerateArray().Select(root => root.GetString()));
    }

    [Fact]
    public async Task APackageContentRootMissingFromThisHostsCache_IsRefusedBeforeTheTests()
    {
        const string Manifest = "manifest.json";
        PackageRoot(".", Directory.CreateDirectory(Path.Combine(_harness.Root, "host-cache")).FullName);
        _harness.Write("ws/" + Manifest, """{"ContentRoots":["/ci/.nuget/packages/omnieurope.blazor/1.8.1/staticwebassets/"]}""");
        _parameters["static_web_assets_manifest"] = Manifest;
        _parameters["static_web_assets_project"] = "src/Aetheus.Back";

        var (result, _) = await RunAsync();

        Assert.Contains("Static web assets package content root is not in this host's NuGet cache: /ci/.nuget/packages/omnieurope.blazor/1.8.1/staticwebassets/",
            result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_dotnet);
    }

    [Fact]
    public async Task AnUnresolvedRestoredRoot_FallsBackToNuGetPackages()
    {
        const string Manifest = "manifest.json";
        var cache = Path.Combine(_harness.Root, "nuget-packages");
        var package = Directory.CreateDirectory(Path.Combine(cache, "omnieurope.blazor", "1.8.1", "staticwebassets")).FullName;
        _env["NUGET_PACKAGES"] = cache;
        _harness.Write("ws/" + Manifest, """{"ContentRoots":["/ci/.nuget/packages/omnieurope.blazor/1.8.1/staticwebassets/"]}""");
        _parameters["static_web_assets_manifest"] = Manifest;
        _parameters["static_web_assets_project"] = "src/Aetheus.Back";
        _trx = FailedTestNamesTests.Trx(("A", "Passed"));

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(package + Path.DirectorySeparatorChar, File.ReadAllText(Path.Combine(_harness.Workspace, Manifest)).Replace("\\\\", "\\", StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AContentRootOutsideTheWebProject_IsRefused()
    {
        const string Manifest = "manifest.json";
        _harness.Write("ws/" + Manifest, """{"ContentRoots":["/tmp/unexpected/content/"]}""");
        _parameters["static_web_assets_manifest"] = Manifest;
        _parameters["static_web_assets_project"] = "src/Aetheus.Back";

        var (result, _) = await RunAsync();

        Assert.Contains("Unexpected static web assets content root: /tmp/unexpected/content/", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_dotnet);
    }

    [Theory]
    [InlineData("label", "bad label", "Invalid integration result label.")]
    [InlineData("test_project", "tests/Missing", "The test project is missing: ")]
    [InlineData("static_web_assets_project", "src/Aetheus.Back", "static_web_assets_manifest and static_web_assets_project go together.")]
    public async Task Arguments_AreRefusedBeforeAnyRun(string parameter, string value, string reason)
    {
        _parameters[parameter] = value;

        var (result, _) = await RunAsync();

        Assert.Contains(reason, result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_dotnet);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("x")]
    public async Task AnInvalidProgressInterval_IsRefused(string interval)
    {
        _env["AETHEUS_PROGRESS_INTERVAL_SECONDS"] = interval;

        var (result, _) = await RunAsync();

        Assert.EndsWith("AETHEUS_PROGRESS_INTERVAL_SECONDS must be a positive integer.", result.FailureReason, StringComparison.Ordinal);
    }
}
