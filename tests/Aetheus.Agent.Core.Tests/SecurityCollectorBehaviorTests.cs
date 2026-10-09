// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public sealed class SecurityCollectorBehaviorTests
{
    [Fact]
    public async Task MailCollector_InstalledStack_MapsVersionsServicesQueueAndDomains()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"),
                Arg.Is<IReadOnlyList<string>>(a => a[0].StartsWith("postfix", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/postfix\n", string.Empty));
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"),
                Arg.Is<IReadOnlyList<string>>(a => a[0].StartsWith("dovecot", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/dovecot\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "mail_version" })), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "mail_version = 3.8.1\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.Contains("virtual_mailbox_domains")), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "example.com, $mydestination ignored.example\n", string.Empty));
        shell.RunExecAsync("dovecot", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "2.3.21 (abc123)\n", string.Empty));
        shell.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "active\n", string.Empty));
        shell.RunExecAsync("postqueue", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "queue item\n-- 5 Kbytes in 3 Requests.\n", string.Empty));

        var result = await new MailCollector(
            NullLogger<MailCollector>.Instance,
            shell,
            fileExists: path => path == "/etc/postfix/main.cf").CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsPostfixRunning);
        Assert.True(result.IsDovecotRunning);
        Assert.Equal("3.8.1", result.PostfixVersion);
        Assert.Equal("2.3.21", result.DovecotVersion);
        Assert.Equal(3, result.QueueSize);
        Assert.Equal(["example.com", "ignored.example"], result.Domains.Select(d => d.Name));
    }

    [Fact]
    public async Task MailCollector_PartialFailureAfterPostfixDetection_StillReportsInstalled()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"),
                Arg.Is<IReadOnlyList<string>>(a => a[0].StartsWith("postfix", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "postfix", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "mail_version" })), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "mail_version = 3.8.1\n", string.Empty));
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"),
                Arg.Is<IReadOnlyList<string>>(a => a[0].StartsWith("dovecot", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns<Task<ShellExecResult>>(_ => throw new IOException("probe failed"));

        var result = await new MailCollector(
            NullLogger<MailCollector>.Instance,
            shell,
            fileExists: path => path == "/etc/postfix/main.cf").CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.False(result.IsDovecotRunning);
    }

    [Fact]
    public async Task MailCollector_MissingPostfixConfiguration_SkipsPostfixCommands()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"),
                Arg.Is<IReadOnlyList<string>>(a => a[0].StartsWith("postfix", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/postfix\n", string.Empty));
        var collector = new MailCollector(
            NullLogger<MailCollector>.Instance,
            shell,
            fileExists: _ => false);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
        await shell.DidNotReceive().RunExecAsync("postconf", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await shell.DidNotReceive().RunExecAsync("postqueue", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await shell.DidNotReceive().RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task MailCollector_InvalidPostfixConfiguration_SkipsQueueAndServiceCommands()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"),
                Arg.Is<IReadOnlyList<string>>(a => a[0].StartsWith("postfix", StringComparison.Ordinal)), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/postfix\n", string.Empty));
        shell.RunExecAsync("postconf", Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "mail_version" })), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(1, string.Empty, "fatal: invalid configuration"));
        var collector = new MailCollector(
            NullLogger<MailCollector>.Instance,
            shell,
            fileExists: path => path == "/etc/postfix/main.cf");

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
        await shell.DidNotReceive().RunExecAsync("postqueue", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await shell.DidNotReceive().RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
        await shell.Received(1).RunExecAsync("postconf", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task PortsentryCollector_DpkgInstallAndActiveService_MapsInstalledState()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("which", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/portsentry\n", string.Empty));
        shell.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "active\n", string.Empty));
        shell.RunExecAsync("dpkg", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "ii  portsentry  1.2-4  amd64  port scan detector\n", string.Empty));

        var result = await new PortsentryCollector(
            NullLogger<PortsentryCollector>.Instance,
            shell,
            fileExists: _ => false).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsRunning);
        Assert.Equal("1.2-4", result.Version);
        Assert.Equal("tcp", result.Mode);
        Assert.Empty(result.BlockedIps);
    }

    [Fact]
    public async Task PortsentryCollector_RpmInstallAndPidFallback_MapsInstalledState()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("which", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/usr/sbin/portsentry", string.Empty));
        shell.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(3, "inactive", string.Empty));
        shell.RunExecAsync("pidof", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "1234", string.Empty));
        shell.RunExecAsync("dpkg", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(1, string.Empty, "not Debian"));
        shell.RunExecAsync("rpm", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "1.2", string.Empty));

        var result = await new PortsentryCollector(NullLogger<PortsentryCollector>.Instance, shell).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsRunning);
        Assert.Equal("1.2", result.Version);
    }

    [Fact]
    public async Task RkhunterCollector_KnownBinary_ReportsInstalledEvenWithoutOptionalFiles()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("rkhunter", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "Rootkit Hunter version 1.4.6\n", string.Empty));
        var collector = new RkhunterCollector(
            NullLogger<RkhunterCollector>.Instance, shell,
            fileExists: path => path == "/usr/bin/rkhunter");

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.Equal("1.4.6", result.Version);
        Assert.Equal("none", result.LastScanStatus);
        Assert.Equal(DateTime.MinValue, result.LastScanTime);
    }

    [Fact]
    public void RkhunterCollector_ParseLastScan_CountsEveryWarningOfALongLastScan()
    {
        // 25 warnings: a 20-line tail drops the [Started] line and undercounts.
        var lines = new List<string> { "[Started] 2026-09-01 03:00:00", "[ Warning ] old scan", "System checks summary" };
        lines.Add("[Started] 2026-10-01 03:00:00");
        lines.AddRange(Enumerable.Range(0, 25).Select(i => $"[ Warning ] finding {i}"));
        lines.Add("System checks summary");

        var (time, status, warnings) = RkhunterCollector.ParseLastScan(lines);

        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0), time);
        Assert.Equal("warning", status);
        Assert.Equal(25, warnings);
    }

    [Fact]
    public void RkhunterCollector_ParseLastScan_CleanLastScanDoesNotInheritEarlierWarnings()
    {
        string[] lines =
        [
            "[Started] 2026-09-30 03:00:00", "[ Warning ] old finding", "System checks summary",
            "[Started] 2026-10-01 03:00:00", "System checks summary",
        ];

        var (time, status, warnings) = RkhunterCollector.ParseLastScan(lines);

        Assert.Equal(new DateTime(2026, 10, 1, 3, 0, 0), time);
        Assert.Equal("clean", status);
        Assert.Equal(0, warnings);
    }

    [Fact]
    public async Task CertbotCollector_PathDetectionAndStderrVersion_ReportInstalledVersion()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/opt/certbot/bin/certbot\n", string.Empty));
        shell.RunExecAsync("/opt/certbot/bin/certbot", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, string.Empty, "certbot 2.11.0\n"));

        var collector = new CertbotCollector(
            NullLogger<CertbotCollector>.Instance,
            shell,
            collectCertificates: () => []);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.Equal("2.11.0", result.Version);
        Assert.Empty(result.Certificates);
    }

    [Fact]
    public async Task CertbotCollector_CapsCertificateInventoryToHeartbeatBound()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync(Arg.Is<string>(x => x == "where" || x == "which"),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "/opt/certbot/bin/certbot\n", string.Empty));
        shell.RunExecAsync("/opt/certbot/bin/certbot", Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<CancellationToken>())
            .Returns(new ShellExecResult(0, "certbot 2.11.0\n", string.Empty));
        var certificates = Enumerable.Range(0, 2050)
            .Select(index => new CertbotCertificateDto { Name = $"cert-{index}" })
            .ToList();
        var collector = new CertbotCollector(
            NullLogger<CertbotCollector>.Instance,
            shell,
            collectCertificates: () => certificates);

        var result = await collector.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2048, result.Certificates.Count);
        Assert.Equal("cert-2047", result.Certificates[^1].Name);
    }
}
