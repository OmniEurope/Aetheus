// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Recette R3-36: every RKHunter task failed on "/usr/bin/grep: Permission denied" and
/// "Unable to determine if you are root", because the sudoers grant carried <c>noexec</c> and rkhunter,
/// a shell script, must exec grep, tr and cut (reproduced in a Debian container with the old drop-in).
/// The grant now names a root-owned helper (ADR-019 exception) that runs the fixed rkhunter argv; these
/// tests hold the agent, the helper template, the sudoers template and the installer to one contract.</summary>
public sealed partial class RkhunterHelperAuditTests
{
    [Fact]
    public void HelperPath_MatchesTheInstaller()
    {
        var script = InstallScript();
        Assert.Equal(RkhunterOperationExecutor.HelperPath, Variable(script, "RKHUNTER_HELPER_PATH"));
        Assert.Contains("render_host_config rkhunter/aetheus-rkhunter \"$RKHUNTER_HELPER_PATH\"", script, StringComparison.Ordinal);
        Assert.Contains("chown root:root \"$RKHUNTER_HELPER_PATH\"", script, StringComparison.Ordinal);
        Assert.Contains("chmod 755 \"$RKHUNTER_HELPER_PATH\"", script, StringComparison.Ordinal);
    }

    [Fact]
    public void Sudoers_GrantsTheHelperWordsOnly_WithoutNoexec()
    {
        var sudoers = LinuxHostConfigTemplates.Read("rkhunter/sudoers.d/aetheus-rkhunter");

        foreach (var word in new[] { "check", "update", "propupd" })
            Assert.Contains($"#{{RKHUNTER_HELPER_PATH}}# {word}", sudoers, StringComparison.Ordinal);
        Assert.DoesNotContain("/usr/bin/rkhunter", sudoers, StringComparison.Ordinal);
        Assert.DoesNotMatch(NoexecDefault(), sudoers);
        Assert.Contains("Defaults!AETHEUS_RKHUNTER_MANAGE env_reset", sudoers, StringComparison.Ordinal);
        Assert.Contains("Defaults!AETHEUS_RKHUNTER_MANAGE timestamp_timeout=0", sudoers, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(OperationKind.RkhunterScan, "check", "/usr/bin/rkhunter --check --skip-keypress --nocolors --report-warnings-only")]
    [InlineData(OperationKind.RkhunterUpdate, "update", "/usr/bin/rkhunter --update --nocolors")]
    [InlineData(OperationKind.RkhunterPropupd, "propupd", "/usr/bin/rkhunter --propupd --nocolors")]
    public void EachAgentWord_RunsTheFixedRkhunterArgv(OperationKind kind, string word, string rkhunter)
    {
        Assert.Equal(word, RkhunterOperationExecutor.BuildSudoArgv(kind)![^1]);
        Assert.Contains($"    {word})", Helper(), StringComparison.Ordinal);
        Assert.Matches(new Regex($@"^\s+{word}\)\s+{Regex.Escape(rkhunter)} ;;$", RegexOptions.Multiline), Helper());
    }

    [Fact]
    public void Helper_RefusesAnythingButOneWord_AndKeepsRkhunterExitCode()
    {
        var helper = Helper();
        Assert.StartsWith("#!/bin/sh\n", helper, StringComparison.Ordinal);
        Assert.Contains("[ \"$#\" -eq 1 ] ||", helper, StringComparison.Ordinal);
        Assert.Contains("*) echo", helper, StringComparison.Ordinal);
        Assert.Contains("exit \"$rc\"", helper, StringComparison.Ordinal);
        // The agent reads the scan from the log rkhunter rewrites 0600: read access for the agent alone.
        Assert.Contains("setfacl -m \"u:#{AGENT_USER}#:r\" \"$LOG\"", helper, StringComparison.Ordinal);
    }

    [Fact]
    public void ScanThatFoundWarnings_IsACompletedScan()
    {
        var exitOne = new ExecutorResult(1, false);

        Assert.True(RkhunterOperationExecutor.IsCompletedScanWithWarnings(OperationKind.RkhunterScan, exitOne, 3));
        Assert.True(RkhunterOperationExecutor.IsScanWarning("Warning: The kernel modules directory '/lib/modules' is missing or empty."));
    }

    [Fact]
    public void ScanThatCouldNotRun_StaysAFailure()
    {
        // The R3-36 log: exit 1, "Unable to determine if you are root.", no Warning: line.
        Assert.False(RkhunterOperationExecutor.IsScanWarning("Unable to determine if you are root."));
        Assert.False(RkhunterOperationExecutor.IsScanWarning("[stderr] /usr/bin/rkhunter: line 73: /usr/bin/grep: Permission denied"));
        Assert.False(RkhunterOperationExecutor.IsCompletedScanWithWarnings(OperationKind.RkhunterScan, new ExecutorResult(1, false), 0));
        Assert.False(RkhunterOperationExecutor.IsCompletedScanWithWarnings(OperationKind.RkhunterScan, new ExecutorResult(2, false), 3));
        Assert.False(RkhunterOperationExecutor.IsCompletedScanWithWarnings(OperationKind.RkhunterScan, new ExecutorResult(1, true), 3));
        Assert.False(RkhunterOperationExecutor.IsCompletedScanWithWarnings(OperationKind.RkhunterUpdate, new ExecutorResult(1, false), 3));
    }

    private static string Helper() => LinuxHostConfigTemplates.Read("rkhunter/aetheus-rkhunter");

    private static string InstallScript() =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "deploy", "agent-installer", "install-agent-linux.sh"));

    private static string Variable(string script, string name)
    {
        var match = Regex.Match(script, $@"^{Regex.Escape(name)}=""([^""]*)""", RegexOptions.Multiline);
        Assert.True(match.Success, $"{name}=\"...\" not found in install-agent-linux.sh");
        return match.Groups[1].Value;
    }

    [GeneratedRegex(@"^Defaults!AETHEUS_RKHUNTER_MANAGE\s+noexec", RegexOptions.Multiline)]
    private static partial Regex NoexecDefault();
}
