// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Linux.Collectors;
using Aetheus.Tests.Shared;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The kernel log fallback chain that names an OOM victim under the shipped systemd unit: /dev/kmsg
/// first (hidden by ProtectKernelLogs=true, refused by dmesg_restrict=1), then the journal, which the
/// unit reads through the systemd-journal group; an install without that group degrades to "not readable".
/// </summary>
public sealed class LinuxKernelLogTests
{
    private static readonly string[] JournalLines =
    [
        "eth0: link up",
        "stress invoked oom-killer: gfp_mask=0x140cca(GFP_HIGHUSER_MOVABLE|__GFP_COMP), order=0, oom_score_adj=0",
        "Out of memory: Killed process 4242 (stress) total-vm:2105440kB, anon-rss:1998336kB, file-rss:0kB",
        "Memory cgroup out of memory: Killed process 777 (java) total-vm:4000000kB, anon-rss:900000kB"
    ];

    [Fact]
    public void ReadRecords_WhenKmsgIsReadable_DoesNotConsultTheJournal()
    {
        string[] kmsg = ["3,1,2,-;Out of memory: Killed process 1 (a) total-vm:1kB"];
        var journalReads = 0;

        var records = LinuxKernelLog.ReadRecords(() => kmsg, () => { journalReads++; return JournalLines; });

        Assert.Same(kmsg, records);
        Assert.Equal(0, journalReads);
    }

    [Fact]
    public void ReadRecords_WhenKmsgIsReadableButEmpty_StaysEmptyAndSkipsTheJournal()
    {
        var journalReads = 0;

        var records = LinuxKernelLog.ReadRecords(() => [], () => { journalReads++; return JournalLines; });

        Assert.NotNull(records);
        Assert.Empty(records);
        Assert.Equal(0, journalReads);
    }

    [Fact]
    public void ReadRecords_WhenKmsgIsRefused_FallsBackToTheJournal()
    {
        var records = LinuxKernelLog.ReadRecords(() => null, () => JournalLines);

        Assert.Same(JournalLines, records);
    }

    [Fact]
    public void ReadRecords_WhenNeitherSourceIsReadable_IsNull() =>
        Assert.Null(LinuxKernelLog.ReadRecords(() => null, () => null));

    [Fact]
    public void JournalLines_NameTheVictimsThroughTheTracker()
    {
        var tracker = new OomKillTracker(
            () => "oom_kill 2\n",
            () => "boot-1\n",
            () => LinuxKernelLog.ReadRecords(() => null, () => JournalLines));

        var report = tracker.Read();

        Assert.NotNull(report);
        Assert.True(report.VictimsReadable);
        Assert.Equal([(4242, "stress"), (777, "java")], report.RecentVictims.Select(v => (v.Pid, v.Process)));
    }

    [Fact]
    public void Tracker_WithoutAnyReadableSource_ReportsTheCountWithoutAName()
    {
        var tracker = new OomKillTracker(
            () => "oom_kill 2\n",
            () => "boot-1\n",
            () => LinuxKernelLog.ReadRecords(() => null, () => null));

        var report = tracker.Read();

        Assert.NotNull(report);
        Assert.Equal(2, report.KillCount);
        Assert.False(report.VictimsReadable);
        Assert.Empty(report.RecentVictims);
    }

    [Fact]
    public void ParseOutput_KeepsTheMessageLinesAndDropsBlankOnes()
    {
        var records = LinuxJournalKernelLog.ParseOutput(0, ["first", "", "  ", "second"]);

        Assert.Equal(["first", "second"], records);
    }

    [Fact]
    public void ParseOutput_NonZeroExit_IsNotReadable() =>
        Assert.Null(LinuxJournalKernelLog.ParseOutput(1, JournalLines));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ParseOutput_NoLines_IsNotReadable_NeverAnEmptyProof(int blankLines) =>
        // Not in systemd-journal: journalctl exits 0 and lists nothing of the system. A booted kernel
        // always logs, so an empty answer must not be mistaken for "no OOM kill".
        Assert.Null(LinuxJournalKernelLog.ParseOutput(0, Enumerable.Repeat(string.Empty, blankLines)));

    [Fact]
    public void Read_WhenJournalctlIsNotInstalled_IsNotReadable() =>
        Assert.Null(LinuxJournalKernelLog.Read("aetheus-no-such-journalctl-binary", LinuxJournalKernelLog.Arguments));

    [Fact]
    public void JournalArguments_AskForTheCurrentBootKernelMessagesAsPlainText()
    {
        Assert.Contains("--dmesg", LinuxJournalKernelLog.Arguments);
        Assert.Contains("--boot=0", LinuxJournalKernelLog.Arguments);
        Assert.Contains("--output=cat", LinuxJournalKernelLog.Arguments);
        Assert.Contains("--no-pager", LinuxJournalKernelLog.Arguments);
    }

    /// <summary>A real child process: its stdout lines come back, a non-zero exit and an empty run do not.</summary>
    [PlatformFact("linux")]
    public void Read_RunsARealProcessAndAppliesTheSameRules()
    {
        var lines = LinuxJournalKernelLog.Read("printf", ["Out of memory: Killed process 42 (worker) total-vm:1kB\\nsecond\\n"]);
        Assert.NotNull(lines);
        Assert.Equal(["Out of memory: Killed process 42 (worker) total-vm:1kB", "second"], lines);

        Assert.Null(LinuxJournalKernelLog.Read("false", []));
        Assert.Null(LinuxJournalKernelLog.Read("true", []));
    }
}
