// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using System.Text.Json.Serialization;
using Aetheus.Agent.Core.Collectors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The kernel tells a container-limit (cgroup) kill from a host-wide one, and names the cgroup and the
/// memory of the victim. The lines below are RECONSTRUCTED from the facts of the 2026-10-24 production
/// incident (victim dotnet pid 827947 with about 648 MB anonymous, 42 MB file and 204 MB shared memory,
/// constraint CONSTRAINT_MEMCG, task_memcg the docker scope of the container, a git process invoking the
/// oom-killer) in the standard Linux 5.x/6.x kernel format. They are NOT copied from the journal of that
/// host: the container id, total-vm, pgtables and the numbers of the other lines are invented but
/// shaped like the kernel's.
/// </summary>
public sealed class OomKillKindParsingTests
{
    // 64 hex characters, as in a docker scope name; invented for the test.
    private const string ContainerId = "a1b2c3d4e5f60718293a4b5c6d7e8f90a1b2c3d4e5f60718293a4b5c6d7e8f90";
    private const string Scope = "/system.slice/docker-" + ContainerId + ".scope";

    // What `journalctl --dmesg --output=cat` prints: the bare message of each kernel record.
    private static readonly string[] CgroupKillJournal =
    [
        "git invoked oom-killer: gfp_mask=0xcc0(GFP_KERNEL), order=0, oom_score_adj=0",
        "memory: usage 1048576kB, limit 1048576kB, failcnt 4821",
        "oom-kill:constraint=CONSTRAINT_MEMCG,nodemask=(null),cpuset=docker-" + ContainerId + ".scope,mems_allowed=0,oom_memcg=" + Scope + ",task_memcg=" + Scope + ",task=dotnet,pid=827947,uid=0",
        "Memory cgroup out of memory: Killed process 827947 (dotnet) total-vm:2843260kB, anon-rss:663552kB, file-rss:43008kB, shmem-rss:208896kB, UID:0 pgtables:3128kB oom_score_adj:0"
    ];

    // What /dev/kmsg returns: "priority,sequence,timestamp,flags;message" for each record.
    private static readonly string[] CgroupKillKmsg =
    [
        "6,9000,12345678901,-;eth0: link up",
        "4,9001,12345678902,-;git invoked oom-killer: gfp_mask=0xcc0(GFP_KERNEL), order=0, oom_score_adj=0",
        "6,9002,12345678903,-;oom-kill:constraint=CONSTRAINT_MEMCG,nodemask=(null),cpuset=docker-" + ContainerId + ".scope,mems_allowed=0,oom_memcg=" + Scope + ",task_memcg=" + Scope + ",task=dotnet,pid=827947,uid=0",
        "3,9003,12345678904,-;Memory cgroup out of memory: Killed process 827947 (dotnet) total-vm:2843260kB, anon-rss:663552kB, file-rss:43008kB, shmem-rss:208896kB, UID:0 pgtables:3128kB oom_score_adj:0"
    ];

