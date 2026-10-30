// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Runs the root-owned <c>deploy/agent-host-config/portsentry/unblock-ip</c> helper for real (Git Bash on
/// Windows, /bin/sh on Linux) against a sandbox: the helper text is copied with its three host paths
/// (<c>/etc/hosts.deny</c>, <c>/etc/portsentry/portsentry.conf</c>, <c>/var/lib/portsentry</c>) pointed into a
/// temporary folder, and iptables/ip6tables are fakes that hold no rule. R-119: an unblocked IP stayed in
/// PortSentry's blocked files, so the page kept listing it and PortSentry would not block it again.
/// </summary>
public sealed class PortsentryUnblockHelperTests : IDisposable
{
    private const string HostsDeny = "/etc/hosts.deny";
    private const string Conf = "/etc/portsentry/portsentry.conf";
    private const string BlockedDir = "/var/lib/portsentry";

    private readonly string _root = Directory.CreateTempSubdirectory("unblock-ip-").FullName;
    private string Dir => Path.Combine(_root, "var", "lib", "portsentry");
    private string ShellDir => ToShellPath(Dir);

    public PortsentryUnblockHelperTests()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(Path.Combine(_root, "etc", "portsentry"));
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        foreach (var tool in new[] { "iptables", "ip6tables" })
            WriteExecutable(Path.Combine(_root, "bin", tool), "#!/bin/sh\n# -C: no DROP rule for this address\nexit 1\n");
    }

    private static string Line(string host, string ip, int port, string proto) =>
        $"1728381045 - 10/08/2024 11:50:45 Host: {host}/{ip} Port: {port} {proto} Blocked";

    [Fact]
    public async Task Unblock_RemovesTheIpFromEveryDefaultBlockedFile_AndKeepsTheNeighbours()
    {
        Write("etc/portsentry/portsentry.conf", "TCP_PORTS=\"1,11\"\n# BLOCKED_FILE=\"/elsewhere/x\"\n");
        Write("var/lib/portsentry/portsentry.blocked.atcp", string.Join('\n',
            Line("10.0.0.1", "10.0.0.1", 1080, "TCP"),
            Line("10.0.0.10", "10.0.0.10", 1080, "TCP"),
            Line("110.0.0.1", "110.0.0.1", 23, "TCP"),
            Line("scanner.example.net", "10.0.0.1", 31337, "TCP")) + "\n");
        Write("var/lib/portsentry/portsentry.blocked.udp", Line("10.0.0.1", "10.0.0.1", 161, "UDP") + "\n" + "10.0.0.11\n");
        Write("var/lib/portsentry/portsentry.blocked.stcp", Line("10.0.0.100", "10.0.0.100", 25, "TCP") + "\n");
        Write("etc/hosts.deny", "ALL: 10.0.0.1\nALL: 10.0.0.10\n");

        var (exit, output) = await RunAsync("10.0.0.1");

        Assert.Equal(0, exit);
        Assert.Contains("3 line(s) removed", output, StringComparison.Ordinal);
        Assert.Contains("unblocked 10.0.0.1", output, StringComparison.Ordinal);
        Assert.Equal(
            Line("10.0.0.10", "10.0.0.10", 1080, "TCP") + "\n" + Line("110.0.0.1", "110.0.0.1", 23, "TCP") + "\n",
            Read("var/lib/portsentry/portsentry.blocked.atcp"));
        Assert.Equal("10.0.0.11\n", Read("var/lib/portsentry/portsentry.blocked.udp"));
        Assert.Equal(Line("10.0.0.100", "10.0.0.100", 25, "TCP") + "\n", Read("var/lib/portsentry/portsentry.blocked.stcp"));
        Assert.Equal("ALL: 10.0.0.10\n", Read("etc/hosts.deny"));
    }

    [Fact]
    public async Task Unblock_OfAnIpv6Address_RemovesOnlyThatAddress()
    {
        Write("etc/portsentry/portsentry.conf", string.Empty);
        Write("var/lib/portsentry/portsentry.blocked.tcp",
            Line("2001:db8::7", "2001:db8::7", 1080, "TCP") + "\n" + Line("2001:db8::70", "2001:db8::70", 1080, "TCP") + "\n");

        var (exit, output) = await RunAsync("2001:db8::7");

        Assert.Equal(0, exit);
        Assert.Contains("1 line(s) removed", output, StringComparison.Ordinal);
        Assert.Equal(Line("2001:db8::70", "2001:db8::70", 1080, "TCP") + "\n", Read("var/lib/portsentry/portsentry.blocked.tcp"));
    }

    [Theory]
    [InlineData("BLOCKED_FILE=\"{0}/custom.blocked\"\n")]
    [InlineData("  BLOCKED_FILE={0}/custom.blocked  \r\n")]
    [InlineData("BLOCKED_FILE=\"\"\nBLOCKED_FILE={0}/custom.blocked\n")]
    public async Task Unblock_ResolvesTheBaseFromBlockedFile_QuotedOrNot_LikeTheCollector(string conf)
    {
        Write("etc/portsentry/portsentry.conf", string.Format(System.Globalization.CultureInfo.InvariantCulture, conf, ShellDir));
        Write("var/lib/portsentry/custom.blocked.audp", Line("203.0.113.66", "203.0.113.66", 161, "UDP") + "\n" + Line("203.0.113.6", "203.0.113.6", 161, "UDP") + "\n");
        Write("var/lib/portsentry/portsentry.blocked.audp", Line("203.0.113.66", "203.0.113.66", 161, "UDP") + "\n");

        var (exit, output) = await RunAsync("203.0.113.66");

        Assert.Equal(0, exit);
        Assert.Contains("unblocked 203.0.113.66", output, StringComparison.Ordinal);
        Assert.Equal(Line("203.0.113.6", "203.0.113.6", 161, "UDP") + "\n", Read("var/lib/portsentry/custom.blocked.audp"));
        // Only the configured base is PortSentry's: the default one is not touched.
        Assert.Equal(Line("203.0.113.66", "203.0.113.66", 161, "UDP") + "\n", Read("var/lib/portsentry/portsentry.blocked.audp"));
    }

    [Theory]
    [InlineData("BLOCKED_FILE={1}/outside/evil.blocked\n")]
    [InlineData("BLOCKED_FILE={0}/../../../outside/evil.blocked\n")]
    [InlineData("BLOCKED_FILE=outside/evil.blocked\n")]
    [InlineData("BLOCKED_FILE={0}/\n")]
    [InlineData("BLOCKED_FILE=\"{0}/evil blocked\"\n")]
    public async Task Unblock_RefusesABaseOutsideThePortsentryFolder_AndTouchesNoBlockedFile(string conf)
    {
        var outside = Path.Combine(_root, "outside");
        Directory.CreateDirectory(outside);
        var victim = Line("203.0.113.66", "203.0.113.66", 1080, "TCP") + "\n";
        Write("outside/evil.blocked.tcp", victim);
        Write("var/lib/portsentry/portsentry.blocked.tcp", victim);
        Write("etc/portsentry/portsentry.conf",
            string.Format(System.Globalization.CultureInfo.InvariantCulture, conf, ShellDir, ToShellPath(_root)));

        var (exit, output) = await RunAsync("203.0.113.66");

        Assert.Equal(1, exit);
        Assert.Contains("refused", output, StringComparison.Ordinal);
        Assert.DoesNotContain("unblocked 203.0.113.66", output, StringComparison.Ordinal);
        Assert.Equal(victim, Read("outside/evil.blocked.tcp"));
        Assert.Equal(victim, Read("var/lib/portsentry/portsentry.blocked.tcp"));
    }

    [Fact]
    public async Task Unblock_ABlockedPathThatIsNotARegularFile_FailsWithoutSayingUnblocked()
    {
        Write("etc/portsentry/portsentry.conf", string.Empty);
        Directory.CreateDirectory(Path.Combine(Dir, "portsentry.blocked.tcp"));
        Write("var/lib/portsentry/portsentry.blocked.udp", Line("203.0.113.66", "203.0.113.66", 161, "UDP") + "\n");

        var (exit, output) = await RunAsync("203.0.113.66");

        Assert.Equal(1, exit);
        Assert.DoesNotContain("unblocked 203.0.113.66", output, StringComparison.Ordinal);
        // The other files are still cleaned: one bad path does not leave the rest blocked.
        Assert.Equal(string.Empty, Read("var/lib/portsentry/portsentry.blocked.udp"));
    }

    [Fact]
    public async Task Unblock_WithNoBlockedFileAtAll_Succeeds()
    {
        var (exit, output) = await RunAsync("203.0.113.66");

        Assert.Equal(0, exit);
        Assert.Contains("0 line(s) removed", output, StringComparison.Ordinal);
        Assert.Contains("unblocked 203.0.113.66", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_IsShippedByTheManifest_AndRenderedByTheInstaller()
    {
        var manifest = LinuxHostConfigTemplates.Read("manifest").Split('\n');
        Assert.Contains("portsentry/unblock-ip", manifest);
        var installer = File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "agent-installer", "install-agent-linux.sh"));
        Assert.Contains("render_host_config portsentry/unblock-ip \"$UNBLOCK_HELPER_PATH\"", installer, StringComparison.Ordinal);
    }

    [Fact]
    public void Helper_NeverRunsFileContent_AndKeepsItsBoundary()
    {
        var script = LinuxHostConfigTemplates.Read("portsentry/unblock-ip");
        // Outside comments: no eval, no source, no "." builtin, nothing that runs what it reads.
        Assert.DoesNotMatch(@"(?m)^[^#]*\b(eval|source|exec)\b", script);
        Assert.DoesNotMatch(@"(?m)^\s*\.\s", script);
        Assert.Contains("PORTSENTRY_BLOCKED_DIR=" + BlockedDir + "\n", script, StringComparison.Ordinal);
        Assert.Contains("PORTSENTRY_CONF=" + Conf + "\n", script, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output)> RunAsync(string ip)
    {
        var script = LinuxHostConfigTemplates.Read("portsentry/unblock-ip");
        foreach (var path in new[] { HostsDeny, Conf, BlockedDir })
            Assert.Contains(path, script, StringComparison.Ordinal);
        var sandboxed = script
            .Replace(HostsDeny, ToShellPath(Path.Combine(_root, "etc", "hosts.deny")), StringComparison.Ordinal)
            .Replace(Conf, ToShellPath(Path.Combine(_root, "etc", "portsentry", "portsentry.conf")), StringComparison.Ordinal)
            .Replace(BlockedDir, ShellDir, StringComparison.Ordinal);
        var helper = Path.Combine(_root, "unblock-ip");
        WriteExecutable(helper, sandboxed);

        var start = new ProcessStartInfo(OperatingSystem.IsWindows() ? @"C:\Program Files\Git\bin\bash.exe" : "/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add(ToShellPath(helper));
        start.ArgumentList.Add(ip);
        start.Environment["PATH"] = Path.Combine(_root, "bin") + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the helper.");
        var stdout = process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);
        return (process.ExitCode, await stdout + await stderr);
    }

    private void Write(string relative, string content) =>
        File.WriteAllText(Path.Combine(_root, relative), content);

    private string Read(string relative) =>
        File.ReadAllText(Path.Combine(_root, relative)).Replace("\r\n", "\n", StringComparison.Ordinal);

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static string ToShellPath(string path) =>
        OperatingSystem.IsWindows() ? "/" + char.ToLowerInvariant(path[0]) + path[2..].Replace('\\', '/') : path;

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
