// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Agent.Core.Tests;

public class ApacheCollectorTests
{
    private readonly IShellRunner _shellMock = Substitute.For<IShellRunner>();
    private readonly ApacheCollector _sut;

    public ApacheCollectorTests()
    {
        _sut = BuildCollector(isWindows: false);
    }

    private ApacheCollector BuildCollector(bool isWindows) =>
        new(NullLogger<ApacheCollector>.Instance, _shellMock)
        {
            IsWindows = isWindows,
            DirectoryExists = path => path == "/etc/apache2"
        };

    private static ShellExecResult Ok(string stdout = "") => new(0, stdout, string.Empty);
    private static ShellExecResult Fail(int code = 1) => new(code, string.Empty, "denied");

    [Fact]
    public async Task CollectAsync_BinaryNotFound_ReturnsNotInstalled()
    {
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Fail()); // which/where all fail

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
    }

    [Fact]
    public async Task CollectAsync_BinaryFound_ReturnsInstalled()
    {
        SetupInstalledApache();

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
    }

    [Fact]
    public async Task CollectAsync_ParsesVersion()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-v")), Arg.Any<CancellationToken>())
            .Returns(Ok("Server version: Apache/2.4.58 (Ubuntu)\nServer built:   2024-01-01T00:00:00"));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("2.4.58", result.Version);
    }

    [Fact]
    public async Task CollectAsync_ParsesModules()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-M")), Arg.Any<CancellationToken>())
            .Returns(Ok("""
                Loaded Modules:
                 core_module (static)
                 rewrite_module (shared)
                 ssl_module (shared)
                """));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(3, result.Modules.Count);
        Assert.Contains(result.Modules, m => m.Name == "core_module" && m.Type == "static");
        Assert.Contains(result.Modules, m => m.Name == "rewrite_module" && m.Type == "shared");
        Assert.Contains(result.Modules, m => m.Name == "ssl_module" && m.Type == "shared");
    }

    [Fact]
    public async Task CollectAsync_ListsDisabledModules_FromModsAvailable_WithoutDuplicatingLoadedOnes()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-M")), Arg.Any<CancellationToken>())
            .Returns(Ok("""
                Loaded Modules:
                 core_module (static)
                 rewrite_module (shared)
                """));
        var sut = new ApacheCollector(NullLogger<ApacheCollector>.Instance, _shellMock)
        {
            IsWindows = false,
            DirectoryExists = path => path is "/etc/apache2" or "/etc/apache2/mods-available",
            EnumerateModuleFiles = _ => ["/etc/apache2/mods-available/rewrite.load", "/etc/apache2/mods-available/proxy_http.load", "/etc/apache2/mods-available/headers.load"],
            // Never the real /etc/apache2/mods-enabled: the CI agent runs Apache with headers and proxy_http enabled.
            FileExists = _ => false
        };

        var result = await sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["core_module", "rewrite_module", "headers_module", "proxy_http_module"], result.Modules.Select(m => m.Name));
        Assert.Single(result.Modules, m => m.Name == "rewrite_module");
        Assert.All(result.Modules.Where(m => m.Name is "headers_module" or "proxy_http_module"), m =>
        {
            Assert.False(m.IsEnabled);
            Assert.Equal("shared", m.Type);
        });
        Assert.True(result.Modules.Single(m => m.Name == "rewrite_module").IsEnabled);
    }

    [Fact]
    public async Task CollectAsync_EnabledModuleWhoseLoadFileNameDiffersFromItsId_IsNotListedDisabled()
    {
        SetupInstalledApache();
        // php8.2.load (what a2enmod takes) loads php_module: the id derived from the file name never matches.
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-M")), Arg.Any<CancellationToken>())
            .Returns(Ok("""
                Loaded Modules:
                 core_module (static)
                 php_module (shared)
                """));
        var sut = new ApacheCollector(NullLogger<ApacheCollector>.Instance, _shellMock)
        {
            IsWindows = false,
            DirectoryExists = path => path is "/etc/apache2" or "/etc/apache2/mods-available",
            EnumerateModuleFiles = _ => ["/etc/apache2/mods-available/php8.2.load", "/etc/apache2/mods-available/headers.load"],
            FileExists = path => path == "/etc/apache2/mods-enabled/php8.2.load"
        };

        var result = await sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["core_module", "php_module", "headers_module"], result.Modules.Select(m => m.Name));
        Assert.DoesNotContain(result.Modules, m => m.Name == "php8.2_module");
        Assert.True(result.Modules.Single(m => m.Name == "php_module").IsEnabled);
        Assert.False(result.Modules.Single(m => m.Name == "headers_module").IsEnabled);
    }

    [Fact]
    public async Task CollectAsync_ParsesVirtualHosts()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-S")), Arg.Any<CancellationToken>())
            .Returns(Ok("""
                VirtualHost configuration:
                port 80 namevhost example.com (/etc/apache2/sites-enabled/example.conf:1)
                port 443 namevhost secure.example.com (/etc/apache2/sites-enabled/ssl.conf:1)
                """));
        _shellMock.RunExecAsync("cat", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Ok("DocumentRoot /var/www/html\n"));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, result.VirtualHosts.Count);
        Assert.Contains(result.VirtualHosts, v => v.ServerName == "example.com" && v.Port == 80);
        Assert.Contains(result.VirtualHosts, v => v.ServerName == "secure.example.com" && v.Port == 443);
    }

    [Fact]
    public async Task CollectAsync_ParsesLinuxConfigRoot()
    {
        SetupInstalledApache();
        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("/etc/apache2", result.ConfigRoot);
    }

    [Fact]
    public async Task CollectAsync_ParsesWindowsConfigRoot()
    {
        SetupInstalledApache(isWindows: true);
        _shellMock.RunExecAsync("httpd.exe", Arg.Is<IReadOnlyList<string>>(a => a.Contains("-V")), Arg.Any<CancellationToken>())
            .Returns(Ok("HTTPD_ROOT=\"C:/Apache24\""));

        var result = await BuildCollector(isWindows: true).CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal("C:/Apache24", result.ConfigRoot);
    }

    [Fact]
    public async Task CollectAsync_RunningWithPid_ReturnsRunningState()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync("systemctl",
                Arg.Is<IReadOnlyList<string>>(a => a.Contains("is-active") && a.Contains("apache2")), Arg.Any<CancellationToken>())
            .Returns(Ok("active"));
        _shellMock.RunExecAsync("pgrep", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Ok("1234"));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsRunning);
        Assert.NotNull(result.Pid);
    }

    [Fact]
    public async Task CollectAsync_WindowsRunningWithPid_ReturnsRunningState()
    {
        SetupInstalledApache(isWindows: true);
        _shellMock.RunExecAsync("tasklist", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Ok("\"httpd.exe\",\"5678\",\"Services\",\"0\",\"12,345 K\""));

        var result = await BuildCollector(isWindows: true).CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsRunning);
        Assert.Equal(5678, result.Pid);
    }

    [Fact]
    public async Task CollectAsync_NotRunning_ReturnsFalse()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Ok("inactive"));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsRunning);
        Assert.Null(result.Pid);
    }

    [Fact]
    public async Task CollectAsync_VersionParseFailure_ReturnsEmptyVersion()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-v")), Arg.Any<CancellationToken>())
            .Returns(Ok("some unexpected output"));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, result.Version);
    }

    [Fact]
    public async Task CollectAsync_ShellThrows_ReturnsNotInstalled()
    {
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Throws(new InvalidOperationException("Shell error"));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
    }

    [Fact]
    public async Task CollectAsync_VhostDumpFails_FlagsDegradedWithoutSudo()
    {
        SetupInstalledApache();
        // apache2ctl -S exits non-zero (agent cannot read the config) and yields no vhosts.
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-S")), Arg.Any<CancellationToken>())
            .Returns(Fail(1));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.True(result.CollectionDegraded);
        Assert.Empty(result.VirtualHosts);
        // Security: steer the operator to read-access, never to a privilege grant.
        Assert.Contains("read access", result.CollectionDiagnostics);
        Assert.DoesNotContain("NOPASSWD", result.CollectionDiagnostics);
        Assert.DoesNotContain("sudoers", result.CollectionDiagnostics);
    }

    [Fact]
    public async Task CollectAsync_VhostDumpExitsZero_NotDegraded()
    {
        SetupInstalledApache();
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-S")), Arg.Any<CancellationToken>())
            .Returns(Ok("port 80 namevhost ok.example.com (/etc/apache2/sites-enabled/ok.conf:1)"));

        var result = await _sut.CollectAsync(TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.False(result.CollectionDegraded);
        Assert.Equal(string.Empty, result.CollectionDiagnostics);
        Assert.Contains(result.VirtualHosts, v => v.ServerName == "ok.example.com" && v.Port == 80);
    }

    private void SetupInstalledApache(bool isWindows = false)
    {
        // Default: every exec succeeds with empty output (installed, idle, empty config).
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Ok());

        if (isWindows)
        {
            _shellMock.RunExecAsync("where", Arg.Is<IReadOnlyList<string>>(a => a.Contains("httpd.exe")), Arg.Any<CancellationToken>())
                .Returns(Ok("C:\\Apache24\\bin\\httpd.exe"));
            _shellMock.RunExecAsync("tasklist", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(Ok("\"httpd.exe\",\"5678\",\"Services\",\"0\",\"12,345 K\""));
        }
        else
        {
            _shellMock.RunExecAsync("which", Arg.Is<IReadOnlyList<string>>(a => a.Contains("apache2")), Arg.Any<CancellationToken>())
                .Returns(Ok("/usr/sbin/apache2"));
            _shellMock.RunExecAsync("which", Arg.Is<IReadOnlyList<string>>(a => a.Contains("apache2ctl")), Arg.Any<CancellationToken>())
                .Returns(Ok("/usr/sbin/apache2ctl"));
            _shellMock.RunExecAsync("systemctl", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
                .Returns(Ok("inactive"));
        }

        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-v")), Arg.Any<CancellationToken>())
            .Returns(Ok("Server version: Apache/2.4.58 (Ubuntu)"));
        // -S / -M default to exit 0 + empty (installed, not degraded). Tests override as needed.
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-S")), Arg.Any<CancellationToken>())
            .Returns(Ok());
        _shellMock.RunExecAsync(Arg.Any<string>(), Arg.Is<IReadOnlyList<string>>(a => a.Contains("-M")), Arg.Any<CancellationToken>())
            .Returns(Ok());
    }
}
