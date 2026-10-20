// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Agent.Core.Collectors;
using Aetheus.Agent.Linux.Collectors;
using Aetheus.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Agent.Core.Tests;

/// <summary>PLAN-010 lot 1: OOM kills read from /proc/vmstat and named from the kernel log.</summary>
public sealed class OomKillCollectionTests
{
    private const string Vmstat = """
        nr_free_pages 123456
        pgfault 987654
        oom_kill 3
        compact_stall 0
        """;

    private const string BootId = "5a8c1f9e-0d2b-4c55-9a77-1b2c3d4e5f60\n";

    // /dev/kmsg records as the kernel prints them: global OOM, cgroup OOM, and unrelated lines.
    private static readonly string[] KernelLog =
    [
        "6,1001,5000000,-;eth0: link up",
        "4,1002,6000000,-;stress invoked oom-killer: gfp_mask=0x140cca(GFP_HIGHUSER_MOVABLE|__GFP_COMP), order=0, oom_score_adj=0",
        "6,1003,6000100,-;oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/user.slice,task=stress,pid=4242,uid=1000",
        "3,1004,6000200,-;Out of memory: Killed process 4242 (stress) total-vm:2105440kB, anon-rss:1998336kB, file-rss:0kB, shmem-rss:0kB, UID:1000 pgtables:3960kB oom_score_adj:0",
        "3,1005,7000000,-;Memory cgroup out of memory: Killed process 777 (java) total-vm:4000000kB, anon-rss:900000kB",
        "3,1006,8000000,-;Out of memory: Killed process 31337 (Web Content) total-vm:100kB"
    ];

    [Theory]
    [InlineData(Vmstat, 3L)]
    [InlineData("oom_kill 0\n", 0L)]
    [InlineData("nr_free_pages 1\noom_kill_x 9\n", null)] // kernel before 4.13: no counter
    [InlineData("oom_kill abc\n", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ParseVmstatOomKill_ReadsTheCounterOrReportsUnknown(string? vmstat, long? expected) =>
        Assert.Equal(expected, OomKillParser.ParseVmstatOomKill(vmstat));

    [Fact]
    public void ParseVictims_NamesGlobalAndCgroupKillsOldestFirst()
    {
        var victims = OomKillParser.ParseVictims(KernelLog);

        Assert.Equal(
            [(4242, "stress"), (777, "java"), (31337, "Web Content")],
            victims.Select(victim => (victim.Pid, victim.Process)));
    }

    [Fact]
    public void ParseVictims_KeepsOnlyTheNewestMaxVictims()
    {
        var records = Enumerable.Range(1, OomKillReportDto.MaxVictims + 4)
            .Select(pid => $"Out of memory: Killed process {pid} (p{pid}) total-vm:1kB");

        var victims = OomKillParser.ParseVictims(records);

        Assert.Equal(OomKillReportDto.MaxVictims, victims.Count);
        Assert.Equal(5, victims[0].Pid);
        Assert.Equal(OomKillReportDto.MaxVictims + 4, victims[^1].Pid);
    }

    [Fact]
    public void Tracker_ReportsCounterBootIdAndVictims()
    {
        var tracker = new OomKillTracker(() => Vmstat, () => BootId, () => KernelLog);

        var report = tracker.Read();

        Assert.NotNull(report);
        Assert.Equal("5a8c1f9e-0d2b-4c55-9a77-1b2c3d4e5f60", report.BootId);
        Assert.Equal(3, report.KillCount);
        Assert.True(report.VictimsReadable);
        Assert.Equal([4242, 777, 31337], report.RecentVictims.Select(victim => victim.Pid));
    }

    [Fact]
    public void Tracker_WithoutKernelLogAccess_ReportsTheCounterOnly()
    {
        var tracker = new OomKillTracker(() => Vmstat, () => BootId, () => null);

        var report = tracker.Read();

        Assert.NotNull(report);
        Assert.Equal(3, report.KillCount);
        Assert.False(report.VictimsReadable);
        Assert.Empty(report.RecentVictims);
    }

    [Theory]
    [InlineData(null, BootId)]
    [InlineData("nr_free_pages 1\n", BootId)]
    [InlineData(Vmstat, null)]
    [InlineData(Vmstat, "  \n")]
    public void Tracker_UnreadableCounterOrBootId_IsUnknown(string? vmstat, string? bootId)
    {
        var tracker = new OomKillTracker(() => vmstat, () => bootId, () => KernelLog);

        Assert.Null(tracker.Read());
    }

    [Fact]
    public void Tracker_ReadsTheKernelLogOnlyWhenTheCounterMoves()
    {
        var count = 3;
        var logReads = 0;
        var tracker = new OomKillTracker(
            () => $"oom_kill {count.ToString(CultureInfo.InvariantCulture)}\n",
            () => BootId,
            () => { logReads++; return KernelLog; });

        tracker.Read();
        tracker.Read();
        tracker.Read();
        Assert.Equal(1, logReads);

        count = 4;
        var report = tracker.Read();
        Assert.Equal(2, logReads);
        Assert.Equal(4, report!.KillCount);
    }

    [Fact]
    public void Tracker_KillDuringTheLogRead_RereadsSoCountAndVictimsMatch()
    {
        var vmstatReads = 0;
        string[] firstLog = [KernelLog[3]];
        // The counter reads 1 before the first log read and 2 after it: a kill landed in between.
        var tracker = new OomKillTracker(
            () => ++vmstatReads == 1 ? "oom_kill 1\n" : "oom_kill 2\n",
            () => BootId,
            () => vmstatReads <= 1 ? firstLog : KernelLog);

        var report = tracker.Read();

        Assert.Equal(2, report!.KillCount);
        Assert.Equal(31337, report.RecentVictims[^1].Pid);
    }

    [Fact]
    public void WindowsAndDefaultCollectors_DoNotReportOomKills()
    {
        var heartbeat = new NoOomCollector().Collect();

        Assert.Null(heartbeat.OomKills);
    }

    /// <summary>The real collector on a real Linux kernel: its counter equals the one /proc/vmstat holds.</summary>
    [PlatformFact("linux")]
    public void LinuxCollector_ReadsTheRealVmstatCounter()
    {
        var before = OomKillParser.ParseVmstatOomKill(File.ReadAllText("/proc/vmstat"));
        var report = new LinuxMetricsCollector(NullLogger<LinuxMetricsCollector>.Instance).Collect().OomKills;
        var after = OomKillParser.ParseVmstatOomKill(File.ReadAllText("/proc/vmstat"));
        var rawLine = File.ReadAllLines("/proc/vmstat").Single(line => line.StartsWith("oom_kill ", StringComparison.Ordinal));
        var bootId = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();

        TestContext.Current.SendDiagnosticMessage(
            $"OOM proof: /proc/vmstat line '{rawLine}', boot_id {bootId}; collector KillCount={report?.KillCount}, " +
            $"BootId={report?.BootId}, VictimsReadable={report?.VictimsReadable}, Victims=[{string.Join(", ", report?.RecentVictims.Select(v => $"{v.Process} (pid {v.Pid})") ?? [])}]");
        Assert.NotNull(report);
        Assert.InRange(report.KillCount, before!.Value, after!.Value);
        Assert.Equal(bootId, report.BootId);
    }

    private sealed class NoOomCollector : BaseMetricsCollector
    {
        protected override double GetCpuPercent() => 0;
        protected override double GetMemoryUsedMb() => 0;
        protected override double GetMemoryTotalMb() => 0;
    }
}
