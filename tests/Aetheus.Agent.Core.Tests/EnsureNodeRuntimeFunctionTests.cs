// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The <c>ensure-node-runtime</c> function, port of <c>deploy/scripts/ensure-node-runtime.sh</c>, run
/// through the real executor: the pinned host <c>node</c> published as <c>NODE</c> with its directory as
/// <c>NODE_BIN_DIR</c>, the pinned archive installed under <c>$HOME/.aetheus</c> otherwise, and the
/// script's refusals. The resolution rules themselves are <see cref="PinnedNodeRuntime"/>'s, also
/// exercised by <see cref="RestoreJavaScriptAnalysisDependenciesFunctionTests"/>.
/// </summary>
public sealed class EnsureNodeRuntimeFunctionTests : IDisposable
{
    private static readonly byte[] ArchiveBytes = Encoding.UTF8.GetBytes("fake node archive");

    private readonly FunctionTestHarness _harness = new("aetheus-ensure-node");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private string _reportedVersion = "v" + PinnedNodeRuntime.Version;

    public EnsureNodeRuntimeFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["HOME"] = Path.Combine(_harness.Root, "home");
        _tools.OnRun = Answer;
        _tools.DownloadContent = ArchiveBytes;
    }

    public void Dispose() => _harness.Dispose();

    private static PinnedDownload FakeArchive =>
        new(new Uri("https://example.invalid/node.tar.gz"), Convert.ToHexStringLower(SHA256.HashData(ArchiveBytes)));

    /// <summary>A host node reports <see cref="_reportedVersion"/>, an installed one the pinned version;
    /// tar lays out bin/node in the staging directory.</summary>
    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var name = Path.GetFileName(process.FileName);
        if (name == "node")
            return Task.FromResult((0, new[] { process.FileName.StartsWith(Path.Combine(_harness.Root, "bin"), StringComparison.Ordinal) ? _reportedVersion : "v" + PinnedNodeRuntime.Version }));
        if (name == "tar")
        {
            var node = Path.Combine(process.ArgumentList[^1], "bin", "node");
            Directory.CreateDirectory(Path.GetDirectoryName(node)!);
            File.WriteAllText(node, string.Empty);
            FunctionTestHarness.MakeExecutable(node);
            return Task.FromResult((0, Array.Empty<string>()));
        }
        return Task.FromResult((127, Array.Empty<string>()));
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync() =>
        _harness.RunAsync(new EnsureNodeRuntimeFunction(_tools, FakeArchive), _env);

    [Fact]
    public async Task PinnedHostNode_IsPublishedWithItsDirectory()
    {
        var node = _harness.Executable("bin/node");

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(node, variables["NODE"]);
        Assert.Equal(Path.Combine(_harness.Root, "bin"), variables["NODE_BIN_DIR"]);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task OtherHostVersion_OnLinuxX64_InstallsThePinnedArchiveUnderHome()
    {
        _harness.Executable("bin/node");
        _harness.Executable("bin/tar");
        _reportedVersion = "v22.0.0";
        _tools.IsLinuxX64 = true;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        var installed = Path.Combine(_harness.Root, "home", ".aetheus", $"node-v{PinnedNodeRuntime.Version}-linux-x64", "bin", "node");
        Assert.Equal(installed, variables["NODE"]);
        Assert.Equal(Path.GetDirectoryName(installed), variables["NODE_BIN_DIR"]);
        Assert.Single(_tools.Downloads);
    }

    [Fact]
    public async Task NoPinnedNode_OutsideLinuxX64_FailsLikeTheScriptWithoutPublishing()
    {
        _reportedVersion = "v22.0.0";
        _harness.Executable("bin/node");

        var (result, variables) = await RunAsync();

        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        Assert.EndsWith($"Pinned Node.js {PinnedNodeRuntime.Version} bootstrap supports Linux x86_64 only", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(variables);
    }
}
