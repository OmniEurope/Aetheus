// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;
using Aetheus.Agent.Linux.Collectors;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The service list a server shows is assembled from three systemd passes plus binary probes. What
/// matters to the operator is the label: a timer-driven unit sitting at "dead" is healthy and waiting,
/// not broken, and a critical unit must never be offered a Stop button. These drive the real collector
/// against scripted systemctl output and assert the DTOs it produces.
/// </summary>
public class LinuxServiceCollectorTests
{
    /// <summary>
    /// Answers /bin/bash -c &lt;command&gt; from a table keyed by a substring of the command; a direct
    /// argv launch (dpkg-query, /usr/bin/test) is matched on its program and arguments joined by spaces.
    /// </summary>
    private sealed class ScriptedBash(params (string Match, string StdOut)[] answers) : IShellRunner
    {
        public Task<ShellExecResult> RunExecAsync(
            string fileName, IReadOnlyList<string> args, CancellationToken ct, TimeSpan? timeout = null)
        {
            var command = fileName == "/bin/bash"
                ? args.Count > 1 ? args[1] : string.Empty
                : string.Join(' ', [fileName, .. args]);
            foreach (var (match, stdout) in answers)
                if (command.Contains(match, StringComparison.Ordinal))
                    return Task.FromResult(new ShellExecResult(0, stdout, string.Empty));
            return Task.FromResult(new ShellExecResult(1, string.Empty, "no such command"));
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

        [Obsolete("Shell-based path; the collector routes through RunExecAsync.")]
        public Task<string> RunAsync(string command, CancellationToken ct, TimeSpan? timeout = null)
            => throw new NotSupportedException("The collector must not use the legacy shell path.");
    }

    private static Task<List<ServiceInfoDto>> CollectAsync(params (string, string)[] answers) =>
        new LinuxServiceCollector(NullLogger<LinuxServiceCollector>.Instance, new ScriptedBash(answers))
            .CollectAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task InstalledUnit_IsReportedInstalledAndNotRunning()
    {
        var services = await CollectAsync(
            ("list-unit-files", "nginx.service enabled\n"));

        var nginx = Assert.Single(services, s => s.Name == "nginx");
        Assert.True(nginx.IsInstalled);
        Assert.False(nginx.IsRunning);
        Assert.Equal(ServiceType.Systemd, nginx.Type);
        Assert.Equal("enabled", nginx.Status);
    }

    [Fact]
    public async Task RunningUnit_OverlaysItsRuntimeSubState()
    {
        var services = await CollectAsync(
            ("list-unit-files", "nginx.service enabled\n"),
            ("list-units", "nginx.service loaded active running\n"));

        var nginx = Assert.Single(services, s => s.Name == "nginx");
        Assert.True(nginx.IsRunning);
        Assert.Equal("running", nginx.Status);
    }

    [Fact]
    public async Task CriticalUnit_IsNotOfferedForManagement()
    {
        // Offering Stop on the unit that keeps the agent reachable is how a server is lost remotely.
        var services = await CollectAsync(
            ("list-unit-files", "sshd.service enabled\ncron.service enabled\nnginx.service enabled\n"));

        Assert.False(Assert.Single(services, s => s.Name == "sshd").IsManageable);
        Assert.False(Assert.Single(services, s => s.Name == "cron").IsManageable);
        Assert.True(Assert.Single(services, s => s.Name == "nginx").IsManageable);
    }

    /// <summary>
    /// Records a gap found while writing these tests, WITHOUT asserting it is acceptable.
    ///
    /// SystemCriticalServices lists "sshd", the RHEL unit name. On Debian and Ubuntu - which is what
    /// install-agent-linux.sh targets - the unit is "ssh", which is not in the list, so the UI offers
    /// a Stop button on the very service that keeps the box reachable.
    ///
    /// This test pins the current behaviour so the gap is visible and measured rather than assumed
    /// fixed. Adding "ssh" to the deny-list is a product decision (it also removes the operator's
    /// ability to restart sshd from the UI), so it is left to the owner rather than changed here.
    /// When it is decided, this test flips to Assert.False.
    /// </summary>
    [Fact]
    public async Task DebianSshUnitIsStillManageable_KnownGapInTheCriticalList()
    {
        var services = await CollectAsync(("list-unit-files", "ssh.service enabled\n"));

        Assert.True(Assert.Single(services, s => s.Name == "ssh").IsManageable);
    }

