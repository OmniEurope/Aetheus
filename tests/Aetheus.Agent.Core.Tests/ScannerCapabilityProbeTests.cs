// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public sealed class ScannerCapabilityProbeTests
{
    [Fact]
    public async Task CollectAsync_FreshTrivyMetadataReportsReady()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        using var directory = new TemporaryDirectory();
        await WriteMetadataAsync(directory.Path, "2026-07-22T06:00:00Z");
        var diagnostics = await ScannerCapabilityProbe.CollectAsync(
            UnavailableDocker(), directory.Path, new FakeTimeProvider(now), TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, value => value.StartsWith("scanner-db:trivy:ready:", StringComparison.Ordinal));
        Assert.Contains($"scanner-manifest:sha256:{ScannerManifestCatalog.Sha256}", diagnostics);
    }

    [Fact]
    public async Task CollectAsync_StaleTrivyMetadataReportsDegraded()
    {
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        using var directory = new TemporaryDirectory();
        await WriteMetadataAsync(directory.Path, "2026-07-19T06:00:00Z");
        var diagnostics = await ScannerCapabilityProbe.CollectAsync(
            UnavailableDocker(), directory.Path, new FakeTimeProvider(now), TestContext.Current.CancellationToken);
        Assert.Contains(diagnostics, value => value.StartsWith("scanner-db:trivy:degraded:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CollectAsync_MissingTrivyMetadataIsExplicitlyNotInitialized()
    {
        using var directory = new TemporaryDirectory();
        var diagnostics = await ScannerCapabilityProbe.CollectAsync(
            UnavailableDocker(), directory.Path, TimeProvider.System, TestContext.Current.CancellationToken);
        Assert.Contains("scanner-db:trivy:not-initialized:no vulnerability database cached", diagnostics);
    }

    [Fact]
    public async Task CollectAsync_ResidualScanWorkspaces_AreReportedForReview()
    {
        using var directory = new TemporaryDirectory();
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "scans", "run-1"));
        Directory.CreateDirectory(System.IO.Path.Combine(directory.Path, "scans", "run-2"));

        var diagnostics = await ScannerCapabilityProbe.CollectAsync(
            UnavailableDocker(), directory.Path, TimeProvider.System, TestContext.Current.CancellationToken);

        Assert.Contains("scanner-cleanup:degraded:2 residual workspaces require operator review", diagnostics);
        Assert.Equal(ScannerManifestCatalog.Default.Scanners.Count + 3, diagnostics.Count);
    }

    private static readonly ScannerManifestEntry Container = new() { Key = "trivy", Version = "1.0", Image = "aquasec/trivy@sha256:abc" };

    [Theory]
    [InlineData(0, "scanner:trivy:1.0:ready:immutable image cached")]
    [InlineData(1, "scanner:trivy:1.0:available-on-demand:immutable image pull required")]
    public async Task ContainerDiagnostic_FollowsTheCachedImage(int exitCode, string expected)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("docker", Arg.Is<IReadOnlyList<string>>(args => args.SequenceEqual(new[] { "image", "inspect", Container.Image! })),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(exitCode, string.Empty, string.Empty));

        Assert.Equal(expected, await ScannerCapabilityProbe.ContainerDiagnosticAsync(shell, Container, true, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContainerDiagnostic_WithoutDockerOrWhenTheProbeFails_IsUnavailable()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns<ShellExecResult>(_ => throw new InvalidOperationException("docker vanished"));
        var ct = TestContext.Current.CancellationToken;

        Assert.Equal("scanner:trivy:1.0:unavailable:docker daemon or socket unavailable",
            await ScannerCapabilityProbe.ContainerDiagnosticAsync(shell, Container, false, ct));
        Assert.Equal("scanner:trivy:1.0:unavailable:container image probe failed",
            await ScannerCapabilityProbe.ContainerDiagnosticAsync(shell, Container, true, ct));
    }

    [Fact]
    public async Task BinaryDiagnostic_ReportsPlatformDockerDownloadAndHash()
    {
        using var directory = new TemporaryDirectory();
        var ct = TestContext.Current.CancellationToken;
        var content = "verified scanner binary"u8.ToArray();
        var hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content));
        var scanner = new ScannerManifestEntry { Key = "gitleaks", Version = "8.0", Sha256LinuxAmd64 = hash.ToUpperInvariant() };

        Assert.Equal("scanner:gitleaks:8.0:unavailable:verified binary supports Linux x64 only",
            await ScannerCapabilityProbe.BinaryDiagnosticAsync(scanner, directory.Path, false, true, ct));
        Assert.Equal("scanner:gitleaks:8.0:unavailable:container runtime required for binary confinement",
            await ScannerCapabilityProbe.BinaryDiagnosticAsync(scanner, directory.Path, true, false, ct));
        Assert.Equal("scanner:gitleaks:8.0:available-on-demand:verified binary download required",
            await ScannerCapabilityProbe.BinaryDiagnosticAsync(scanner, directory.Path, true, true, ct));

        var binary = System.IO.Path.Combine(directory.Path, "scanners", "gitleaks", "8.0", "gitleaks");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(binary)!);
        await File.WriteAllBytesAsync(binary, content, ct);
        Assert.Equal("scanner:gitleaks:8.0:ready:verified binary cached",
            await ScannerCapabilityProbe.BinaryDiagnosticAsync(scanner, directory.Path, true, true, ct));

        await File.WriteAllTextAsync(binary, "tampered", ct);
        Assert.Equal("scanner:gitleaks:8.0:degraded:cached binary hash mismatch; redownload required",
            await ScannerCapabilityProbe.BinaryDiagnosticAsync(scanner, directory.Path, true, true, ct));
    }

    private static IShellRunner UnavailableDocker()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(new ShellExecResult(1, string.Empty, "unavailable"));
        return shell;
    }

    private static async Task WriteMetadataAsync(string root, string updatedAt)
    {
        var directory = System.IO.Path.Combine(root, "scanner-cache", "trivy", "db");
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(System.IO.Path.Combine(directory, "metadata.json"),
            $$"""{"UpdatedAt":"{{updatedAt}}"}""", TestContext.Current.CancellationToken);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"aetheus-scanner-probe-{Guid.NewGuid():N}");
        public TemporaryDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
