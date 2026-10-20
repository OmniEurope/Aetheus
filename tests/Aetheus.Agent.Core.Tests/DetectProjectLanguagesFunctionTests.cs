// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>detect-project-languages.sh</c>, run through the real executor on a temp
/// workspace unique to each test: detection per language, excluded directories, the <c>root</c>
/// parameter, a root escaping the workspace, and the published <c>##aetheus[setvariable]</c> lines.
/// </summary>
public sealed class DetectProjectLanguagesFunctionTests : IDisposable
{
    private const string SetVariablePrefix = "##aetheus[setvariable name=";

    private readonly string _workspace = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "aetheus-detect-" + Guid.NewGuid().ToString("N"), "ws")).FullName;

    private readonly List<(string Line, TaskLogLevel Level)> _log = [];

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(_workspace)!, recursive: true);

    private void Touch(string relativePath)
    {
        var path = Path.Combine(_workspace, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Empty);
    }

    private Task Capture(string line, TaskLogLevel level)
    {
        lock (_log)
            _log.Add((line, level));
        return Task.CompletedTask;
    }

    private IEnumerable<string> SetVariableLines => _log
        .Select(entry => entry.Line)
        .Where(line => line.StartsWith(SetVariablePrefix, StringComparison.Ordinal));

    private async Task<(ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(string? parametersJson = null)
    {
        var env = new Dictionary<string, string> { [PipelineFunctionRequest.WorkspaceVariable] = _workspace };
        if (parametersJson is not null)
            env[PipelineFunctionVariables.ParametersJson] = parametersJson;
        var executor = new PipelineFunctionOperationExecutor([new DetectProjectLanguagesFunction()]);
        var result = await executor.ExecuteAsync(
            OperationKind.PipelineFunction, "detect-project-languages", env, 30, Capture,
            TestContext.Current.CancellationToken);
        var variables = SetVariableLines
            .Select(line => line[SetVariablePrefix.Length..].Split(']', 2))
            .ToDictionary(parts => parts[0], parts => parts[1], StringComparer.Ordinal);
        return (result, variables);
    }

    private static void AssertLanguages(
        Dictionary<string, string> variables, bool dotnet = false, bool js = false, bool python = false, bool java = false)
    {
        Assert.Equal(Flag(dotnet), variables["HAS_DOTNET"]);
        Assert.Equal(Flag(js), variables["HAS_JAVASCRIPT"]);
        Assert.Equal(Flag(python), variables["HAS_PYTHON"]);
        Assert.Equal(Flag(java), variables["HAS_JAVA"]);
    }

    private static string Flag(bool value) => value ? "true" : "false";

    [Fact]
    public async Task EmptyWorkspace_PublishesFourFalseVariables()
    {
        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(4, variables.Count);
        AssertLanguages(variables);
    }

    [Theory]
    [InlineData("src/App/App.csproj", "dotnet")]
    [InlineData("web/a.js", "js")]
    [InlineData("web/a.jsx", "js")]
    [InlineData("web/a.ts", "js")]
    [InlineData("web/a.tsx", "js")]
    [InlineData("tools/a.py", "python")]
    [InlineData("svc/A.java", "java")]
    public async Task EachLanguage_IsDetectedAlone(string file, string language)
    {
        Touch(file);

        var (_, variables) = await RunAsync();

        AssertLanguages(
            variables, dotnet: language == "dotnet", js: language == "js", python: language == "python", java: language == "java");
    }

    [Fact]
    public async Task AllLanguages_AreDetectedTogether()
    {
        Touch("a/A.csproj");
        Touch("b/b.ts");
        Touch("c/c.py");
        Touch("d/D.java");

        var (_, variables) = await RunAsync();

        AssertLanguages(variables, dotnet: true, js: true, python: true, java: true);
    }

    [Theory]
    [InlineData(".git")]
    [InlineData(".claude")]
    [InlineData(".codex")]
    [InlineData("bin")]
    [InlineData("obj")]
    [InlineData("node_modules")]
    [InlineData(".venv")]
    [InlineData("target")]
    [InlineData("build")]
    [InlineData("TestResults")]
    [InlineData(".analysis-coverage")]
    [InlineData("coverage")]
    [InlineData("tests/security-rules")]
    public async Task ExcludedDirectories_AreIgnored_AtAnyDepth(string excluded)
    {
        Touch($"{excluded}/x.py");
        Touch($"src/deep/{excluded}/y.java");

        var (_, variables) = await RunAsync();

        AssertLanguages(variables);
    }

    [Fact]
    public async Task SecurityRulesDirectory_IsExcludedOnlyBelowTests()
    {
        Touch("other/security-rules/x.py");

        var (_, variables) = await RunAsync();

        AssertLanguages(variables, python: true);
    }

    [Fact]
    public async Task ProductFileBesideAnExcludedDirectory_IsStillFound()
    {
        Touch("node_modules/dep/index.js");
        Touch("src/app.ts");

        var (_, variables) = await RunAsync();

        AssertLanguages(variables, js: true);
    }

    [Fact]
    public async Task Extensions_AreMatchedCaseSensitively_LikeFind()
    {
        Touch("a/Script.PY");
        Touch("a/Project.CSPROJ");

        var (_, variables) = await RunAsync();

        AssertLanguages(variables);
    }

    [Fact]
    public async Task RootParameter_ScansOnlyThatDirectory()
    {
        Touch("api/A.csproj");
        Touch("web/b.ts");

        var (_, variables) = await RunAsync("""{"root":"web"}""");

        AssertLanguages(variables, js: true);
    }

    [Fact]
    public async Task RootParameter_PointingToAMissingDirectory_Fails()
    {
        var (result, variables) = await RunAsync("""{"root":"missing"}""");

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(variables);
        Assert.Contains(_log, entry => entry.Line.Contains("does not exist", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../sibling")]
    [InlineData("sub/../../escape")]
    public async Task RootParameter_EscapingTheWorkspace_IsRefused(string root)
    {
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["root"] = root });

        var (result, variables) = await RunAsync(json);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(variables);
        Assert.Contains(_log, entry => entry.Line.Contains("outside the workspace", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AbsoluteRootOutsideTheWorkspace_IsRefused()
    {
        var outside = Path.GetDirectoryName(_workspace)!;
        var json = JsonSerializer.Serialize(new Dictionary<string, string> { ["root"] = outside });

        var (result, variables) = await RunAsync(json);

        Assert.Equal(1, result.ExitCode);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task OutputsAreEmittedInCatalogOrder_AsSetVariableLines()
    {
        Touch("a/A.csproj");

        await RunAsync();

        Assert.Equal(
            [
                "##aetheus[setvariable name=HAS_DOTNET]true",
                "##aetheus[setvariable name=HAS_JAVASCRIPT]false",
                "##aetheus[setvariable name=HAS_PYTHON]false",
                "##aetheus[setvariable name=HAS_JAVA]false"
            ],
            SetVariableLines.ToList());
    }
}