    [Fact]
    public async Task DeadUnitWithAnActiveTimer_IsReportedScheduled_NotBroken()
    {
        var services = await CollectAsync(
            ("list-unit-files", "certbot.service enabled\n"),
            ("list-units --type=service", "certbot.service loaded inactive dead\n"),
            ("--type=timer", "certbot.timer loaded active waiting\n"));

        Assert.Equal("scheduled", Assert.Single(services, s => s.Name == "certbot").Status);
    }

    [Fact]
    public async Task DeadOneshotWithoutATimer_IsReportedIdle()
    {
        var services = await CollectAsync(
            ("list-unit-files", "unattended-upgrades.service enabled\n"),
            ("list-units --type=service", "unattended-upgrades.service loaded inactive dead\n"));

        Assert.Equal("idle", Assert.Single(services, s => s.Name == "unattended-upgrades").Status);
    }

    [Fact]
    public async Task DeadUnitThatIsNeitherTimerDrivenNorAKnownOneshot_KeepsItsRawSubState()
    {
        var services = await CollectAsync(
            ("list-unit-files", "nginx.service enabled\n"),
            ("list-units --type=service", "nginx.service loaded inactive dead\n"));

        Assert.Equal("dead", Assert.Single(services, s => s.Name == "nginx").Status);
    }

    [Fact]
    public async Task RunningUnit_IsNeverReclassified_EvenWithAPairedTimer()
    {
        var services = await CollectAsync(
            ("list-unit-files", "certbot.service enabled\n"),
            ("list-units --type=service", "certbot.service loaded active running\n"),
            ("--type=timer", "certbot.timer loaded active waiting\n"));

        var certbot = Assert.Single(services, s => s.Name == "certbot");
        Assert.True(certbot.IsRunning);
        Assert.Equal("running", certbot.Status);
    }

