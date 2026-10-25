// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of the former <c>extract-agent-release-from-image.sh</c> script, run through the real
/// executor against a Docker CLI stand-in whose <c>docker cp</c> lays out the release the image serves:
/// the argument refusals, the copy and the container always removed, each manifest check with the
/// script's message, the protocol policies, <c>full</c> and <c>manifest-only</c>, and the defaults the
/// CI relies on (source <c>/app/wwwroot/downloads/</c>, commit <c>BUILD_SOURCEVERSION</c>).
/// </summary>
public sealed class ExtractAgentReleaseFunctionTests : IDisposable
{
    private const string Prefix = "Function 'extract-agent-release' failed: ";

    private readonly FunctionTestHarness _harness = new("aetheus-extract-agent");
    private readonly FakeFunctionTools _tools = new();
    private readonly List<string> _docker = [];
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal)
    {
        ["image"] = "aetheus-back:" + AgentReleaseFixture.Commit,
        ["source"] = "/app/wwwroot/downloads/",
        ["output"] = ".nminus1-agent-release",
        ["commit"] = AgentReleaseFixture.Commit,
        ["protocol_policy"] = "historical-compatible"
    };

    private JsonObject _manifest = AgentReleaseFixture.Manifest();
    private int _copyExit;

    public ExtractAgentReleaseFunctionTests() => _tools.OnRun = Answer;

    public void Dispose() => _harness.Dispose();

    private string Output => Path.Combine(_harness.Workspace, ".nminus1-agent-release");

    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var arguments = process.ArgumentList.ToArray();
        _docker.Add(string.Join(' ', arguments));
        return Task.FromResult(arguments switch
        {
            ["create", _] => (0, new[] { "cid123" }),
            ["cp", "cid123:/app/wwwroot/downloads/.", var output] => Copy(output),
            ["rm", "-f", "cid123"] => (0, Array.Empty<string>()),
            _ => (127, Array.Empty<string>())
        });
    }

    private (int, string[]) Copy(string output)
    {
        if (_copyExit != 0)
            return (_copyExit, []);
        AgentReleaseFixture.Write(output, _manifest);
        File.WriteAllText(Path.Combine(output, "install-agent-linux.sh"), "#!/bin/sh\n");
        Directory.CreateDirectory(Path.Combine(output, "releases", "1.3.7"));
        File.WriteAllBytes(Path.Combine(output, "releases", "1.3.7", AgentReleaseFixture.ArchiveName), AgentReleaseFixture.ArchiveBytes);
        return (0, []);
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new ExtractAgentReleaseFunction(_tools), _env);
    }

    private async Task<string> FailureAsync()
    {
        var (result, _) = await RunAsync();
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.StartsWith(Prefix, result.FailureReason);
        return result.FailureReason![Prefix.Length..];
    }

    [Fact]
    public async Task AVerifiedRelease_IsCopiedAndTheContainerRemoved()
    {
        _harness.Write("ws/.nminus1-agent-release/stale.txt", "old");

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["create aetheus-back:" + AgentReleaseFixture.Commit, "cp cid123:/app/wwwroot/downloads/. " + Output, "rm -f cid123"], _docker);
        Assert.True(File.Exists(Path.Combine(Output, AgentReleaseFixture.ArchiveName)));
        Assert.True(File.Exists(Path.Combine(Output, "releases", "1.3.7", AgentReleaseFixture.ArchiveName)));
        Assert.False(File.Exists(Path.Combine(Output, "stale.txt")));
    }

    [Fact]
    public async Task ManifestOnly_KeepsTheManifestAlone()
    {
        _parameters["mode"] = "manifest-only";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(["agent-release-manifest.json"], Directory.EnumerateFileSystemEntries(Output).Select(Path.GetFileName));
    }

    [Fact]
    public async Task TheCiDefaults_ReadTheDownloadsAndBuildSourceVersion()
    {
        _parameters.Remove("source");
        _parameters.Remove("commit");
        _parameters.Remove("protocol_policy");
        _env["BUILD_SOURCEVERSION"] = AgentReleaseFixture.Commit;

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("cp cid123:/app/wwwroot/downloads/. " + Output, _docker[1]);
    }

    [Fact]
    public async Task WithoutCommitOrBuildSourceVersion_IsRefused()
    {
        _parameters.Remove("commit");
        // Empty, so a BUILD_SOURCEVERSION of the process running the tests (a CI agent) is not read.
        _env["BUILD_SOURCEVERSION"] = string.Empty;

        Assert.Equal("Expected source commit must be hexadecimal.", await FailureAsync());
        Assert.Empty(_docker);
    }

    [Fact]
    public async Task AFailedCopy_FailsAndStillRemovesTheContainer()
    {
        _copyExit = 1;

        Assert.StartsWith("docker cp of /app/wwwroot/downloads/ out of", await FailureAsync(), StringComparison.Ordinal);
        Assert.Equal("rm -f cid123", _docker[^1]);
    }

    [Theory]
    [InlineData("mode", "partial", "Output mode must be full or manifest-only.")]
    [InlineData("protocol_policy", "latest", "Protocol policy must be upgrade-bridge or historical-compatible.")]
    [InlineData("commit", "not-hex", "Expected source commit must be hexadecimal.")]
    [InlineData("output", "../outside", "The output directory must stay inside the workspace: ")]
    public async Task Arguments_AreRefusedBeforeDockerRuns(string parameter, string value, string reason)
    {
        _parameters[parameter] = value;

        Assert.StartsWith(reason, await FailureAsync(), StringComparison.Ordinal);
        Assert.Empty(_docker);
    }

    [Fact]
    public async Task AnotherCommit_IsRefused()
    {
        _manifest["commit"] = "ffff";

        Assert.Equal($"Agent manifest commit ffff does not match {AgentReleaseFixture.Commit}.", await FailureAsync());
    }

    [Fact]
    public async Task AStringProtocol_IsNotProtocolTwo()
    {
        _manifest["protocolVersion"] = "2";

        Assert.Equal("Agent manifest does not expose the required protocol 1-2 upgrade bridge.", await FailureAsync());
    }

    [Theory]
    [InlineData(2, 2, 2, "upgrade-bridge", "Agent manifest does not expose the required protocol 1-2 upgrade bridge.")]
    [InlineData(1, 2, 3, "upgrade-bridge", "Agent manifest does not expose the required protocol 1-2 upgrade bridge.")]
    [InlineData(3, 3, 2, "historical-compatible", "Historical agent manifest is not compatible with protocol 2.")]
    [InlineData(1, 3, 2, "historical-compatible", "Historical agent manifest is not compatible with protocol 2.")]
    public async Task AProtocolOutsideThePolicy_IsRefused(int minimum, int maximum, int protocol, string policy, string message)
    {
        _parameters["protocol_policy"] = policy;
        _manifest["minimumSupportedProtocol"] = minimum;
        _manifest["maximumSupportedProtocol"] = maximum;
        _manifest["protocolVersion"] = protocol;

        Assert.Equal(message, await FailureAsync());
    }

    [Fact]
    public async Task HistoricalCompatible_AcceptsTwoToTwo()
    {
        _manifest["minimumSupportedProtocol"] = 2;

        Assert.Equal(0, (await RunAsync()).Result.ExitCode);
    }

    [Fact]
    public async Task WithoutAiRun_IsRefused()
    {
        _manifest["softwareCapabilities"] = new JsonArray("shell");

        Assert.Equal("Agent manifest does not publish the ai.run capability.", await FailureAsync());
    }

    [Theory]
    [InlineData("sizeBytes")]
    [InlineData("sha256")]
    public async Task AnArchiveThatDoesNotMatch_IsRefused(string property)
    {
        var archive = _manifest["archives"]![0]!.AsObject();
        archive[property] = property == "sizeBytes" ? 1 : new string('0', 64);

        Assert.Equal($"Agent archive integrity mismatch: {AgentReleaseFixture.ArchiveName}.", await FailureAsync());
    }

    [Fact]
    public async Task AMissingArchive_IsRefusedWithItsName()
    {
        _manifest["archives"]![0]!["fileName"] = "absent.tar.gz";

        Assert.Equal("Agent archive absent.tar.gz is missing.", await FailureAsync());
    }

    [Fact]
    public async Task AManifestWithoutArchives_IsRefused()
    {
        _manifest.Remove("archives");

        Assert.Equal("Agent manifest does not list its archives.", await FailureAsync());
    }
}
