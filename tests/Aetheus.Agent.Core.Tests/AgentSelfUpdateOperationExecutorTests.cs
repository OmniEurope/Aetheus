// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Aetheus.Agent.Core.Configuration;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Aetheus.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

[Collection(ProcessSpawningTestCollection.Name)]
public class AgentSelfUpdateOperationExecutorTests : IDisposable
{
    private readonly string _workDir = Directory.CreateTempSubdirectory("agent-update-test-").FullName;
    private readonly IServerApiClient _api = Substitute.For<IServerApiClient>();
    private readonly AgentState _state = new() { ServerId = 7 };

    public void Dispose()
    {
        try { Directory.Delete(_workDir, recursive: true); } catch (Exception) { /* best effort */ }
    }

    private sealed class StubHandler(HttpStatusCode status, byte[]? body, string? sha, long? contentLength) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var resp = new HttpResponseMessage(status) { Content = new ByteArrayContent(body ?? []) };
            if (contentLength.HasValue) resp.Content.Headers.ContentLength = contentLength.Value;
            // X-Content-SHA256 must go on the RESPONSE (message) headers, NOT Content.Headers: the
            // backend sets it via httpContext.Response.Headers and the agent reads it from
            // response.Headers.TryGetValues. Putting it on Content.Headers (the old stub) made every
            // "checksum" test silently hit the missing-header branch, so the real hash-comparison
            // happy path was never exercised.
            if (sha is not null) resp.Headers.TryAddWithoutValidation("X-Content-SHA256", sha);
            return Task.FromResult(resp);
        }
    }

    private AgentSelfUpdateOperationExecutor Build(
        HttpStatusCode status, byte[]? body, string? sha, bool allowInsecure, long? contentLength = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ =>
            new HttpClient(new StubHandler(status, body, sha, contentLength)));

        var options = Options.Create(new AetheusAgentOptions
        {
            WorkDirectory = _workDir,
            ServerUrl = "https://backend.test",
            AllowInsecureCerts = allowInsecure
        });

        return new AgentSelfUpdateOperationExecutor(
            factory, options, _api, _state,
            NullLogger<AgentSelfUpdateOperationExecutor>.Instance);
    }

    private Task<ExecutorResult> Run(AgentSelfUpdateOperationExecutor sut) =>
        sut.ExecuteAsync(OperationKind.AgentSelfUpdate, "", 60, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

    private Task<ExecutorResult> RunLegacyDownload(AgentSelfUpdateOperationExecutor sut)
    {
        sut.IsWindowsOverride = true;
        return Run(sut);
    }

    [Fact]
    public void CanHandle_OnlyAgentSelfUpdate()
    {
        var sut = Build(HttpStatusCode.OK, [1], null, false);
        Assert.True(sut.CanHandle(OperationKind.AgentSelfUpdate));
        Assert.False(sut.CanHandle(OperationKind.ServiceStart));
    }

    [Fact]
    public void IsSameOrChildPath_RejectsOnlyPathsInsideInstallRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "aetheus-install");

        Assert.True(AgentSelfUpdateFileSystem.IsSameOrChildPath(root, root));
        Assert.True(AgentSelfUpdateFileSystem.IsSameOrChildPath(Path.Combine(root, "work"), root));
        Assert.False(AgentSelfUpdateFileSystem.IsSameOrChildPath(root + "-other", root));
    }

    [Fact]
    public async Task Execute_WrongKind_ReturnsFailure()
    {
        var sut = Build(HttpStatusCode.OK, [1], null, false);

        var result = await sut.ExecuteAsync(OperationKind.ServiceStart, "", 60, (_, _) => Task.CompletedTask, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Execute_DownloadHttpError_ReturnsFailure()
    {
        var result = await RunLegacyDownload(Build(HttpStatusCode.NotFound, null, null, false));

        Assert.Equal(-1, result.ExitCode);
        await _api.Received().ReportUpdateProgressAsync(7, Arg.Any<Aetheus.Shared.Components.Servers.AgentUpdateProgressReport>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_DownloadFailure_PreservesLastKnownRollback()
    {
        var rollbackDir = Directory.CreateDirectory(Path.Combine(_workDir, ".agent-rollback"));
        var marker = Path.Combine(rollbackDir.FullName, "previous-agent.dll");
        await File.WriteAllTextAsync(marker, "last-known-good", TestContext.Current.CancellationToken);

        var result = await RunLegacyDownload(Build(HttpStatusCode.NotFound, null, null, false));

        Assert.Equal(-1, result.ExitCode);
        Assert.Equal("last-known-good", await File.ReadAllTextAsync(
            marker, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Execute_EmptyArchive_ReturnsFailure()
    {
        var result = await RunLegacyDownload(Build(HttpStatusCode.OK, [], null, false));

        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Execute_MissingChecksumHeader_Rejected_WhenNotInsecure()
    {
        var result = await RunLegacyDownload(Build(HttpStatusCode.OK, [1, 2, 3, 4], sha: null, allowInsecure: false));

        Assert.Equal(-1, result.ExitCode);
    }

    [Fact]
    public async Task Execute_InvalidChecksum_Rejected()
    {
        var result = await RunLegacyDownload(Build(HttpStatusCode.OK, [1, 2, 3, 4], sha: "DEADBEEF", allowInsecure: false));

        Assert.Equal(-1, result.ExitCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Execute_AnArchiveThatDisagreesWithTheReleaseManifest_IsRejectedBeforeDownload(bool checksumDiffers)
    {
        var archive = BuildTarGzip(("Aetheus.Agent.Linux.dll", "new-agent"));
        var sha = Convert.ToHexString(SHA256.HashData(archive));
        var sut = checksumDiffers
            ? BuildVersionedLinux(archive, sha, servedSha: new string('0', 64))
            : BuildVersionedLinux(archive, sha, manifestSize: archive.LongLength + 1);
        sut.IsWindowsOverride = false;
        var output = new List<string>();

        var result = await sut.ExecuteAsync(
            OperationKind.AgentSelfUpdate, "",
            new Dictionary<string, string> { ["AETHEUS_AGENT_TARGET_VERSION"] = VersionedLinuxHandler.Version },
            60, (line, _) => { output.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains(
            checksumDiffers
                ? "Update archive checksum header does not match the immutable release manifest."
                : $"Update archive size differs from manifest: expected {archive.LongLength + 1}, got {archive.LongLength}.",
            output);
        Assert.False(File.Exists(Path.Combine(_workDir, ".agent-update", VersionedLinuxHandler.FileName)));
    }

    [Fact]
    public async Task Execute_MissingChecksumHeader_IsAcceptedWithAWarning_OnlyInInsecureDevMode()
    {
        var output = new List<string>();

        var sut = Build(HttpStatusCode.OK, [1, 2, 3, 4], sha: null, allowInsecure: true);
        sut.IsWindowsOverride = true;

        var result = await sut
            .ExecuteAsync(OperationKind.AgentSelfUpdate, "", 60,
                (line, _) => { output.Add(line); return Task.CompletedTask; }, TestContext.Current.CancellationToken);

        // Accepted past the integrity gate, then refused by the archive itself (four bytes are no archive).
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("WARNING: no X-Content-SHA256 header - accepted because AllowInsecureCerts=true (dev mode)", output);
        Assert.DoesNotContain("update rejected: missing X-Content-SHA256", output);
    }

    [Fact]
    public async Task Execute_OversizedContentLength_IsRejectedBeforeDownload()
    {
        var result = await RunLegacyDownload(Build(
            HttpStatusCode.OK,
            [1],
            sha: new string('A', 64),
            allowInsecure: false,
            contentLength: AgentReleaseArchiveResolver.MaxArchiveBytes + 1));

        Assert.Equal(-1, result.ExitCode);
        Assert.False(File.Exists(Path.Combine(
            _workDir, ".agent-update", "aetheus-agent-win-x64.zip")));
    }

    [Fact]
    public async Task Execute_ValidChecksum_PassesIntegrityGate_ThenFailsAtExtraction()
    {
        // Exercises the REAL fail-closed integrity happy path: a matching X-Content-SHA256 header
        // (on response.Headers, where the agent reads it) passes the gate, THEN extraction of
        // non-archive bytes fails. Proof the gate passed: the Downloaded phase (reported only AFTER
        // DownloadAsync returns true, i.e. after the hash comparison succeeds) fired.
        var body = new byte[] { 1, 2, 3, 4, 5 };
        var sha = Convert.ToHexString(SHA256.HashData(body));
        var sut = BuildVersionedLinux(body, sha);
        sut.IsWindowsOverride = false;

        var result = await RunVersionedLinux(sut);

        Assert.Equal(-1, result.ExitCode);
        await _api.Received().ReportUpdateProgressAsync(7,
            Arg.Is<Aetheus.Shared.Components.Servers.AgentUpdateProgressReport>(r => r.Phase == AgentUpdatePhase.Downloaded),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ValidArchive_DownloadsAndExtracts_ThenFailsStagingWithoutMarkerBinary()
    {
        // Exercises the real download hash check and archive extraction without launching the
        // binary swap. Omitting the expected agent executable stops the operation at staging
        // validation.
        var archive = BuildTarGzip(("readme.txt", "not the agent binary"));
        var sha = Convert.ToHexString(SHA256.HashData(archive));
        var sut = BuildVersionedLinux(archive, sha);
        sut.IsWindowsOverride = false;

        var result = await RunVersionedLinux(sut);

        Assert.Equal(-1, result.ExitCode);
        // Proof the download SHA gate passed and extraction actually ran: those phases were reported.
        await _api.Received().ReportUpdateProgressAsync(7,
            Arg.Is<Aetheus.Shared.Components.Servers.AgentUpdateProgressReport>(r => r.Phase == AgentUpdatePhase.Downloaded),
            Arg.Any<CancellationToken>());
        await _api.Received().ReportUpdateProgressAsync(7,
            Arg.Is<Aetheus.Shared.Components.Servers.AgentUpdateProgressReport>(r => r.Phase == AgentUpdatePhase.Extracting),
            Arg.Any<CancellationToken>());
        // And it failed at staging validation, not before extraction.
        await _api.Received().ReportUpdateProgressAsync(7,
            Arg.Is<Aetheus.Shared.Components.Servers.AgentUpdateProgressReport>(r => r.Phase == AgentUpdatePhase.Failed && r.Message == "staged payload invalid"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Execute_ValidWindowsArchive_ReachesDetachedSwapHandoffWithoutLaunchingAProcess()
    {
        var installDir = Directory.CreateDirectory(Path.Combine(_workDir, "isolated-install"));
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"),
            "old-agent",
            TestContext.Current.CancellationToken);
        var archive = BuildZip(
            ("Aetheus.Agent.Windows.exe", "new-agent"),
            ("dependency.dll", "new-dependency"));
        var sha = Convert.ToHexString(SHA256.HashData(archive));
        string? launchedScript = null;
        IReadOnlyList<string>? launchedArguments = null;
        var sut = Build(HttpStatusCode.OK, archive, sha, allowInsecure: false);
        sut.IsWindowsOverride = true;
        sut.InstallDirectoryOverride = installDir.FullName;
        sut.DetachedLaunchOverride = (script, arguments) =>
        {
            launchedScript = script;
            launchedArguments = arguments;
        };

        var result = await Run(sut);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(launchedScript);
        Assert.True(File.Exists(launchedScript));
        Assert.NotNull(launchedArguments);
        Assert.Equal(4, launchedArguments.Count);
        Assert.Equal(installDir.FullName, launchedArguments[2]);
        Assert.True(File.Exists(Path.Combine(launchedArguments[1], "Aetheus.Agent.Windows.exe")));
        Assert.Equal("old-agent", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"),
            TestContext.Current.CancellationToken));
        await _api.Received().ReportUpdateProgressAsync(7,
            Arg.Is<Aetheus.Shared.Components.Servers.AgentUpdateProgressReport>(
                report => report.Phase == AgentUpdatePhase.LaunchingUpdater),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SnapshotRollback_KeepsPreviousBinariesButNotConfiguration()
    {
        var installDir = Directory.CreateDirectory(Path.Combine(_workDir, "install"));
        var rollbackDir = Path.Combine(_workDir, "rollback");
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"), "binary", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "appsettings.json"), "secret", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "stale.bak"), "stale", TestContext.Current.CancellationToken);

        AgentSelfUpdateFileSystem.SnapshotRollback(installDir.FullName, rollbackDir);

        Assert.True(File.Exists(Path.Combine(rollbackDir, "Aetheus.Agent.Linux.dll")));
        Assert.True(File.Exists(Path.Combine(rollbackDir, "rollback-version.txt")));
        Assert.False(File.Exists(Path.Combine(rollbackDir, "appsettings.json")));
        Assert.False(File.Exists(Path.Combine(rollbackDir, "stale.bak")));
    }

    [Fact]
    public async Task ValidateArchive_RejectsPathTraversal()
    {
        var archivePath = Path.Combine(_workDir, "traversal.zip");
        await File.WriteAllBytesAsync(archivePath, BuildZip(("../escape.txt", "forbidden")),
            TestContext.Current.CancellationToken);
        var extractDir = Directory.CreateDirectory(Path.Combine(_workDir, "extract-traversal"));

        var valid = AgentSelfUpdateOperationExecutor.ValidateArchive(
            archivePath, extractDir.FullName, isWindows: true, out var error);

        Assert.False(valid);
        Assert.Contains("escapes", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(Path.Combine(_workDir, "escape.txt")));
    }

    [Fact]
    public void ValidateArchive_RejectsSymbolicLinkEntries()
    {
        var archivePath = Path.Combine(_workDir, "symlink.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("Aetheus.Agent.Windows.exe");
            entry.ExternalAttributes = 0xA000 << 16;
            using var writer = new StreamWriter(entry.Open());
            writer.Write("target");
        }
        var extractDir = Directory.CreateDirectory(Path.Combine(_workDir, "extract-symlink"));

        var valid = AgentSelfUpdateOperationExecutor.ValidateArchive(
            archivePath, extractDir.FullName, isWindows: true, out var error);

        Assert.False(valid);
        Assert.Contains("symbolic link", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ValidateArchive_RejectsExcessiveEntryCount()
    {
        var archivePath = Path.Combine(_workDir, "too-many-entries.zip");
        using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
        {
            for (var index = 0; index <= AgentSelfUpdateOperationExecutor.MaxArchiveEntries; index++)
                archive.CreateEntry($"payload/{index}.txt");
        }
        var extractDir = Directory.CreateDirectory(Path.Combine(_workDir, "extract-too-many"));

        var valid = AgentSelfUpdateOperationExecutor.ValidateArchive(
            archivePath, extractDir.FullName, isWindows: true, out var error);

        Assert.False(valid);
        Assert.Contains("entries", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReplaceInstallFilesWithRollback_PartialFailureRestoresPreviousBinariesAndConfiguration()
    {
        var installDir = Directory.CreateDirectory(Path.Combine(_workDir, "install-partial"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(_workDir, "source-partial"));
        var rollbackDir = Path.Combine(_workDir, "rollback-partial");
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"), "old-agent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "dependency.dll"), "old-dependency",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "appsettings.json"), "operator-config",
            TestContext.Current.CancellationToken);
        var nestedInstall = Directory.CreateDirectory(Path.Combine(installDir.FullName, "runtimes", "linux-x64", "native"));
        await File.WriteAllTextAsync(Path.Combine(nestedInstall.FullName, "native.so"), "old-native",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sourceDir.FullName, "a-new.dll"), "new-file",
            TestContext.Current.CancellationToken);
        var nestedSource = Directory.CreateDirectory(Path.Combine(sourceDir.FullName, "runtimes", "linux-x64", "native"));
        await File.WriteAllTextAsync(Path.Combine(nestedSource.FullName, "new-native.so"), "new-native",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sourceDir.FullName, "z-blocked.dll"), "copy-must-fail",
            TestContext.Current.CancellationToken);
        AgentSelfUpdateFileSystem.SnapshotRollback(installDir.FullName, rollbackDir);

        var error = Assert.Throws<InvalidOperationException>(() =>
            AgentSelfUpdateFileSystem.ReplaceInstallFilesWithRollback(
                sourceDir.FullName, installDir.FullName, rollbackDir,
                relativePath =>
                {
                    if (relativePath == "z-blocked.dll") throw new IOException("injected copy failure");
                }));

        Assert.Contains("restored automatically", error.Message, StringComparison.Ordinal);
        Assert.Equal("old-agent", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"), TestContext.Current.CancellationToken));
        Assert.Equal("old-dependency", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "dependency.dll"), TestContext.Current.CancellationToken));
        Assert.Equal("old-native", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "runtimes", "linux-x64", "native", "native.so"),
            TestContext.Current.CancellationToken));
        Assert.Equal("operator-config", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "appsettings.json"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(installDir.FullName, "a-new.dll")));
        Assert.False(File.Exists(Path.Combine(
            installDir.FullName, "runtimes", "linux-x64", "native", "new-native.so")));
    }

    [Fact]
    public void WindowsUpdater_ContainsFailClosedRestoreAndParsesOnWindows()
    {
        var stagingRoot = Directory.CreateDirectory(Path.Combine(_workDir, "windows-updater"));

        var updater = AgentSelfUpdateOperationExecutor.WriteWindowsUpdater(stagingRoot.FullName);
        var script = File.ReadAllText(updater);

        Assert.Contains("$RollbackReady = $true", script, StringComparison.Ordinal);
        Assert.Contains("previous binaries restored automatically", script, StringComparison.Ordinal);
        Assert.Contains("automatic rollback also failed", script, StringComparison.Ordinal);
        Assert.Contains("$ConfirmationTimeoutSeconds", script, StringComparison.Ordinal);
        Assert.Contains(".agent-update-pending", script, StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id $NewPid", script, StringComparison.Ordinal);
        Assert.True(
            script.IndexOf("Copy-Item -Path (Join-Path $Src '*')", StringComparison.Ordinal)
            < script.IndexOf("Get-ChildItem -LiteralPath $Rollback -Force", StringComparison.Ordinal));

        if (!OperatingSystem.IsWindows()) return;

        var parser = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        parser.ArgumentList.Add("-NoProfile");
        parser.ArgumentList.Add("-NonInteractive");
        parser.ArgumentList.Add("-Command");
        parser.ArgumentList.Add(
            "$errors = $null; [System.Management.Automation.Language.Parser]::ParseFile($env:AETHEUS_UPDATER_PATH, [ref]$null, [ref]$errors) | Out-Null; " +
            "if ($errors.Count -gt 0) { $errors | ForEach-Object { [Console]::Error.WriteLine($_.Message) }; exit 1 }");
        parser.Environment["AETHEUS_UPDATER_PATH"] = updater;
        using var process = Process.Start(parser);
        Assert.NotNull(process);
        var standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, standardError);
    }

    [PlatformFact("windows")]
    public async Task WindowsUpdater_ValidPayload_ReplacesBinariesAndPreservesConfiguration()
    {
        var stagingRoot = Directory.CreateDirectory(Path.Combine(_workDir, "windows-update-success"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(stagingRoot.FullName, "source"));
        var installDir = Directory.CreateDirectory(Path.Combine(_workDir, "windows-install-success"));
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"),
            "old-agent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "dependency.dll"),
            "old-dependency",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "appsettings.json"),
            "operator-config",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDir.FullName, "Aetheus.Agent.Windows.exe"),
            "new-agent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDir.FullName, "dependency.dll"),
            "new-dependency",
            TestContext.Current.CancellationToken);
        var updater = AgentSelfUpdateOperationExecutor.WriteWindowsUpdater(stagingRoot.FullName);
        var startInfo = BuildWindowsUpdaterStartInfo(updater, sourceDir.FullName, installDir.FullName);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var markerPath = Path.Combine(_workDir, ".agent-update-pending");
        // Waits for the updater marker after process startup, rollback snapshot and payload copy.
        // The timeout accommodates filesystem latency while a missing marker still fails the test.
        var markerDeadline = DateTime.UtcNow.AddSeconds(60);
        while (!File.Exists(markerPath) && DateTime.UtcNow < markerDeadline)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.True(File.Exists(markerPath));
        File.Delete(markerPath);
        var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.True(process.ExitCode == 0, standardError);
        Assert.Equal("new-agent", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"),
            TestContext.Current.CancellationToken));
        Assert.Equal("new-dependency", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "dependency.dll"),
            TestContext.Current.CancellationToken));
        Assert.Equal("operator-config", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "appsettings.json"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task LinuxRequester_CurrentPosture_WritesQualifiedAtomicRequest()
    {
        var postureVersion = Path.Combine(_workDir, "posture-version");
        var request = Path.Combine(_workDir, "posture-request");
        await File.WriteAllTextAsync(
            postureVersion,
            AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion,
            TestContext.Current.CancellationToken);
        const string qualifiedRequest =
            "1.0.1118\taetheus-agent-linux-x64-v1.0.1118.tar.gz\t43263953\t"
            + "a279fcb9a279fcb9a279fcb9a279fcb9a279fcb9a279fcb9a279fcb9a279fcb9\t"
            + "edf471f794b4aeabdad9a2ffeb6f12e31cf3b38a";

        var requested = await LinuxAgentPostureUpgradeRequester.TryRequestAsync(
            _workDir,
            postureVersion,
            request,
            AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion,
            qualifiedRequest,
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.True(requested);
        Assert.Equal(
            qualifiedRequest + "\n",
            await File.ReadAllTextAsync(request, TestContext.Current.CancellationToken));
        Assert.True(File.Exists(AgentUpdateRecoveryState.GetMarkerPath(_workDir)));
        Assert.False(File.Exists(request + ".tmp"));
    }

    // ADR-055: posture 4 is the supervisor that maps the eleven whitelisted tokens. A host still on posture 3
    // knows only patch-manage, so the agent never hands it a request that could carry the other tokens.
    [Fact]
    public async Task LinuxRequester_PostureThreeHost_IsRefusedAndNoRequestIsWritten()
    {
        var postureVersion = Path.Combine(_workDir, "posture-three-version");
        var request = Path.Combine(_workDir, "posture-three-request");
        await File.WriteAllTextAsync(postureVersion, "3\n", TestContext.Current.CancellationToken);

        var requested = await LinuxAgentPostureUpgradeRequester.TryRequestAsync(
            _workDir,
            postureVersion,
            request,
            AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion,
            "qualified-request",
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.Equal("4", AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion);
        Assert.False(requested);
        Assert.False(File.Exists(request));
    }

    [Fact]
    public async Task LinuxRequester_PostureFourHost_AcceptsTheRequest()
    {
        var postureVersion = Path.Combine(_workDir, "posture-four-version");
        var request = Path.Combine(_workDir, "posture-four-request");
        await File.WriteAllTextAsync(postureVersion, "4\n", TestContext.Current.CancellationToken);

        var requested = await LinuxAgentPostureUpgradeRequester.TryRequestAsync(
            _workDir,
            postureVersion,
            request,
            AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion,
            "qualified-request",
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

        Assert.True(requested);
        Assert.True(File.Exists(request));
    }

    [Fact]
    public async Task LinuxRequester_ObsoletePosture_FailsClosedBeforeWritingRequest()
    {
        var installDir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"aetheus-linux-install-{Guid.NewGuid():N}"));
        var postureVersion = Path.Combine(_workDir, "obsolete-posture-version");
        var request = Path.Combine(_workDir, "obsolete-posture-request");
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"),
            "old-agent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(postureVersion, "0", TestContext.Current.CancellationToken);

        try
        {
            var requested = await LinuxAgentPostureUpgradeRequester.TryRequestAsync(
                _workDir,
                postureVersion,
                request,
                AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion,
                "qualified-request",
                (_, _) => Task.CompletedTask,
                TestContext.Current.CancellationToken);

            Assert.False(requested);
            Assert.False(File.Exists(request));
            Assert.Equal(
                "old-agent",
                await File.ReadAllTextAsync(
                    Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(installDir.FullName, recursive: true);
        }
    }

    private sealed class VersionedLinuxHandler(byte[] archive, string sha, string? servedSha = null, long? manifestSize = null) : HttpMessageHandler
    {
        internal const string Version = "1.0.1118";
        internal const string FileName = "aetheus-agent-linux-x64-v1.0.1118.tar.gz";
        internal const string Commit = "edf471f794b4aeabdad9a2ffeb6f12e31cf3b38a";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/agent-release-manifest.json", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new AgentReleaseManifestDto
                    {
                        SoftwareVersion = Version,
                        ProtocolVersion = Aetheus.Shared.Components.Shared.AgentProtocol.CurrentVersion,
                        MinimumSupportedProtocol = Aetheus.Shared.Components.Shared.AgentProtocol.CurrentVersion - 1,
                        MaximumSupportedProtocol = Aetheus.Shared.Components.Shared.AgentProtocol.MaximumSupportedVersion,
                        Commit = Commit,
                        Archives =
                        [
                            new AgentReleaseArchiveDto
                            {
                                Platform = "linux",
                                Architecture = "x64",
                                FileName = FileName,
                                SizeBytes = manifestSize ?? archive.LongLength,
                                Sha256 = sha
                            }
                        ]
                    })
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archive)
            };
            response.Content.Headers.ContentLength = archive.LongLength;
            response.Headers.TryAddWithoutValidation("X-Content-SHA256", servedSha ?? sha);
            return Task.FromResult(response);
        }
    }

    private AgentSelfUpdateOperationExecutor BuildVersionedLinux(
        byte[] archive, string sha, string? servedSha = null, long? manifestSize = null)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ =>
            new HttpClient(new VersionedLinuxHandler(archive, sha, servedSha, manifestSize)));
        var options = Options.Create(new AetheusAgentOptions
        {
            WorkDirectory = _workDir,
            ServerUrl = "https://backend.test"
        });
        return new AgentSelfUpdateOperationExecutor(
            factory, options, _api, _state,
            NullLogger<AgentSelfUpdateOperationExecutor>.Instance);
    }

    private Task<ExecutorResult> RunVersionedLinux(AgentSelfUpdateOperationExecutor sut) =>
        sut.ExecuteAsync(
            OperationKind.AgentSelfUpdate,
            "",
            new Dictionary<string, string>
            {
                ["AETHEUS_AGENT_TARGET_VERSION"] = VersionedLinuxHandler.Version
            },
            60,
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

    [Fact]
    public async Task Execute_ValidLinuxArchive_RequestsQualifiedFullPostureUpgrade()
    {
        var installDir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"aetheus-linux-install-{Guid.NewGuid():N}"));
        var postureVersion = Path.Combine(_workDir, "executor-posture-version");
        var request = Path.Combine(_workDir, "executor-posture-request");
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"),
            "old-agent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            postureVersion,
            AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion,
            TestContext.Current.CancellationToken);
        var archive = BuildTarGzip(
            ("Aetheus.Agent.Linux.dll", "new-agent"),
            ("install-agent-linux.sh", "#!/bin/sh\nexit 0\n"));
        var sha = Convert.ToHexString(SHA256.HashData(archive));
        var sut = BuildVersionedLinux(archive, sha);
        sut.IsWindowsOverride = false;
        sut.InstallDirectoryOverride = installDir.FullName;
        sut.LinuxPostureVersionPathOverride = postureVersion;
        sut.LinuxUpdateRequestPathOverride = request;

        try
        {
            var result = await RunVersionedLinux(sut);

            Assert.Equal(0, result.ExitCode);
            var requestContent = await File.ReadAllTextAsync(
                request,
                TestContext.Current.CancellationToken);
            Assert.EndsWith("\n", requestContent, StringComparison.Ordinal);
            var fields = requestContent.TrimEnd('\n').Split('\t');
            Assert.Equal(5, fields.Length);
            Assert.Equal(VersionedLinuxHandler.Version, fields[0]);
            Assert.Equal(VersionedLinuxHandler.FileName, fields[1]);
            Assert.Equal(archive.LongLength.ToString(System.Globalization.CultureInfo.InvariantCulture), fields[2]);
            Assert.Equal(sha.ToLowerInvariant(), fields[3]);
            Assert.Equal(VersionedLinuxHandler.Commit, fields[4]);
            Assert.True(File.Exists(AgentUpdateRecoveryState.GetMarkerPath(_workDir)));
            Assert.Equal(
                "old-agent",
                await File.ReadAllTextAsync(
                    Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"),
                    TestContext.Current.CancellationToken));
        }
        finally
        {
            Directory.Delete(installDir.FullName, recursive: true);
        }
    }

    private Task<ExecutorResult> RunVersionedLinuxEnabling(AgentSelfUpdateOperationExecutor sut, string capabilities) =>
        sut.ExecuteAsync(
            OperationKind.AgentSelfUpdate,
            "",
            new Dictionary<string, string>
            {
                ["AETHEUS_AGENT_TARGET_VERSION"] = VersionedLinuxHandler.Version,
                [AgentEnableableCapabilities.EnvironmentVariable] = capabilities
            },
            60,
            (_, _) => Task.CompletedTask,
            TestContext.Current.CancellationToken);

    private async Task<(AgentSelfUpdateOperationExecutor Sut, DirectoryInfo InstallDir, string Request)> PrepareLinuxEnabling()
    {
        var installDir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"aetheus-linux-install-{Guid.NewGuid():N}"));
        var postureVersion = Path.Combine(_workDir, $"capabilities-posture-{Guid.NewGuid():N}");
        var request = Path.Combine(_workDir, $"capabilities-request-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"), "old-agent", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            postureVersion, AgentSelfUpdateOperationExecutor.LinuxIntegrationPostureVersion, TestContext.Current.CancellationToken);
        var archive = BuildTarGzip(
            ("Aetheus.Agent.Linux.dll", "new-agent"),
            ("install-agent-linux.sh", "#!/bin/sh\nexit 0\n"));
        var sut = BuildVersionedLinux(archive, Convert.ToHexString(SHA256.HashData(archive)));
        sut.IsWindowsOverride = false;
        sut.InstallDirectoryOverride = installDir.FullName;
        sut.LinuxPostureVersionPathOverride = postureVersion;
        sut.LinuxUpdateRequestPathOverride = request;
        return (sut, installDir, request);
    }

    [Fact]
    public async Task Execute_LinuxWithWhitelistedCapability_AddsItAsTheSixthRequestField()
    {
        var (sut, installDir, request) = await PrepareLinuxEnabling();
        try
        {
            var result = await RunVersionedLinuxEnabling(sut, AgentEnableableCapabilities.PatchManage);

            Assert.Equal(0, result.ExitCode);
            var fields = (await File.ReadAllTextAsync(request, TestContext.Current.CancellationToken))
                .TrimEnd('\n').Split('\t');
            Assert.Equal(6, fields.Length);
            Assert.Equal(VersionedLinuxHandler.Commit, fields[4]);
            Assert.Equal("patch-manage", fields[5]);
        }
        finally
        {
            Directory.Delete(installDir.FullName, recursive: true);
        }
    }

    [Theory]
    [InlineData("docker")]
    [InlineData("service-control")]
    [InlineData("patch-manage,docker")]
    [InlineData("PATCH-MANAGE")]
    [InlineData("patch-manage; rm -rf /")]
    public async Task Execute_CapabilityOutsideTheWhitelist_IsRefusedWithoutDownloadOrRequest(string capabilities)
    {
        var (sut, installDir, request) = await PrepareLinuxEnabling();
        try
        {
            var result = await RunVersionedLinuxEnabling(sut, capabilities);

            Assert.Equal(-1, result.ExitCode);
            Assert.False(File.Exists(request));
            await _api.Received().ReportUpdateProgressAsync(
                7,
                Arg.Is<AgentUpdateProgressReport>(report =>
                    report.Phase == AgentUpdatePhase.Failed && report.Message == "capability request refused"),
                Arg.Any<CancellationToken>());
            await _api.DidNotReceive().ReportUpdateProgressAsync(
                7,
                Arg.Is<AgentUpdateProgressReport>(report => report.Phase == AgentUpdatePhase.Downloading),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(installDir.FullName, recursive: true);
        }
    }

    [Fact]
    public async Task Execute_WindowsAgentAskedToEnableACapability_RefusesBeforeAnyDownload()
    {
        var (sut, installDir, request) = await PrepareLinuxEnabling();
        sut.IsWindowsOverride = true;
        try
        {
            var result = await RunVersionedLinuxEnabling(sut, AgentEnableableCapabilities.PatchManage);

            Assert.Equal(-1, result.ExitCode);
            Assert.False(File.Exists(request));
            await _api.DidNotReceive().ReportUpdateProgressAsync(
                7,
                Arg.Is<AgentUpdateProgressReport>(report => report.Phase == AgentUpdatePhase.Downloading),
                Arg.Any<CancellationToken>());
        }
        finally
        {
            Directory.Delete(installDir.FullName, recursive: true);
        }
    }

    [Fact]
    public void RequestBuilder_WithoutCapabilities_KeepsTheFiveFieldLineEveryInstalledSupervisorAccepts()
    {
        var release = new ResolvedAgentReleaseArchive(
            "1.0.0", new string('a', 40),
            new AgentReleaseArchiveDto { FileName = "aetheus-agent-linux-x64-v1.0.0.tar.gz", SizeBytes = 10, Sha256 = new string('B', 64) });

        Assert.Equal(5, LinuxAgentPostureUpgradeRequester.BuildQualifiedRequest(release).Split('\t').Length);
        Assert.Equal(5, LinuxAgentPostureUpgradeRequester.BuildQualifiedRequest(release, []).Split('\t').Length);
        Assert.Equal(6, LinuxAgentPostureUpgradeRequester.BuildQualifiedRequest(release, ["patch-manage"]).Split('\t').Length);
    }

    [Fact]
    public async Task Execute_MissingLinuxPosture_ReportsTerminalFailureProgress()
    {
        var installDir = Directory.CreateDirectory(Path.Combine(
            Path.GetTempPath(), $"aetheus-linux-install-{Guid.NewGuid():N}"));
        var postureVersion = Path.Combine(_workDir, "missing-posture-version");
        var request = Path.Combine(_workDir, "missing-posture-request");
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Linux.dll"),
            "old-agent",
            TestContext.Current.CancellationToken);
        var archive = BuildTarGzip(
            ("Aetheus.Agent.Linux.dll", "new-agent"),
            ("install-agent-linux.sh", "#!/bin/sh\nexit 0\n"));
        var sha = Convert.ToHexString(SHA256.HashData(archive));
        var sut = BuildVersionedLinux(archive, sha);
        sut.IsWindowsOverride = false;
        sut.InstallDirectoryOverride = installDir.FullName;
        sut.LinuxPostureVersionPathOverride = postureVersion;
        sut.LinuxUpdateRequestPathOverride = request;

        try
        {
            var result = await RunVersionedLinux(sut);

            Assert.Equal(-1, result.ExitCode);
            Assert.Equal(
                Aetheus.Shared.Components.Shared.TaskFailureCodes.InfrastructureMismatch,
                result.FailureCode);
            await _api.Received().ReportUpdateProgressAsync(
                7,
                Arg.Is<AgentUpdateProgressReport>(report =>
                    report.Phase == AgentUpdatePhase.Failed
                    && report.Percent == 80
                    && report.Message ==
                        "Linux integration posture upgrade supervisor is missing or obsolete."),
                Arg.Any<CancellationToken>());
            Assert.False(File.Exists(request));
        }
        finally
        {
            Directory.Delete(installDir.FullName, recursive: true);
        }
    }

    [PlatformFact("windows")]
    public async Task WindowsUpdater_NoConfirmingHeartbeat_RestoresPreviousBinaries()
    {
        var stagingRoot = Directory.CreateDirectory(Path.Combine(_workDir, "windows-update-timeout"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(stagingRoot.FullName, "source"));
        var installDir = Directory.CreateDirectory(Path.Combine(_workDir, "windows-install-timeout"));
        await File.WriteAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"),
            "old-agent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(sourceDir.FullName, "Aetheus.Agent.Windows.exe"),
            "new-agent",
            TestContext.Current.CancellationToken);
        var updater = AgentSelfUpdateOperationExecutor.WriteWindowsUpdater(stagingRoot.FullName);
        var startInfo = BuildWindowsUpdaterStartInfo(
            updater, sourceDir.FullName, installDir.FullName, confirmationTimeoutSeconds: 1);

        using var process = Process.Start(startInfo);
        Assert.NotNull(process);
        var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(4, process.ExitCode);
        Assert.Equal(
            "old-agent",
            await File.ReadAllTextAsync(
                Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"),
                TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(_workDir, ".agent-update-pending")));
    }

    [Fact]
    public void LinuxRollbackGuard_RequiresHeartbeatMarkerRemovalAndRestoresSnapshot()
    {
        var stagingRoot = Directory.CreateDirectory(Path.Combine(_workDir, "linux-rollback-guard"));

        var guard = LinuxAgentRollbackGuardScript.Write(stagingRoot.FullName);
        var script = File.ReadAllText(guard);

        Assert.Contains(".agent-update/update.log", script, StringComparison.Ordinal);
        Assert.Contains("kill -0 \"$new_pid\"", script, StringComparison.Ordinal);
        Assert.Contains("cp -a \"$rollback_dir\"/. \"$install_dir\"/", script, StringComparison.Ordinal);
        Assert.Contains("previous binaries restored automatically", script, StringComparison.Ordinal);
        // sh would read a CR as part of each command; the guard must reach the host with LF only.
        Assert.DoesNotContain('\r', script);
    }

    [Fact]
    public void RecoveryState_NewSessionRegistersPid_HeartbeatConfirms()
    {
        AgentUpdateRecoveryState.MarkPending(_workDir);
        var marker = AgentUpdateRecoveryState.GetMarkerPath(_workDir);
        Assert.Equal("0", File.ReadAllText(marker));

        AgentUpdateRecoveryState.RegisterStartedProcess(_workDir);
        Assert.Equal(
            Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            File.ReadAllText(marker));

        AgentUpdateRecoveryState.ConfirmSuccessfulHeartbeat(_workDir);
        Assert.False(File.Exists(marker));
    }

    [PlatformFact("windows")]
    public async Task WindowsUpdater_PartialCopyFailureRestoresPreviousBinaries()
    {
        var stagingRoot = Directory.CreateDirectory(Path.Combine(_workDir, "windows-update-runtime"));
        var sourceDir = Directory.CreateDirectory(Path.Combine(stagingRoot.FullName, "source"));
        var installDir = Directory.CreateDirectory(Path.Combine(_workDir, "windows-install"));
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"), "old-agent",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "dependency.dll"), "old-dependency",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(installDir.FullName, "appsettings.json"), "operator-config",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(sourceDir.FullName, "a-new.dll"), "partial-new-file",
            TestContext.Current.CancellationToken);
        var blockedSource = Path.Combine(sourceDir.FullName, "z-blocked.dll");
        await File.WriteAllTextAsync(blockedSource, "copy-must-fail", TestContext.Current.CancellationToken);
        var updater = AgentSelfUpdateOperationExecutor.WriteWindowsUpdater(stagingRoot.FullName);

        var startInfo = BuildWindowsUpdaterStartInfo(updater, sourceDir.FullName, installDir.FullName);

        using (File.Open(blockedSource, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var process = Process.Start(startInfo))
        {
            Assert.NotNull(process);
            var standardError = await process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.True(process.ExitCode == 1, standardError);
        }

        Assert.Equal("old-agent", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "Aetheus.Agent.Windows.exe"), TestContext.Current.CancellationToken));
        Assert.Equal("old-dependency", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "dependency.dll"), TestContext.Current.CancellationToken));
        Assert.Equal("operator-config", await File.ReadAllTextAsync(
            Path.Combine(installDir.FullName, "appsettings.json"), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(installDir.FullName, "a-new.dll")));
        Assert.Contains("previous binaries restored automatically",
            await File.ReadAllTextAsync(Path.Combine(stagingRoot.FullName, "update.log"),
                TestContext.Current.CancellationToken),
            StringComparison.Ordinal);
    }

    private static ProcessStartInfo BuildWindowsUpdaterStartInfo(
        string updater,
        string sourceDirectory,
        string installDirectory,
        int confirmationTimeoutSeconds = 30)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[]
        {
            "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", updater,
            "-AgentPid", int.MaxValue.ToString(), "-Src", sourceDirectory, "-Dest", installDirectory,
            "-ConfirmationTimeoutSeconds", confirmationTimeoutSeconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture)
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
    }

    private static byte[] BuildZip(params (string Name, string Content)[] entries)
    {
        using var ms = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(ms, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                var entry = zip.CreateEntry(name);
                using var w = new StreamWriter(entry.Open());
                w.Write(content);
            }
        }
        return ms.ToArray();
    }

    private static byte[] BuildTarGzip(params (string Name, string Content)[] entries)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var archive = new System.Formats.Tar.TarWriter(gzip, leaveOpen: true))
        {
            foreach (var (name, content) in entries)
            {
                using var data = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
                archive.WriteEntry(new System.Formats.Tar.PaxTarEntry(
                    System.Formats.Tar.TarEntryType.RegularFile,
                    name)
                {
                    DataStream = data
                });
            }
        }
        return output.ToArray();
    }
}