    private static readonly string[] GlobalKill =
    [
        "stress invoked oom-killer: gfp_mask=0x140cca(GFP_HIGHUSER_MOVABLE|__GFP_COMP), order=0, oom_score_adj=0",
        "oom-kill:constraint=CONSTRAINT_NONE,nodemask=(null),cpuset=/,mems_allowed=0,global_oom,task_memcg=/user.slice/user-1000.slice,task=stress,pid=4242,uid=1000",
        "Out of memory: Killed process 4242 (stress) total-vm:2105440kB, anon-rss:1998336kB, file-rss:1024kB, shmem-rss:0kB, UID:1000 pgtables:3960kB oom_score_adj:0"
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CgroupKill_IsRecognisedWithItsContainerAndItsMemory(bool kmsgRecords)
    {
        var victim = Assert.Single(OomKillParser.ParseVictims(kmsgRecords ? CgroupKillKmsg : CgroupKillJournal));

        Assert.Equal(827947, victim.Pid);
        Assert.Equal("dotnet", victim.Process);
        Assert.Equal(OomKillKind.Cgroup, victim.Kind);
        Assert.Equal(Scope, victim.Cgroup);
        Assert.Equal("a1b2c3d4e5f6", victim.ContainerId);
        Assert.Equal(2843260, victim.TotalVmKb);
        Assert.Equal(663552, victim.AnonRssKb);
        Assert.Equal(43008, victim.FileRssKb);
        Assert.Equal(208896, victim.ShmemRssKb);
    }

    [Fact]
    public void GlobalKill_IsRecognisedAndHasNoContainer()
    {
        var victim = Assert.Single(OomKillParser.ParseVictims(GlobalKill));

        Assert.Equal(OomKillKind.Global, victim.Kind);
        Assert.Equal("/user.slice/user-1000.slice", victim.Cgroup);
        Assert.Null(victim.ContainerId);
        Assert.Equal(1998336, victim.AnonRssKb);
        Assert.Equal(0, victim.ShmemRssKb);
    }

    [Fact]
    public void BothKillsOfTheSameLog_KeepTheirOwnKindAndContainer()
    {
        var victims = OomKillParser.ParseVictims([.. GlobalKill, .. CgroupKillJournal]);

        Assert.Equal(
            [(4242, OomKillKind.Global, (string?)null), (827947, OomKillKind.Cgroup, "a1b2c3d4e5f6")],
            victims.Select(v => (v.Pid, v.Kind, v.ContainerId)));
    }

    [Theory]
    [InlineData("Memory cgroup out of memory: Killed process 77 (java) total-vm:10kB, anon-rss:5kB, file-rss:1kB, shmem-rss:0kB", OomKillKind.Cgroup)]
    [InlineData("Out of memory: Killed process 77 (java) total-vm:10kB, anon-rss:5kB, file-rss:1kB, shmem-rss:0kB", OomKillKind.Global)]
    [InlineData("Killed process 77 (java) total-vm:10kB, anon-rss:5kB, file-rss:1kB, shmem-rss:0kB", OomKillKind.Unknown)]
    public void KillLineAlone_FallsBackOnItsOwnPhrase(string line, OomKillKind expected)
    {
        var victim = Assert.Single(OomKillParser.ParseVictims([line]));

        Assert.Equal(expected, victim.Kind);
        Assert.Null(victim.Cgroup);
        Assert.Null(victim.ContainerId);
        Assert.Equal(5, victim.AnonRssKb);
    }

    [Theory]
    [InlineData("CONSTRAINT_CPUSET", OomKillKind.Cpuset)]
    [InlineData("CONSTRAINT_MEMORY_POLICY", OomKillKind.MemoryPolicy)]
    [InlineData("CONSTRAINT_NONE", OomKillKind.Global)]
    [InlineData("CONSTRAINT_MEMCG", OomKillKind.Cgroup)]
    public void TheConstraintOfTheSummaryLine_WinsOverTheKillLinePhrase(string constraint, OomKillKind expected)
    {
        string[] records =
        [
            $"oom-kill:constraint={constraint},nodemask=0,2,cpuset=/,mems_allowed=0,2,task_memcg=/system.slice/ssh.service,task=sshd,pid=51,uid=0",
            "Out of memory: Killed process 51 (sshd) total-vm:1kB, anon-rss:1kB, file-rss:1kB, shmem-rss:1kB"
        ];

        Assert.Equal(expected, Assert.Single(OomKillParser.ParseVictims(records)).Kind);
    }

    [Fact]
    public void SummaryLineOfAnotherProcess_IsNotAppliedToTheKill()
    {
        string[] records =
        [
            "oom-kill:constraint=CONSTRAINT_MEMCG,nodemask=(null),cpuset=/,mems_allowed=0,oom_memcg=" + Scope + ",task_memcg=" + Scope + ",task=dotnet,pid=100,uid=0",
            "Out of memory: Killed process 200 (stress) total-vm:1kB, anon-rss:1kB, file-rss:1kB, shmem-rss:1kB"
        ];

        var victim = Assert.Single(OomKillParser.ParseVictims(records));

        Assert.Equal(OomKillKind.Global, victim.Kind);
        Assert.Null(victim.ContainerId);
    }

    [Fact]
    public void MemoryFieldsAbsentFromTheLine_StayUnknownNotZero()
    {
        var victim = Assert.Single(OomKillParser.ParseVictims(["Out of memory: Killed process 31337 (Web Content) total-vm:100kB"]));

        Assert.Equal(100, victim.TotalVmKb);
        Assert.Null(victim.AnonRssKb);
        Assert.Null(victim.FileRssKb);
        Assert.Null(victim.ShmemRssKb);
    }

    [Theory]
    [InlineData("/system.slice/docker-" + ContainerId + ".scope", "a1b2c3d4e5f6")]
    [InlineData("/system.slice/docker-" + ContainerId + ".scope/init.scope", "a1b2c3d4e5f6")]
    [InlineData("/docker/" + ContainerId, "a1b2c3d4e5f6")]
    [InlineData("/user.slice/user-1000.slice", null)]
    [InlineData("/system.slice/ssh.service", null)]
    [InlineData("/system.slice/docker-abc.scope", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ContainerIdFromCgroup_ReadsTheDockerScopeOnly(string? cgroup, string? expected) =>
        Assert.Equal(expected, OomKillParser.ContainerIdFromCgroup(cgroup));

    [Fact]
    public void Tracker_ReportsTheKindAndTheContainerOfTheLatestKills()
    {
        var tracker = new OomKillTracker(() => "oom_kill 1\n", () => "boot\n", () => CgroupKillKmsg);

        var victim = Assert.Single(tracker.Read()!.RecentVictims);

        Assert.Equal(OomKillKind.Cgroup, victim.Kind);
        Assert.Equal("a1b2c3d4e5f6", victim.ContainerId);
    }

    private static readonly JsonSerializerOptions Wire = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    [Fact]
    public void ReportFromAnOlderAgent_StillDeserialisesAndKeepsTheUnknownKind()
    {
        const string older = """{"bootId":"b","killCount":1,"victimsReadable":true,"recentVictims":[{"pid":4242,"process":"stress"}]}""";

        var victim = Assert.Single(JsonSerializer.Deserialize<OomKillReportDto>(older, Wire)!.RecentVictims);

        Assert.Equal(OomKillKind.Unknown, victim.Kind);
        Assert.Null(victim.Cgroup);
        Assert.Null(victim.ContainerId);
        Assert.Null(victim.AnonRssKb);
    }

    [Fact]
    public void ReportWithKinds_RoundTripsThroughTheWireFormat()
    {
        var victim = Assert.Single(OomKillParser.ParseVictims(CgroupKillKmsg));
        var json = JsonSerializer.Serialize(new OomKillReportDto { BootId = "b", KillCount = 1, VictimsReadable = true, RecentVictims = [victim] }, Wire);

        var back = Assert.Single(JsonSerializer.Deserialize<OomKillReportDto>(json, Wire)!.RecentVictims);

        Assert.Contains("\"kind\":\"Cgroup\"", json, StringComparison.Ordinal);
        Assert.Equal(victim, back);
    }
}
