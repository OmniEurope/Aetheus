// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Services;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Exercises Linux capability diagnostics from a Windows test host through explicit
/// platform selection and test-owned sudoers paths.</summary>
public class CapabilityDiagnosticsProbeTests
{
    private static ShellExecResult SudoOk() => new(0, "User may run the following commands", string.Empty);
    private static ShellExecResult SudoBlocked() => new(1, string.Empty, "sudo: a password is required");

    [Fact]
    public async Task CollectAsync_OnWindows_ReturnsEmpty_AndNeverProbes()
    {
        var shell = Substitute.For<IShellRunner>();

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell, isWindows: true, sudoersFiles: ["/etc/sudoers.d/aetheus-agent"], TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
        await shell.DidNotReceiveWithAnyArgs()
            .RunExecAsync(default!, default!, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task CollectAsync_SudoWorks_NoSudoersFiles_ReturnsEmpty()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(SudoOk());

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell, isWindows: false, sudoersFiles: ["/nonexistent/aetheus-agent"], TestContext.Current.CancellationToken);

        Assert.Empty(diagnostics);
        // The real-execution probe is exactly `sudo -n -l` (S-TECH-CAPX).
        await shell.Received(1).RunExecAsync(
            "sudo",
            Arg.Is<IReadOnlyList<string>>(a => a.SequenceEqual(new[] { "-n", "-l" })),
            Arg.Any<CancellationToken>(),
            Arg.Any<TimeSpan?>());
    }

    [Fact]
    public async Task CollectAsync_SudoProbeFails_ReportsSandboxBlock()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(SudoBlocked());

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell, isWindows: false, sudoersFiles: [], TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("passwordless sudo unavailable", diagnostic);
    }

    [Fact]
    public async Task CollectAsync_SudoBinaryMissing_ReportsProbeCouldNotRun()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns<ShellExecResult>(_ => throw new System.ComponentModel.Win32Exception("file not found"));

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell, isWindows: false, sudoersFiles: [], TestContext.Current.CancellationToken);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("probe could not run", diagnostic);
    }

    [Fact]
    public async Task CollectAsync_SudoersFilePresentButUnreadable_ReportsMissingReadAcl()
    {
        const string unreadableFile = "/etc/sudoers.d/aetheus-agent";
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(SudoOk());

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell,
            isWindows: false,
            sudoersFiles: [unreadableFile],
            TestContext.Current.CancellationToken,
            fileExists: path => path == unreadableFile,
            readAllBytesAsync: (_, _) => Task.FromException<byte[]>(new UnauthorizedAccessException("denied")));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("present but unreadable", diagnostic);
        Assert.Contains(Path.GetFileName(unreadableFile), diagnostic);
    }

    // DEP-019 / D12: the installer leaves a docker membership it did not add, so the agent says it.
    private const string GroupDatabase = "root:x:0:\nadm:x:4:syslog\ndocker:x:998:aetheus-agent\n";

    private static Func<string, CancellationToken, Task<string>> Files(string status, string groups) =>
        (path, _) => Task.FromResult(path switch
        {
            "/proc/self/status" => status,
            "/etc/group" => groups,
            _ => throw new FileNotFoundException(path),
        });

    [Fact]
    public async Task CollectAsync_ProcessCarriesDockerGroup_ReportsRootEquivalentPosture()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(SudoOk());

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell, isWindows: false, sudoersFiles: [], TestContext.Current.CancellationToken,
            readAllTextAsync: Files("Name:\tAetheus.Agent\nUid:\t997\t997\t997\t997\nGroups:\t4 997 998 \n", GroupDatabase));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal("agent account is in the docker group: root-equivalent (full access to the Docker socket)", diagnostic);
    }

    [Theory]
    [InlineData("Groups:\t4 997\n", GroupDatabase)]
    [InlineData("Groups:\t9980 99\n", GroupDatabase)]
    [InlineData("Groups:\t998\n", "root:x:0:\n")]
    public async Task CollectAsync_ProcessWithoutDockerGroup_ReportsNothing(string status, string groups)
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(SudoOk());

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell, isWindows: false, sudoersFiles: [], TestContext.Current.CancellationToken,
            readAllTextAsync: Files(status, groups));

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task CollectAsync_ProcessStatusUnreadable_SaysThePostureIsUnknown()
    {
        var shell = Substitute.For<IShellRunner>();
        shell.RunExecAsync("sudo", Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>(), Arg.Any<TimeSpan?>())
            .Returns(SudoOk());

        var diagnostics = await CapabilityDiagnosticsProbe.CollectAsync(
            shell, isWindows: false, sudoersFiles: [], TestContext.Current.CancellationToken,
            readAllTextAsync: (_, _) => Task.FromException<string>(new UnauthorizedAccessException("denied")));

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("docker group posture could not be read", diagnostic);
    }
}
