// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Certbot detection is the source of the CBDT mismatch the collector was hardened against: the
/// Services tab said "installed" while the Certbot section said "NOT INSTALLED", because the agent's
/// hardened systemd PATH does not contain the certbot directories and a bare `which certbot` returned
/// nothing. These drive the real collector through a scripted shell and assert the DTO it produces.
/// </summary>
public class CertbotCollectorTests
{
    /// <summary>Returns a canned result per executable name, and records what was asked for.</summary>
    private sealed class ScriptedShell(Dictionary<string, ShellExecResult> byFileName) : IShellRunner
    {
        public List<string> Calls { get; } = [];

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            Calls.Add($"{fileName} {string.Join(' ', args)}");
            return Task.FromResult(byFileName.TryGetValue(fileName, out var r)
                ? r
                : new ShellExecResult(1, string.Empty, "not found"));
        }

        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args,
            IReadOnlyDictionary<string, string> environmentVariables, string workingDirectory,
            bool inheritEnvironment, int maxCapturedOutputBytes,
            CancellationToken ct, TimeSpan? timeout = null)
            => RunExecAsync(fileName, args, ct, timeout);

        public Task<string> RunWithStdinAsync(
            string fileName, IReadOnlyList<string> args, string stdin,
            CancellationToken ct, TimeSpan? timeout = null)
            => throw new NotSupportedException("The collector must not pipe stdin.");

        [Obsolete("Shell-based path; the collector does not use it.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null)
            => throw new NotSupportedException("The collector must not use the injectable shell path.");
    }

    private static CertbotCollector Build(
        ShellExecResult locate,
        ShellExecResult version,
        Func<List<CertbotCertificateDto>>? certificates = null,
        Func<string, bool>? fileExists = null,
        bool? unixLike = null,
        Func<string, string?>? readHelper = null)
    {
        var locator = OperatingSystem.IsWindows() ? "where" : "which";
        var shell = new ScriptedShell(new Dictionary<string, ShellExecResult>(StringComparer.Ordinal)
        {
            [locator] = locate,
            ["/usr/bin/certbot"] = version
        });
        // Default to an empty filesystem. Without this the known-path probe reads the real disk,
        // so every "certbot is absent" case silently passed on developer machines and failed on
        // any host that actually has certbot installed.
        return new CertbotCollector(
            NullLogger<CertbotCollector>.Instance, shell, certificates ?? (() => []),
            fileExists ?? (_ => false), unixLike, readHelper: readHelper ?? (_ => null));
    }

    [Fact]
    public async Task NoBinaryAnywhere_ReportsNotInstalled_WithNoVersion()
    {
        var collector = Build(
            locate: new ShellExecResult(1, string.Empty, "certbot not found"),
            version: new ShellExecResult(0, "certbot 2.7.4", string.Empty));

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(data.IsInstalled);
        Assert.True(string.IsNullOrEmpty(data.Version));
    }


    [Theory]
    [InlineData("#!/bin/sh\n# SPDX-License-Identifier: EUPL-1.2\n# aetheus-certbot-helper-version: 1\nset -eu\n", 1)]
    [InlineData("# aetheus-certbot-helper-version: 12\n", 12)]
    [InlineData("#!/bin/sh\n# helper without a marker\n", 0)]
    [InlineData(null, 0)]
    public async Task InstalledCertbot_ReportsTheHelperVersionFromItsMarker(string? helperText, int expected)
    {
        string? requestedPath = null;
        var collector = Build(
            locate: new ShellExecResult(1, string.Empty, "certbot not found"),
            version: new ShellExecResult(0, "certbot 2.7.4", string.Empty),
            fileExists: path => path == "/usr/bin/certbot",
            unixLike: true,
            readHelper: path => { requestedPath = path; return helperText; });

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(expected, data.HelperVersion);
        Assert.Equal("/usr/local/lib/aetheus/aetheus-certbot-manage", requestedPath);
    }
    [Fact]
    public async Task PathLookupFails_ButBinarySitsInAKnownLocation_ReportsInstalled()
    {
        // The CBDT mismatch itself: the hardened systemd PATH hides certbot from `which`, so
        // detection has to fall back to the well-known install paths. Driving that probe needs the
        // filesystem to be injectable, otherwise the assertion only reflects the build machine.
        // The fallback is Unix-only by design; the platform decision is injected so the probe is
        // driven here too, on any host, instead of the test disabling itself on Windows.
        var collector = Build(
            locate: new ShellExecResult(1, string.Empty, "certbot not found"),
            version: new ShellExecResult(0, "certbot 2.7.4", string.Empty),
            fileExists: path => path == "/usr/bin/certbot",
            unixLike: true);

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(data.IsInstalled);
        Assert.Equal("2.7.4", data.Version);
    }

    [Fact]
    public async Task BinaryOnPath_ReportsInstalled_AndReadsTheVersionFromStdout()
    {
        var collector = Build(
            locate: new ShellExecResult(0, "/usr/bin/certbot\n", string.Empty),
            version: new ShellExecResult(0, "certbot 2.7.4", string.Empty));

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(data.IsInstalled);
        Assert.Equal("2.7.4", data.Version);
    }

    /// <summary>
    /// Old certbot builds print their version banner on stderr. The collector falls back to it, and
    /// without that fallback an installed certbot reports a blank version.
    /// </summary>
    [Fact]
    public async Task VersionOnStderr_IsStillRead()
    {
        var collector = Build(
            locate: new ShellExecResult(0, "/usr/bin/certbot\n", string.Empty),
            version: new ShellExecResult(0, string.Empty, "certbot 1.12.0"));

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(data.IsInstalled);
        Assert.Equal("1.12.0", data.Version);
    }

    [Fact]
    public async Task UnrecognisableVersionBanner_LeavesTheVersionEmptyButStillInstalled()
    {
        var collector = Build(
            locate: new ShellExecResult(0, "/usr/bin/certbot\n", string.Empty),
            version: new ShellExecResult(0, "some other tool", string.Empty));

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(data.IsInstalled);
        Assert.Equal(string.Empty, data.Version);
    }

    [Fact]
    public async Task LocatorReturnsSeveralPaths_TheFirstOneIsUsed()
    {
        var locator = OperatingSystem.IsWindows() ? "where" : "which";
        var shell = new ScriptedShell(new Dictionary<string, ShellExecResult>(StringComparer.Ordinal)
        {
            [locator] = new(0, "/usr/bin/certbot\n/snap/bin/certbot\n", string.Empty),
            ["/usr/bin/certbot"] = new(0, "certbot 2.7.4", string.Empty)
        });
        var collector = new CertbotCollector(NullLogger<CertbotCollector>.Instance, shell, () => []);

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(data.IsInstalled);
        Assert.Contains(shell.Calls, c => c.StartsWith("/usr/bin/certbot", StringComparison.Ordinal));
        Assert.DoesNotContain(shell.Calls, c => c.StartsWith("/snap/bin/certbot", StringComparison.Ordinal));
    }

    /// <summary>
    /// F-ENG-06: once the binary has been found, a later failure must not downgrade the report to
    /// "not installed" - that is what made the two UI sections disagree.
    /// </summary>
    [Fact]
    public async Task CertificateReadFailsAfterDetection_StillReportsInstalled()
    {
        var collector = Build(
            locate: new ShellExecResult(0, "/usr/bin/certbot\n", string.Empty),
            version: new ShellExecResult(0, "certbot 2.7.4", string.Empty),
            certificates: () => throw new UnauthorizedAccessException("/etc/letsencrypt/live"));

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(data.IsInstalled);
        Assert.Empty(data.Certificates);
    }

    [Fact]
    public async Task CertificateListIsCappedAt2048()
    {
        var many = Enumerable.Range(0, 2100)
            .Select(i => new CertbotCertificateDto { Name = $"cert-{i}" })
            .ToList();
        var collector = Build(
            locate: new ShellExecResult(0, "/usr/bin/certbot\n", string.Empty),
            version: new ShellExecResult(0, "certbot 2.7.4", string.Empty),
            certificates: () => many);

        var data = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2048, data.Certificates.Count);
        Assert.Equal("cert-0", data.Certificates[0].Name);
    }
}
