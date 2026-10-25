// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>A binary scanner is only downloaded from an HTTPS address whose SHA-256 the manifest pins,
/// and only run with its rules and a runtime image pinned by digest.</summary>
public sealed class ScannerBinaryManifestTests
{
    private static readonly string Sha = new('a', 64);

    [Fact]
    public void RequireVerifiedDownload_AcceptsAPinnedHttpsBinary()
    {
        var uri = OpenGrepSandbox.RequireVerifiedDownload(new ScannerManifestEntry
        {
            DownloadUriLinuxAmd64 = "https://example.com/opengrep",
            Sha256LinuxAmd64 = Sha,
        });

        Assert.Equal(new Uri("https://example.com/opengrep"), uri);
    }

    [Theory]
    [InlineData(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("not a uri", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("http://example.com/opengrep", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("https://example.com/opengrep", null)]
    [InlineData("https://example.com/opengrep", "abc")]
    public void RequireVerifiedDownload_RefusesAnIncompleteEntry(string? uri, string? sha)
    {
        var error = Assert.Throws<IOException>(() => OpenGrepSandbox.RequireVerifiedDownload(
            new ScannerManifestEntry { DownloadUriLinuxAmd64 = uri, Sha256LinuxAmd64 = sha }));

        Assert.Equal("Binary scanner manifest entry is incomplete.", error.Message);
    }

    [Fact]
    public void RequireBinaryRuntime_GivesTheReportDirectory()
    {
        var directory = ScannerOperationExecutor.RequireBinaryRuntime(
            new ScannerManifestEntry { Key = "opengrep", RuntimeImage = "debian@sha256:" + Sha },
            Path.Combine("out", "report.sarif"), "rules");

        Assert.Equal("out", directory);
    }

    [Theory]
    [InlineData("opengrep", "debian@sha256:x", null, "Embedded versioned OpenGrep rules are unavailable.")]
    [InlineData("other", null, null, "Binary scanner runtime image is not pinned by digest.")]
    [InlineData("other", "debian:12", null, "Binary scanner runtime image is not pinned by digest.")]
    public void RequireBinaryRuntime_RefusesMissingRulesOrAMutableImage(string key, string? image, string? rules, string message)
    {
        var error = Assert.Throws<IOException>(() => ScannerOperationExecutor.RequireBinaryRuntime(
            new ScannerManifestEntry { Key = key, RuntimeImage = image }, Path.Combine("out", "report.sarif"), rules));

        Assert.Equal(message, error.Message);
    }

    [Fact]
    public void RequireBinaryRuntime_WithoutAReportDirectory_IsRefused()
    {
        var error = Assert.Throws<IOException>(() => ScannerOperationExecutor.RequireBinaryRuntime(
            new ScannerManifestEntry { Key = "other", RuntimeImage = "debian@sha256:" + Sha }, Path.GetPathRoot(Path.GetTempPath())!, null));

        Assert.Equal("Binary scanner report directory is unavailable.", error.Message);
    }
}