    [Fact]
    public async Task ServiceSuffixIsStripped_SoTheNameMatchesWhatTheUiManages()
    {
        var services = await CollectAsync(("list-unit-files", "postfix.service enabled\n"));

        Assert.Contains(services, s => s.Name == "postfix");
        Assert.DoesNotContain(services, s => s.Name.EndsWith(".service", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MalformedRuntimeLine_IsSkippedWithoutLosingTheInstalledUnit()
    {
        // A short line (fewer than four columns) carries no SubState; dropping the whole unit because
        // of it would make an installed service vanish from the UI.
        var services = await CollectAsync(
            ("list-unit-files", "nginx.service enabled\n"),
            ("list-units --type=service", "nginx.service loaded\n"));

        var nginx = Assert.Single(services, s => s.Name == "nginx");
        Assert.True(nginx.IsInstalled);
        Assert.Equal("enabled", nginx.Status);
    }

    [Fact]
    public async Task SystemctlUnavailable_YieldsNoSystemdServicesRatherThanThrowing()
    {
        var services = await CollectAsync();

        Assert.DoesNotContain(services, s => s.Type == ServiceType.Systemd);
    }

    // A systemd not-found entry can remain after package removal and must not appear as an
    // installed service.
    [Theory]
    [InlineData("dovecot.service not-found inactive dead dovecot.service\n")]
    [InlineData("● dovecot.service not-found inactive dead dovecot.service\n")]
    public async Task NotFoundUnit_IsNotReportedInstalled(string listUnits)
    {
        var services = await CollectAsync(
            ("list-unit-files", "nginx.service enabled\n"),
            ("list-units --type=service", listUnits));

        Assert.DoesNotContain(services, s => s.Name == "dovecot");
        Assert.Contains(services, s => s.Name == "nginx" && s.IsInstalled);
    }

    // `apt-get remove` keeps /etc/init.d/dovecot; systemd-sysv-generator turns it into a loaded
    // "generated" unit whose program is gone.
    [Fact]
    public async Task GeneratedUnitWhoseProgramIsGone_IsNotReported()
    {
        var services = await CollectAsync(
            ("list-unit-files", "dovecot.service generated -\n"),
            ("list-units --type=service", "dovecot.service loaded active exited LSB: Dovecot\n"));

        Assert.DoesNotContain(services, s => s.Name == "dovecot");
    }

    [Fact]
    public async Task GeneratedUnitWhoseProgramIsThere_IsKept()
    {
        // portsentry ships only an init script: its generated unit is the normal installed state.
        var services = await CollectAsync(
            ("list-unit-files", "portsentry.service generated -\n"),
            ("list-units --type=service", "portsentry.service loaded active running LSB: portsentry\n"),
            ("/usr/bin/test -x /usr/sbin/portsentry", string.Empty));

        var portsentry = Assert.Single(services, s => s.Name == "portsentry");
        Assert.True(portsentry.IsInstalled);
        Assert.True(portsentry.IsRunning);
    }

    [Fact]
    public async Task PackageLeftInRcState_IsNotReported_EvenWhenAUnitAndTheBinaryProbeRemain()
    {
        // `rc`: removed, configuration files kept. Whatever is left on disk, the service is not installed.
        var services = await CollectAsync(
            ("list-unit-files", "dovecot.service enabled\nnginx.service enabled\n"),
            ("command -v dovecot", string.Empty),
            ("dpkg-query", "dovecot-core\trc \nnginx\tii \nlibc6\tii \n"));

        Assert.DoesNotContain(services, s => s.Name == "dovecot");
        Assert.Contains(services, s => s.Name == "nginx");
    }

    [Fact]
    public async Task PurgedPackage_AbsentFromDpkg_IsNotReported()
    {
        var services = await CollectAsync(
            ("list-unit-files", "postfix.service enabled\n"),
            ("dpkg-query", "libc6\tii \n"));

        Assert.DoesNotContain(services, s => s.Name == "postfix");
    }

    [Fact]
    public async Task InstalledPackage_IsReported()
    {
        var services = await CollectAsync(
            ("list-unit-files", "dovecot.service enabled\n"),
            ("list-units --type=service", "dovecot.service loaded active running Dovecot\n"),
            ("dpkg-query", "dovecot-core\tii \n"));

        var dovecot = Assert.Single(services, s => s.Name == "dovecot");
        Assert.True(dovecot.IsInstalled);
        Assert.True(dovecot.IsRunning);
    }

    [Fact]
    public async Task ServiceAnotherPackageCanProvide_IsNotDecidedByTheCataloguePackage()
    {
        // nginx may come from nginx-core on older releases; a missing `nginx` package proves nothing.
        var services = await CollectAsync(
            ("list-unit-files", "nginx.service enabled\npostfix.service enabled\n"),
            ("dpkg-query", "nginx-core\tii \npostfix\tii \n"));

        Assert.Contains(services, s => s.Name == "nginx");
    }

    [Theory]
    [InlineData("dovecot-core\tii \n", true)]
    [InlineData("dovecot-core\thi \n", true)]   // held, installed
    [InlineData("dovecot-core\tiF \n", true)]   // half-configured: still on disk, must stay uninstallable
    [InlineData("dovecot-core\trc \n", false)]  // removed, configuration files kept
    [InlineData("dovecot-core\tun \n", false)]  // not installed
    [InlineData("dovecot-core\tpn \n", false)]  // purged
    public void DpkgStatus_DecidesWhetherThePackageIsInstalled(string stdout, bool installed)
    {
        var packages = DpkgInstalledPackages.Parse(stdout);

        Assert.NotNull(packages);
        Assert.Equal(installed, packages.Contains("dovecot-core"));
    }

    [Fact]
    public void DpkgAnswerWithoutAnyPackage_IsUnknown_NotEverythingRemoved()
        => Assert.Null(DpkgInstalledPackages.Parse(string.Empty));
}
