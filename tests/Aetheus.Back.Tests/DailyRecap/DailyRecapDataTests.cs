// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>What the daily recap reads: parent runs only, inside the local day, and each server's facts.</summary>
public sealed class DailyRecapDataTests : IDisposable
{
    private static readonly DateTime Now = new(2026, 10, 9, 5, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Yesterday = new(2026, 10, 8);

    private readonly AppDbContext _db = new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new FakeTimeProvider(new DateTimeOffset(Now)));
    private readonly IAgentCompatibilityPolicy _compatibility = Substitute.For<IAgentCompatibilityPolicy>();
    private readonly DailyRecapRepository _repo;

    public DailyRecapDataTests()
    {
        _repo = new DailyRecapRepository(_db);
        _compatibility.Evaluate(Arg.Any<ServerDto>()).Returns(new AgentCompatibilityDto
        {
            Status = AgentCompatibilityStatus.UpdateRecommended,
            TargetVersion = "2.4.0"
        });
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TimeZoneInfo Paris
    {
        get
        {
            Assert.True(DailyRecapClock.TryResolveTimeZone("Europe/Paris", out var zone));
            return zone;
        }
    }

    private DailyRecapCollector Collector(Dictionary<string, string?>? config = null) => new(
        _repo, _compatibility, new ConfigurationBuilder().AddInMemoryCollection(config ?? []).Build());

    private void SeedPipelines()
    {
        _db.Projects.Add(new Project { Id = 1, Name = "Aetheus" });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 1, Name = "release-candidate", ProjectId = 1 },
            new Pipeline { Id = 2, Name = "ci-back", ProjectId = 1 },
            new Pipeline { Id = 3, Name = "nightly" });
        _db.PipelineRuns.AddRange(
            // The parent, finished at 23:30 Paris time on the 8th.
            new PipelineRun { Id = 10, PipelineId = 1, Status = PipelineStatus.Failed, StartedAt = Utc(8, 20), CompletedAt = Utc(8, 21, 30), BranchName = "develop" },
            // Its child, triggered by a trigger step: its failure is already the parent's.
            new PipelineRun { Id = 11, PipelineId = 2, Status = PipelineStatus.Failed, StartedAt = Utc(8, 20, 10), CompletedAt = Utc(8, 21) },
            // A standalone run at 00:10 Paris time on the 8th: inside the day although it is the 7th in UTC.
            new PipelineRun { Id = 12, PipelineId = 3, Status = PipelineStatus.Success, StartedAt = Utc(7, 22), CompletedAt = Utc(7, 22, 10) },
            // 00:30 Paris time on the 9th: the next day.
            new PipelineRun { Id = 13, PipelineId = 3, Status = PipelineStatus.Success, StartedAt = Utc(8, 22), CompletedAt = Utc(8, 22, 30) },
            // 23:50 Paris time on the 7th: the day before.
            new PipelineRun { Id = 14, PipelineId = 3, Status = PipelineStatus.Success, StartedAt = Utc(7, 21), CompletedAt = Utc(7, 21, 50) },
            // Still running: not a result yet.
            new PipelineRun { Id = 15, PipelineId = 3, Status = PipelineStatus.Running, StartedAt = Utc(8, 9) });
        _db.PipelineStepRuns.Add(new PipelineStepRun { Id = 100, PipelineRunId = 10, StageName = "ci", StepName = "trigger ci-back", TriggeredRunId = 11 });
        _db.SaveChanges();
    }

    private static DateTime Utc(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task ParentRuns_LeaveOutTheChildRuns_AndKeepTheLocalDayOnly()
    {
        SeedPipelines();

        var report = await Collector().CollectAsync(Yesterday, Paris, new HashSet<DailyRecapSection> { DailyRecapSection.Pipelines }, Now, Ct);

        Assert.Equal([12, 10], report.PipelineRuns.Select(run => run.RunId));
        var parent = report.PipelineRuns.Single(run => run.RunId == 10);
        Assert.Equal("release-candidate", parent.PipelineName);
        Assert.Equal("Aetheus", parent.ProjectName);
        Assert.Equal(PipelineStatus.Failed, parent.Status);
    }

    [Fact]
    public async Task TheSameRuns_InAnotherZone_FallOnOtherDays()
    {
        SeedPipelines();

        var report = await Collector().CollectAsync(Yesterday, TimeZoneInfo.Utc, new HashSet<DailyRecapSection> { DailyRecapSection.Pipelines }, Now, Ct);

        // In UTC the 8th holds the parent (21:30) and the 22:30 run, not the 22:10 run of the 7th.
        Assert.Equal([10, 13], report.PipelineRuns.Select(run => run.RunId));
    }

    [Fact]
    public async Task SectionsLeftOut_AreNotRead()
    {
        SeedPipelines();

        var report = await Collector().CollectAsync(Yesterday, Paris, new HashSet<DailyRecapSection> { DailyRecapSection.Servers }, Now, Ct);

        Assert.Empty(report.PipelineRuns);
    }

    [Fact]
    public async Task Servers_CarryTheLatestDiskMeasure_TheUpdateProbe_AndTheAgentVerdict()
    {
        _db.Servers.AddRange(
            new Server { Id = 1, Name = "vps-1", Status = ServerStatus.Online, AgentVersion = "2.3.0", LastHeartbeat = Now },
            new Server { Id = 2, Name = "vps-2", Status = ServerStatus.Offline, AgentVersion = "2.4.0", LastHeartbeat = Now.AddHours(-3) });
        _db.ServerMetrics.AddRange(
            new ServerMetric { Id = 1, ServerId = 1, DiskUsedGb = 10, DiskTotalGb = 100, Timestamp = Now.AddHours(-2) },
            new ServerMetric { Id = 2, ServerId = 1, DiskUsedGb = 85, DiskTotalGb = 100, Timestamp = Now.AddMinutes(-1) });
        _db.SecurityUpdatesStates.Add(new SecurityUpdatesState { Id = 1, ServerId = 1, Probed = true, PendingTotal = 4, PendingSecurity = 1 });
        _db.SaveChanges();

        var report = await Collector().CollectAsync(Yesterday, Paris, new HashSet<DailyRecapSection> { DailyRecapSection.Servers }, Now, Ct);

        var first = report.Servers.Single(server => server.Name == "vps-1");
        Assert.NotNull(first.Disk);
        Assert.Equal(15, first.Disk.FreeGb, 3);
        Assert.Equal(RecapDiskLevel.Low, first.Disk.Level);
        Assert.Equal(new RecapOsUpdates(true, 4, 1), first.OsUpdates);
        Assert.Equal(AgentCompatibilityStatus.UpdateRecommended, first.AgentStatus);
        Assert.Equal("2.4.0", first.TargetAgentVersion);

        var second = report.Servers.Single(server => server.Name == "vps-2");
        Assert.Null(second.Disk);
        Assert.Null(second.OsUpdates);
        Assert.Equal(ServerStatus.Offline, second.Status);
    }

    [Fact]
    public void DiskLevel_FollowsTheStorageMonitoringThresholds()
    {
        static RecapServerFacts Facts(double used) => new(1, "s", ServerStatus.Online, Now, "1.0.0", null, used, 100, Now);

        Assert.Equal(RecapDiskLevel.Ok, DailyRecapCollector.Disk(Facts(79), 80, 90)!.Level);
        Assert.Equal(RecapDiskLevel.Low, DailyRecapCollector.Disk(Facts(80), 80, 90)!.Level);
        Assert.Equal(RecapDiskLevel.Critical, DailyRecapCollector.Disk(Facts(95), 80, 90)!.Level);
        Assert.Null(DailyRecapCollector.Disk(Facts(10) with { DiskTotalGb = 0 }, 80, 90));
    }

    [Fact]
    public async Task OtherSections_ReadTheirDay_OrTheirHorizon()
    {
        _db.Projects.Add(new Project { Id = 1, Name = "shop" });
        _db.Servers.Add(new Server { Id = 1, Name = "vps-1" });
        _db.Releases.AddRange(
            new Release { Id = 1, ProjectId = 1, Version = "1.4.0", PromotedAt = Utc(8, 9) },
            new Release { Id = 2, ProjectId = 1, Version = "1.3.9", RolledBackAt = Utc(8, 12) },
            new Release { Id = 3, ProjectId = 1, Version = "1.3.0", PromotedAt = Utc(1, 9) });
        _db.Set<BackupPolicy>().Add(new BackupPolicy { Id = 1, Name = "db-nightly", ProjectId = 1, ServerId = 1 });
        _db.BackupRuns.AddRange(
            new BackupRun { Id = 1, BackupPolicyId = 1, ServerId = 1, Status = BackupRunStatus.Failed, StartedAt = Utc(8, 1), CompletedAt = Utc(8, 1, 5), Message = "pg_dump: connection refused" },
            new BackupRun { Id = 2, BackupPolicyId = 1, ServerId = 1, Status = BackupRunStatus.Succeeded, StartedAt = Utc(6, 1), CompletedAt = Utc(6, 1, 5) });
        _db.CertbotCertificates.AddRange(
            new CertbotCertificate { Id = 1, ServerId = 1, Name = "shop.example.org", ExpiryDate = Now.AddDays(12) },
            new CertbotCertificate { Id = 2, ServerId = 1, Name = "far.example.org", ExpiryDate = Now.AddDays(80) });
        _db.Vaults.Add(new Vault { Id = 1, Name = "prod" });
        _db.VaultSecrets.AddRange(
            new VaultSecret { Id = 1, VaultId = 1, Key = "STRIPE_KEY", ExpiresAt = Now.AddDays(5) },
            new VaultSecret { Id = 2, VaultId = 1, Key = "NEVER", ExpiresAt = null });
        _db.AnalysisFindings.AddRange(
            new AnalysisFinding { Id = 1, ProjectId = 1, Severity = AnalysisSeverity.High, FirstSeenAt = Utc(8, 10) },
            new AnalysisFinding { Id = 2, ProjectId = 1, Severity = AnalysisSeverity.High, FirstSeenAt = Utc(8, 11) },
            new AnalysisFinding { Id = 3, ProjectId = 1, Severity = AnalysisSeverity.Low, FirstSeenAt = Utc(2, 11), ResolvedAt = Utc(8, 15) });
        _db.MailStates.Add(new MailState { Id = 1, ServerId = 1, IsPostfixRunning = true, IsDovecotRunning = false, QueueSize = 3, LastUpdated = Now });
        _db.SaveChanges();

        var report = await Collector().CollectAsync(Yesterday, Paris, DailyRecapSettings.AllSections, Now, Ct);

        Assert.Equal([ReleaseStatus.Promoted, ReleaseStatus.RolledBack], report.Releases.Select(release => release.Event));
        var backup = Assert.Single(report.Backups);
        Assert.Equal(BackupRunStatus.Failed, backup.Status);
        Assert.Equal("shop", backup.ProjectName);
        Assert.Equal("shop.example.org", Assert.Single(report.Certificates).Name);
        Assert.Equal("STRIPE_KEY", Assert.Single(report.Secrets).Name);
        Assert.Equal(2, report.Findings.NewBySeverity[AnalysisSeverity.High]);
        Assert.Equal(1, report.Findings.Resolved);
        Assert.False(Assert.Single(report.MailServers).DovecotRunning);
    }

    [Fact]
    public async Task DefaultRecipients_AreTheActiveAdministratorsWithAnAddress()
    {
        _db.Roles.AddRange(new Role { Id = 1, Name = "Admin" }, new Role { Id = 2, Name = "Viewer" });
        _db.Users.AddRange(
            new User { Id = 1, Username = "alice", Email = "alice@example.org", IsActive = true },
            new User { Id = 2, Username = "bob", Email = "bob@example.org", IsActive = false },
            new User { Id = 3, Username = "carol", Email = null, IsActive = true },
            new User { Id = 4, Username = "dave", Email = "dave@example.org", IsActive = true });
        _db.UserRoles.AddRange(
            new UserRole { UserId = 1, RoleId = 1 }, new UserRole { UserId = 2, RoleId = 1 },
            new UserRole { UserId = 3, RoleId = 1 }, new UserRole { UserId = 4, RoleId = 2 });
        _db.SaveChanges();

        Assert.Equal(["alice@example.org"], await _repo.GetActiveAdministratorEmailsAsync(Ct));
    }
}
