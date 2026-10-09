// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Backups;

public class BackupPolicyServiceTests
{
    private readonly IBackupRepository _repo = Substitute.For<IBackupRepository>();
    private readonly IServerRepository _serverRepo = Substitute.For<IServerRepository>();
    private readonly ITaskService _taskService = Substitute.For<ITaskService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly BackupPolicyService _sut;

    public BackupPolicyServiceTests()
    {
        _encryption.EncryptValue(Arg.Any<string>()).Returns(ci => "enc:" + ci.Arg<string>());
        _encryption.DecryptValue(Arg.Any<string>()).Returns(ci => ci.Arg<string>()["enc:".Length..]);
        _repo.GetProjectOrganizationIdAsync(Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(4);
        _sut = new BackupPolicyService(
            _repo, _serverRepo, _taskService, _encryption, _time, _audit, _notifier);
    }

    [Fact]
    public async Task Create_EncryptsPassword_AndReturnsDtoWithoutSecret()
    {
        BackupPolicy? captured = null;
        _repo.When(r => r.AddPolicy(Arg.Any<BackupPolicy>())).Do(ci => captured = ci.Arg<BackupPolicy>());

        var dto = await _sut.CreateAsync(new CreateBackupPolicyRequest
        {
            Name = "toto-db",
            ProjectId = 1,
            ServerId = 2,
            DbEngine = BackupDbEngine.Postgres,
            DbName = "toto",
            DbUser = "app",
            DbPassword = "s3cret",
            ScheduleCron = "0 3 * * *",
            RetentionCount = 7
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("enc:s3cret", captured!.DbPasswordEncrypted); // stored encrypted
        Assert.True(dto.HasPassword);
        Assert.Equal("0 3 * * *", dto.ScheduleCron);
    }

    [Theory]
    [InlineData("DbHost")]
    [InlineData("DbPort")]
    [InlineData("DbName")]
    [InlineData("DbUser")]
    [InlineData("DbPassword")]
    [InlineData("DbEngine")]
    [InlineData("FilePaths")]
    public async Task GetServerForAgentParameterChange_AnyAgentParameterChanged_ReturnsThePolicyServer(string field)
    {
        _repo.FindPolicyAsync(4, Arg.Any<CancellationToken>()).Returns(ApprovedPolicy());
        var request = SameAsApproved();
        request = field switch
        {
            "DbHost" => request with { DbHost = "10.0.0.5" },
            "DbPort" => request with { DbPort = 5433 },
            "DbName" => request with { DbName = "other" },
            "DbUser" => request with { DbUser = "root" },
            "DbPassword" => request with { DbPassword = "new-secret" },
            "DbEngine" => request with { DbEngine = BackupDbEngine.MySql },
            _ => request with { FilePaths = ["/etc"] }
        };

        Assert.Equal(2, await _sut.GetServerForAgentParameterChangeAsync(4, request, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetServerForAgentParameterChange_OnlyNameScheduleOrSamePassword_ReturnsNull()
    {
        _repo.FindPolicyAsync(4, Arg.Any<CancellationToken>()).Returns(ApprovedPolicy());
        var request = SameAsApproved() with
        {
            Name = "renamed",
            Enabled = false,
            ScheduleCron = "0 5 * * *",
            RetentionCount = 3,
            DbPassword = "s3cret"
        };

        Assert.Null(await _sut.GetServerForAgentParameterChangeAsync(4, request, TestContext.Current.CancellationToken));
    }

    private static BackupPolicy ApprovedPolicy() => new()
    {
        Id = 4,
        Name = "db",
        ProjectId = 1,
        ServerId = 2,
        DbEngine = BackupDbEngine.Postgres,
        DbHost = "localhost",
        DbPort = 5432,
        DbName = "app",
        DbUser = "app",
        DbPasswordEncrypted = "enc:s3cret",
        FilePathsJson = null,
        ScheduleCron = "0 3 * * *",
        RetentionCount = 7
    };

    private static UpdateBackupPolicyRequest SameAsApproved() => new()
    {
        Name = "db",
        Enabled = true,
        DbEngine = BackupDbEngine.Postgres,
        DbHost = "localhost",
        DbPort = 5432,
        DbName = "app",
        DbUser = "app",
        DbPassword = null,
        FilePaths = [],
        ScheduleCron = "0 3 * * *",
        RetentionCount = 7
    };

    [Fact]
    public async Task Create_InvalidCron_Throws()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateAsync(new CreateBackupPolicyRequest
        {
            Name = "x",
            ProjectId = 1,
            ServerId = 2,
            ScheduleCron = "not a cron"
        }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ApplyRestoreCheckResult_Verified_FlipsToVerified()
    {
        var run = new BackupRun { Id = 9, ServerId = 5, RestoreCheckStatus = RestoreCheckStatus.Unverified };
        _repo.FindRunAsync(9, Arg.Any<CancellationToken>()).Returns(run);

        var ok = await _sut.ApplyRestoreCheckResultAsync(9, agentServerId: 5, new RestoreCheckResultDto { Verified = true }, ct: TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.Equal(RestoreCheckStatus.Verified, run.RestoreCheckStatus);
    }

    [Fact]
    public async Task ApplyRestoreCheckResult_Failed_FlipsToFailed_NotOk()
    {
        var run = new BackupRun { Id = 9, ServerId = 5, RestoreCheckStatus = RestoreCheckStatus.Unverified };
        _repo.FindRunAsync(9, Arg.Any<CancellationToken>()).Returns(run);

        await _sut.ApplyRestoreCheckResultAsync(9, 5, new RestoreCheckResultDto { Verified = false, Message = "schema mismatch" }, ct: TestContext.Current.CancellationToken);

        // No-fake: a failed restore-check is Failed (visible), never silently verified/ok.
        Assert.Equal(RestoreCheckStatus.Failed, run.RestoreCheckStatus);
    }

    [Fact]
    public async Task ApplyRestoreCheckResult_WrongServer_Refused_NoChange()
    {
        var run = new BackupRun { Id = 9, ServerId = 5, RestoreCheckStatus = RestoreCheckStatus.Unverified };
        _repo.FindRunAsync(9, Arg.Any<CancellationToken>()).Returns(run);

        var ok = await _sut.ApplyRestoreCheckResultAsync(9, agentServerId: 6, new RestoreCheckResultDto { Verified = true }, ct: TestContext.Current.CancellationToken);

        Assert.False(ok); // IDOR guard
        Assert.Equal(RestoreCheckStatus.Unverified, run.RestoreCheckStatus);
    }

    [Fact]
    public async Task ApplyBackupResult_Success_SetsSucceededAndMetadata()
    {
        var run = new BackupRun { Id = 3, ServerId = 5, BackupPolicyId = 7, Status = BackupRunStatus.Running };
        _repo.FindRunAsync(3, Arg.Any<CancellationToken>()).Returns(run);
        _repo.FindPolicyAsync(7, Arg.Any<CancellationToken>())
            .Returns(new BackupPolicy { Id = 7, ProjectId = 2 });

        await _sut.ApplyBackupResultAsync(3, 5, new BackupExecuteResultDto { Success = true, ArchivePath = "/b/3.dump", SizeBytes = 1024, Sha256 = "ABC" }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        Assert.Equal("/b/3.dump", run.ArchivePath);
        Assert.Equal(1024, run.SizeBytes);
        Assert.NotNull(run.CompletedAt);
        await _notifier.Received(1).BroadcastOperationalAsync(
            ResourceType.Project,
            2,
            OperationalRealtimeEvents.BackupChanged,
            Arg.Any<CancellationToken>(),
            4);
    }

    [Fact]
    public async Task TriggerBackup_CreatesRunningRun_DispatchesTask_SetsLastRun()
    {
        var policy = new BackupPolicy { Id = 1, ProjectId = 3, ServerId = 2, Name = "toto", ScheduleCron = "0 3 * * *", RetentionCount = 5 };

        var runId = await _sut.TriggerBackupAsync(policy, ct: TestContext.Current.CancellationToken);

        _repo.Received().AddRun(Arg.Is<BackupRun>(r => r.Status == BackupRunStatus.Running && r.ServerId == 2));
        await _serverRepo.Received(1).AddTaskAsync(Arg.Is<ServerTask>(t => t.Operation == OperationKind.BackupExecute), Arg.Any<CancellationToken>());
        Assert.Equal(_time.GetUtcNow().UtcDateTime, policy.LastRunAt);
        await _notifier.Received(1).BroadcastOperationalAsync(
            ResourceType.Project,
            3,
            OperationalRealtimeEvents.BackupChanged,
            Arg.Any<CancellationToken>(),
            4);
    }
}
