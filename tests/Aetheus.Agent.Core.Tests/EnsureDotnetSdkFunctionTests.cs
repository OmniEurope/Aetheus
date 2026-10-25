// SPDX-License-Identifier: EUPL-1.2
using System.Security.Cryptography;
using System.Text;
using Aetheus.Agent.Core.Functions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>scripts/ensure-dotnet-sdk.sh</c>, run through the real executor on a temp
/// directory unique to each test: manifest checks, the host SDK kept when it resolves the exact pinned
/// version from the manifest's directory, the version-isolated cache, the pinned installer (digest,
/// bash, exit code, post-install check) and the step timeout reaching a running tool. One test runs
/// the real host <c>dotnet</c>.
/// </summary>
public sealed class EnsureDotnetSdkFunctionTests : IDisposable
{
    private const string Pinned = "10.0.100";
    private static readonly byte[] InstallerBytes = Encoding.UTF8.GetBytes("#!/bin/bash\necho fake installer\n");

    private readonly FunctionTestHarness _harness = new("aetheus-dotnet-sdk");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);

    public EnsureDotnetSdkFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["HOME"] = Path.Combine(_harness.Root, "home");
    }

    public void Dispose() => _harness.Dispose();

    private static string DotnetName => OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";

    private static PinnedDownload FakeInstaller =>
        new(new Uri("https://example.invalid/dotnet-install.sh"), Convert.ToHexStringLower(SHA256.HashData(InstallerBytes)));

    private void WriteGlobalJson(string version) =>
        _harness.Write("ws/global.json", $$"""
            {
              "sdk": {
                "version": "{{version}}",
                "rollForward": "latestFeature"
              }
            }
            """);

    /// <summary>Every <c>dotnet --version</c> answers <paramref name="reported"/>, keyed by executable.</summary>
    private void AnswerVersions(Dictionary<string, string> reported, int installerExit = 0, Action? onInstall = null) =>
        _tools.OnRun = (process, _) =>
        {
            if (process.ArgumentList.Contains("--install-dir"))
            {
                onInstall?.Invoke();
                return Task.FromResult((installerExit, new[] { "installer output" }));
            }
            return Task.FromResult(reported.TryGetValue(process.FileName, out var version)
                ? (0, new[] { version })
                : (1, Array.Empty<string>()));
        };

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync(
        PinnedDownload? installer = null, int timeoutSeconds = 30) =>
        _harness.RunAsync(new EnsureDotnetSdkFunction(_tools, installer ?? FakeInstaller), _env, timeoutSeconds);

    private string FailureReason(Executors.ExecutorResult result)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    [Fact]
    public async Task MissingManifest_FailsLikeTheScript()
    {
        var (result, variables) = await RunAsync();

        Assert.Equal("Function 'ensure-dotnet-sdk' failed: Pinned SDK manifest not found: global.json", FailureReason(result));
        Assert.Empty(variables);
    }

    [Fact]
    public async Task EmptyManifest_IsTreatedAsMissing()
    {
        _harness.Write("ws/global.json", string.Empty);

        var (result, _) = await RunAsync();

        Assert.EndsWith("Pinned SDK manifest not found: global.json", FailureReason(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("10.0")]
    [InlineData("10.0.100-rc.1")]
    [InlineData("")]
    public async Task InexactVersion_FailsLikeTheScript(string version)
    {
        WriteGlobalJson(version);

        var (result, _) = await RunAsync();

        Assert.EndsWith("Pinned SDK version is missing or invalid in global.json", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public void ReadVersion_TakesTheFirstVersionLine_AsSedHeadDoes()
    {
        string[] lines = ["{", "  \"sdk\": {", "\t\"version\" :  \"10.0.101\",", "  \"version\": \"9.0.100\"", "}"];

        Assert.Equal("10.0.101", PinnedDotnetSdk.ReadVersion(lines));
        Assert.Null(PinnedDotnetSdk.ReadVersion(["{ \"sdk\": { \"version\": \"10.0.100\" } }"]));
    }

    [Fact]
    public async Task HostSdkResolvingTheExactVersion_IsPublishedWithoutInstalling()
    {
        WriteGlobalJson(Pinned);
        var system = _harness.Executable("bin/dotnet");
        AnswerVersions(new() { [system] = Pinned });

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(system, variables["DOTNET"]);
        var probe = Assert.Single(_tools.Runs);
        Assert.Equal(["--version"], probe.ArgumentList);
        Assert.Equal(_harness.Workspace, probe.WorkingDirectory);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task ManifestInASubdirectory_IsProbedFromItsOwnDirectory()
    {
        _harness.Write("ws/sub/global.json", $"{{ \"sdk\": {{\n  \"version\": \"{Pinned}\" }} }}");
        var system = _harness.Executable("bin/dotnet");
        AnswerVersions(new() { [system] = Pinned });

        _env[PipelineFunctionVariables.ParametersJson] = "{\"global_json\":\"sub/global.json\"}";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Path.Combine(_harness.Workspace, "sub"), Assert.Single(_tools.Runs).WorkingDirectory);
    }

    [Fact]
    public async Task HostSdkOnAnotherVersion_WithoutHome_FailsBeforeInstalling()
    {
        WriteGlobalJson(Pinned);
        var system = _harness.Executable("bin/dotnet");
        AnswerVersions(new() { [system] = "10.0.200" });
        _env["HOME"] = string.Empty;

        var (result, _) = await RunAsync();

        Assert.EndsWith("HOME is required to install the pinned .NET SDK without root", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task CachedSdkInTheDotnetRoot_IsUsed()
    {
        WriteGlobalJson(Pinned);
        _env["AETHEUS_DOTNET_ROOT"] = Path.Combine(_harness.Root, "cache");
        var cached = _harness.Executable(Path.Combine("cache", Pinned, DotnetName));
        AnswerVersions(new() { [cached] = Pinned });

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(cached, variables["DOTNET"]);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task NoUsableSdk_InstallsThePinnedVersionInTheHomeCache()
    {
        WriteGlobalJson(Pinned);
        var bash = _harness.Executable("bin/bash");
        var installDirectory = Path.Combine(_harness.Root, "home", ".aetheus", "dotnet", Pinned);
        var installed = Path.Combine(installDirectory, DotnetName);
        _tools.DownloadContent = InstallerBytes;
        AnswerVersions(new() { [installed] = Pinned }, onInstall: () =>
        {
            File.WriteAllText(installed, string.Empty);
            FunctionTestHarness.MakeExecutable(installed);
        });

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(installed, variables["DOTNET"]);
        Assert.Equal(FakeInstaller.Source, Assert.Single(_tools.Downloads));
        var install = _tools.Runs.Single(run => run.FileName == bash);
        Assert.Equal(["--version", Pinned, "--install-dir", installDirectory, "--no-path"], install.ArgumentList.Skip(1));
        Assert.False(File.Exists(install.ArgumentList[0]), "The downloaded installer is removed after use.");
        Assert.Contains(_harness.Log, entry => entry.Line == $"Installing pinned .NET SDK {Pinned} in {installDirectory}");
    }

    [Fact]
    public async Task InstallerWithAnotherDigest_IsNeverRun()
    {
        WriteGlobalJson(Pinned);
        _harness.Executable("bin/bash");
        _tools.DownloadContent = Encoding.UTF8.GetBytes("tampered");
        AnswerVersions([]);

        var (result, _) = await RunAsync();

        Assert.Contains("does not match its pinned SHA-256", FailureReason(result), StringComparison.Ordinal);
        Assert.DoesNotContain(_tools.Runs, run => run.ArgumentList.Contains("--install-dir"));
    }

    [Fact]
    public async Task OfficialInstallerPin_IsTheScriptsCommitAndDigest()
    {
        Assert.Equal(
            "https://raw.githubusercontent.com/dotnet/install-scripts/da3ce11ba63f3dbb0fb835d41bda2665d5c48e84/src/dotnet-install.sh",
            PinnedDotnetSdk.OfficialInstaller.Source.ToString());
        Assert.Equal("082f7685e156738a1b2e2ed8381a621870d4ce8e8c59278034556f05c186eb2e", PinnedDotnetSdk.OfficialInstaller.Sha256);

        // The default constructor uses that pin: the download it attempts is the official URL.
        WriteGlobalJson(Pinned);
        _harness.Executable("bin/bash");
        AnswerVersions([]);
        var (result, _) = await RunAsync(PinnedDotnetSdk.OfficialInstaller);

        Assert.Contains("does not match its pinned SHA-256 (082f7685", FailureReason(result), StringComparison.Ordinal);
        Assert.Equal(PinnedDotnetSdk.OfficialInstaller.Source, Assert.Single(_tools.Downloads));
    }

    [Fact]
    public async Task MissingBash_FailsLikeTheScript()
    {
        WriteGlobalJson(Pinned);
        AnswerVersions([]);

        var (result, _) = await RunAsync();

        Assert.EndsWith("bash is required to run the official .NET SDK installer", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(_tools.Downloads);
    }

    [Fact]
    public async Task FailingInstaller_ReportsItsExitCode()
    {
        WriteGlobalJson(Pinned);
        _harness.Executable("bin/bash");
        _tools.DownloadContent = InstallerBytes;
        AnswerVersions([], installerExit: 3);

        var (result, _) = await RunAsync();

        Assert.EndsWith("The official .NET SDK installer failed with exit code 3", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstalledSdkNotReportingThePinnedVersion_FailsVerification()
    {
        WriteGlobalJson(Pinned);
        _harness.Executable("bin/bash");
        var installed = Path.Combine(_harness.Root, "home", ".aetheus", "dotnet", Pinned, DotnetName);
        _tools.DownloadContent = InstallerBytes;
        AnswerVersions(new() { [installed] = "10.0.101" }, onInstall: () =>
        {
            File.WriteAllText(installed, string.Empty);
            FunctionTestHarness.MakeExecutable(installed);
        });

        var (result, variables) = await RunAsync();

        Assert.EndsWith($"Pinned .NET SDK {Pinned} installation could not be verified", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task StepTimeout_ReachesTheRunningTool()
    {
        WriteGlobalJson(Pinned);
        _harness.Executable("bin/dotnet");
        var cancelled = new TaskCompletionSource();
        _tools.OnRun = async (_, token) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                throw;
            }
            return (0, []);
        };

        var (result, variables) = await RunAsync(timeoutSeconds: 1);

        Assert.True(result.TimedOut);
        // The executor returns as soon as the token fires; the tool observes it on its own thread.
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Empty(variables);
    }

}
