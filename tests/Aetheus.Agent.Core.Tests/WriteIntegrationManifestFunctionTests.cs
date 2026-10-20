// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/write-integration-manifest.mjs</c>, with the cases of its
/// <c>integration-manifest.test.mjs</c>. The delivery contract takes a digest over the manifest's bytes,
/// so the expected text is the one the script wrote for the same declaration (Node.js 24, captured when
/// the function replaced it).
/// </summary>
public sealed class WriteIntegrationManifestFunctionTests : IDisposable
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // examples/optional-observability/aetheus.integrations.json when the function replaced the script.
    private const string Declaration = """
        {
          "schema": 1,
          "packages": [
            {
              "id": "Aetheus.Telemetry",
              "version": "1.2.0",
              "enabled": false,
              "sha256": "",
              "enabledProperty": "EnableAetheusTelemetry",
              "versionProperty": "AetheusTelemetryVersion"
            },
            {
              "id": "Aetheus.WebAnalytics",
              "version": "1.1.0",
              "enabled": false,
              "sha256": "",
              "enabledProperty": "EnableAetheusWebAnalytics",
              "versionProperty": "AetheusWebAnalyticsVersion"
            }
          ]
        }
        """;

    // What `node write-integration-manifest.mjs <Commit> <output> <declaration>` wrote.
    private const string ScriptOutput =
        "{\"schema\":1,\"sourceCommit\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"packages\":[{\"id\":\"Aetheus.Telemetry\",\"version\":\"1.2.0\",\"enabled\":false,\"sha256\":\"\",\"enabledProperty\":\"EnableAetheusTelemetry\",\"versionProperty\":\"AetheusTelemetryVersion\"},{\"id\":\"Aetheus.WebAnalytics\",\"version\":\"1.1.0\",\"enabled\":false,\"sha256\":\"\",\"enabledProperty\":\"EnableAetheusWebAnalytics\",\"versionProperty\":\"AetheusWebAnalyticsVersion\"}]}\n";

    private readonly FunctionTestHarness _harness = new("aetheus-integration-manifest");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal) { ["BUILD_SOURCEVERSION"] = Commit };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["output"] = "integration-manifest.json",
        ["declaration"] = "declaration.json"
    };

    public void Dispose() => _harness.Dispose();

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new WriteIntegrationManifestFunction(), _env);
    }

    private string Output => Path.Combine(_harness.Workspace, "integration-manifest.json");

    [Fact]
    public async Task Manifest_IsTheBytesTheScriptWrote()
    {
        _harness.Write("ws/declaration.json", Declaration);

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(variables);
        Assert.Equal(ScriptOutput, await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Manifest_CarriesSchemaOneTheCommitAndTheDeclaredPackages()
    {
        var (manifest, error) = WriteIntegrationManifestFunction.Build(Commit, """{"packages":[{"id":"Aetheus.Telemetry","version":"1.0.0"}]}""");

        Assert.Null(error);
        Assert.Equal(
            """{"schema":1,"sourceCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","packages":[{"id":"Aetheus.Telemetry","version":"1.0.0"}]}""",
            ScriptJson.Compact(manifest!));
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\n")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggg")]
    public void Manifest_RefusesARevisionThatIsNotAFullCommit(string commit) =>
        Assert.Equal(
            "The source commit must be a full 40-character revision.",
            WriteIntegrationManifestFunction.Build(commit, """{"packages":[]}""").Error);

    [Theory]
    [InlineData("""{"other":true}""")]
    [InlineData("""{"packages":{}}""")]
    public void Manifest_RefusesADeclarationWithoutAPackagesArray(string declaration) =>
        Assert.Equal(
            "The integration declaration must carry a `packages` array.",
            WriteIntegrationManifestFunction.Build(Commit, declaration).Error);

    [Fact]
    public async Task Parameter_CommitWinsOverTheBuildVariable()
    {
        _harness.Write("ws/declaration.json", """{"packages":[]}""");
        _parameters["commit"] = new string('b', 40);

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(new string('b', 40), await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MissingDeclaration_FailsWithoutWritingAManifest()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.Contains("does not exist", result.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Output));
    }

    [Fact]
    public async Task MissingRequiredParameter_IsRefusedBeforeRunning()
    {
        _parameters.Remove("declaration");

        var (result, _) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.InvalidRequest, result.FailureCode);
    }
}
