// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/generate-artifact-provenance.mjs</c>, with the case of its
/// <c>artifact-provenance.test.mjs</c>. The expected files are what the script wrote for the same
/// workspace (Node.js 24, captured when the function replaced it), with the built-in Aetheus categories
/// and with a project's own map (an integer key, which <c>Object.entries</c> puts first, and a missing
/// path); the refusals are the script's.
/// </summary>
public sealed class GenerateArtifactProvenanceFunctionTests : IDisposable
{
    private const string Commit = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string EmptyDigest = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private const string AgentArchives =
        "\"agentArchives\":{\"fileCount\":1,\"totalBytes\":241,\"sha256\":\"d97d739dccd8016665d93ad0100aab684c9be2aa7412f991b725cfe9c3dde7f5\",\"declaredArtifacts\":[{\"fileName\":\"a.tar.gz\",\"sizeBytes\":123,\"sha256\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\"},{\"fileName\":\"b.zip\",\"sizeBytes\":7,\"sha256\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\"}]}";

    private const string Coverage = "{\"fileCount\":3,\"totalBytes\":22,\"sha256\":\"a1b65fab33bddbd70363412424551508d14a37dc0fdbd1ec0b0808fbb449d35f\"}";
    private const string Images = "{\"fileCount\":1,\"totalBytes\":14,\"sha256\":\"346f595ca2e0a072d39420a50cd7cc1141f11290ada8ed06e1fb56e894577845\"}";
    private const string Nothing = "{\"fileCount\":0,\"totalBytes\":0,\"sha256\":\"" + EmptyDigest + "\"}";

    private const string ScriptDefault =
        "{\"schema\":1,\"sourceCommit\":\"" + Commit + "\",\"categories\":{\"dockerImages\":" + Images + "," + AgentArchives
        + ",\"coverage\":" + Coverage + ",\"qaRuntime\":" + Nothing + ",\"buildIntermediates\":" + Nothing
        + ",\"optionalObservability\":" + Nothing
        + ",\"contracts\":{\"fileCount\":1,\"totalBytes\":41,\"sha256\":\"6956a2fab978d47c769cfc20353932d849a63cda96a95ad6a2d7cc3135c20db8\"}}}\n";

    private const string ScriptOverride =
        "{\"schema\":1,\"sourceCommit\":\"" + Commit + "\",\"categories\":{\"2\":" + Coverage + ",\"images\":" + Images + ","
        + AgentArchives + ",\"none\":" + Nothing + "}}\n";

    private readonly FunctionTestHarness _harness = new("aetheus-provenance");
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal) { ["BUILD_SOURCEVERSION"] = Commit.ToUpperInvariant() };
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal) { ["output"] = ".pipeline-artifacts/artifact-provenance.json" };

    public GenerateArtifactProvenanceFunctionTests()
    {
        _harness.Write("ws/.pipeline-artifacts/aetheus-back.tar.gz", "secret-payload");
        _harness.Write("ws/.pipeline-artifacts/source-commit", Commit + "\n");
        _harness.Write("ws/.pipeline-artifacts/agent-release/agent-release-manifest.json",
            "{\"archives\":[{\"fileName\":\"b.zip\",\"sizeBytes\":7,\"sha256\":\"" + new string('B', 64)
            + "\"},{\"fileName\":\"a.tar.gz\",\"sizeBytes\":123,\"sha256\":\"" + new string('d', 64) + "\"}]}");
        _harness.Write("ws/coverage/backend/123/coverage.cobertura.xml", "<coverage/>");
        _harness.Write("ws/coverage/backend/backend.trx", "<TestRun/>");
        _harness.Write("ws/coverage/Z.txt", "z");
    }

    public void Dispose() => _harness.Dispose();

    private string Output => _harness.Artifact("artifact-provenance.json");

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new GenerateArtifactProvenanceFunction(), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.False(File.Exists(Output));
        return result.FailureReason!;
    }

    [Fact]
    public async Task AetheusLayout_IsTheProvenanceTheScriptWrote()
    {
        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var written = await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken);
        Assert.Equal(ScriptDefault, written);
        // Sizes and digests, never the contents.
        Assert.DoesNotContain("secret-payload", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AProjectsOwnCategories_AreTheProvenanceTheScriptWrote()
    {
        _harness.Write("ws/categories.json",
            """{"images":[".pipeline-artifacts/aetheus-back.tar.gz"],"2":["coverage"],"agentArchives":[".pipeline-artifacts/agent-release"],"none":["missing/path"]}""");
        _env["AETHEUS_PROVENANCE_CATEGORIES"] = "categories.json";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(ScriptOverride, await File.ReadAllTextAsync(Output, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnOutputOutsideTheWorkspace_IsRefused()
    {
        _parameters["output"] = "../provenance.json";

        var (result, _) = await RunAsync();

        Assert.EndsWith("The provenance output must stay inside the workspace.", result.FailureReason, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(_harness.Root, "provenance.json")));
    }

    [Fact]
    public async Task ARevisionThatIsNotFull_IsRefused()
    {
        _parameters["commit"] = "abc";

        Assert.Contains("40-char-source-commit", await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{}", "The provenance category override declares no category.")]
    [InlineData("""{"a":"coverage"}""", "Provenance category 'a' must list workspace-relative paths.")]
    [InlineData("""{"a":[""]}""", "Provenance category 'a' must list workspace-relative paths.")]
    [InlineData("""{"a":["../outside"]}""", "Provenance category 'a' escapes the workspace.")]
    public async Task AnInvalidCategoryMap_IsRefused(string categories, string message)
    {
        _harness.Write("ws/categories.json", categories);
        _parameters["categories"] = "categories.json";

        Assert.EndsWith(message, await FailureAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"archives":[{"fileName":"a","sizeBytes":-1,"sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]}""")]
    [InlineData("""{"archives":[{"fileName":"a","sizeBytes":1.5,"sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]}""")]
    [InlineData("""{"archives":[{"fileName":"","sizeBytes":1,"sha256":"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd"}]}""")]
    [InlineData("""{"archives":[{"fileName":"a","sizeBytes":1,"sha256":"short"}]}""")]
    public async Task AnInvalidDeclaredArchive_IsRefused(string manifest)
    {
        _harness.Write("ws/.pipeline-artifacts/agent-release/agent-release-manifest.json", manifest);

        Assert.EndsWith("Agent release manifest contains an invalid archive contract.", await FailureAsync(), StringComparison.Ordinal);
    }

    // Creating a symbolic link needs a privilege on Windows; the junction test below proves the same
    // refusal there (Node.js' lstat reports a junction as a symbolic link too).
    [PlatformFact("linux")]
    public async Task ASymbolicLink_IsRefused()
    {
        File.CreateSymbolicLink(Path.Combine(_harness.Workspace, "coverage", "link.txt"), Path.Combine(_harness.Workspace, "coverage", "Z.txt"));

        Assert.Contains("Artifact provenance refuses symlink: coverage", await FailureAsync(), StringComparison.Ordinal);
    }

    [PlatformFact("windows")]
    public async Task ADirectoryJunction_IsRefused()
    {
        var outside = Directory.CreateDirectory(Path.Combine(_harness.Root, "outside")).FullName;
        await File.WriteAllTextAsync(Path.Combine(outside, "token"), "not an artifact", TestContext.Current.CancellationToken);
        var junction = Path.Combine(_harness.Workspace, "coverage", "home");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", ["/c", "mklink", "/J", junction, outside])
        { RedirectStandardOutput = true, UseShellExecute = false })!)
            await mklink.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.True(Directory.Exists(junction), "the junction was not created");

        try
        {
            Assert.Contains("Artifact provenance refuses symlink: coverage", await FailureAsync(), StringComparison.Ordinal);
        }
        finally
        {
            // A non-recursive delete removes the junction itself, never what it points to.
            Directory.Delete(junction);
        }
    }
}
