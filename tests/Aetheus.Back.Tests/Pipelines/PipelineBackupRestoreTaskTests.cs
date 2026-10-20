// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AppBackups;
using Aetheus.Back.Components.Artifacts;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>A restore-backup step only hands the agent a backup that succeeded, was verified, sits on
/// the step's server and belongs to the run's project; the database credentials travel encrypted.</summary>
public sealed class PipelineBackupRestoreTaskTests
{
    private const int RunId = 30;
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly IBackupRepository _backups = Substitute.For<IBackupRepository>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly Server _server = new() { Id = 4, Name = "db-host" };
    private readonly PipelineStepRun _step = new() { Id = 9, StepName = "restore" };
    private ServerTask? _tracked;
    private readonly PipelineArtifactTaskFactory _sut;

    public PipelineBackupRestoreTaskTests()
    {
        var pipeline = new Pipeline { Id = 2, Name = "restore", ProjectId = 7 };
        _repo.GetPipelineRunWithPipelineAsync(RunId, Arg.Any<CancellationToken>()).Returns(new PipelineRun { Id = RunId, Pipeline = pipeline });
        _repo.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(7);
        _repo.When(repo => repo.TrackTask(Arg.Any<ServerTask>())).Do(call => _tracked = call.Arg<ServerTask>());
        _encryption.EncryptValue(Arg.Any<string>()).Returns(call => "enc:" + call.Arg<string>());
        _encryption.DecryptValue("pw-cipher").Returns("s3cr3t");
        _sut = new PipelineArtifactTaskFactory(_repo, Substitute.For<IArtifactRepository>(), _encryption, new FakeTimeProvider(), _backups);
    }

    private static BackupRun Backup(int serverId = 4, int projectId = 7, BackupRunStatus status = BackupRunStatus.Succeeded,
        RestoreCheckStatus check = RestoreCheckStatus.Verified, string? archive = "/var/backups/55.tar.zst") => new()
        {
            Id = 55,
            ServerId = serverId,
            Status = status,
            RestoreCheckStatus = check,
            ArchivePath = archive,
            BackupPolicy = new BackupPolicy
            {
                Id = 3,
                ProjectId = projectId,
                DbEngine = (BackupDbEngine)1,
                DbHost = "localhost",
                DbPort = 5432,
                DbName = "shop",
                DbUser = "shop",
                DbPasswordEncrypted = "pw-cipher",
            },
        };

    private Task CreateAsync(string selector) =>
        _sut.CreateBackupRestoreTaskAsync(RunId, _server, _step,
            new PipelineStepDefinition { Name = "restore", BackupRun = selector, TimeoutSeconds = 900 },
            new Dictionary<string, string> { ["BACKUP_ID"] = "55" }, TestContext.Current.CancellationToken);

    [Fact]
    public async Task AVerifiedBackupOfTheProject_IsRestoredWithItsCredentials()
    {
        _backups.FindRunWithPolicyAsync(55, Arg.Any<CancellationToken>()).Returns(Backup());

        await CreateAsync("$(BACKUP_ID)");

        Assert.NotNull(_tracked);
        Assert.Equal(OperationKind.BackupRestore, _tracked.Operation);
        Assert.Equal("55", _tracked.Command);
        Assert.Equal(4, _tracked.ServerId);
        Assert.Equal(900, _tracked.TimeoutSeconds);
        Assert.Equal(TaskExecutionStatus.Assigned, _step.Status);
        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(_tracked.EnvironmentVariables!["enc:".Length..])!;
        Assert.Equal("s3cr3t", env[BackupConstants.DbPasswordEnvVar]);
        Assert.Equal("/var/backups/55.tar.zst", env[BackupConstants.ArchivePathEnvVar]);
        Assert.Equal("localhost", env[BackupConstants.DbHostEnvVar]);
        Assert.Equal("5432", env[BackupConstants.DbPortEnvVar]);
        Assert.Equal("55", env["BACKUP_ID"]);
    }

    [Fact]
    public async Task APolicyWithoutDatabaseDetails_SendsOnlyTheArchive()
    {
        var backup = Backup();
        backup.BackupPolicy = new BackupPolicy { Id = 3, ProjectId = 7 };
        _backups.FindRunWithPolicyAsync(55, Arg.Any<CancellationToken>()).Returns(backup);

        await CreateAsync("55");

        var env = JsonSerializer.Deserialize<Dictionary<string, string>>(_tracked!.EnvironmentVariables!["enc:".Length..])!;
        Assert.False(env.ContainsKey(BackupConstants.DbPasswordEnvVar));
        Assert.False(env.ContainsKey(BackupConstants.DbHostEnvVar));
        Assert.False(env.ContainsKey(BackupConstants.DbPortEnvVar));
    }

    [Fact]
    public async Task ASelectorThatIsNotARunId_FailsTheStep()
    {
        await CreateAsync("latest");

        AssertFailed("backup_run must resolve to a verified backup run id.");
    }

    public static TheoryData<string> UnusableBackups() => ["missing", "other-server", "failed", "unverified", "no-archive"];

    [Theory]
    [MemberData(nameof(UnusableBackups))]
    public async Task AnUnusableBackup_FailsTheStep(string defect)
    {
        var backup = defect switch
        {
            "other-server" => Backup(serverId: 5),
            "failed" => Backup(status: BackupRunStatus.Failed),
            "unverified" => Backup(check: RestoreCheckStatus.Unverified),
            "no-archive" => Backup(archive: " "),
            _ => null,
        };
        _backups.FindRunWithPolicyAsync(55, Arg.Any<CancellationToken>()).Returns(backup);

        await CreateAsync("55");

        AssertFailed("the selected backup is not successful, verified, or owned by this target server.");
    }

    [Fact]
    public async Task ABackupOfAnotherProject_FailsTheStep()
    {
        _backups.FindRunWithPolicyAsync(55, Arg.Any<CancellationToken>()).Returns(Backup(projectId: 8));

        await CreateAsync("55");

        AssertFailed("the selected backup belongs to a different project.");
    }

    private void AssertFailed(string reason)
    {
        Assert.Null(_tracked);
        Assert.Equal(TaskExecutionStatus.Failed, _step.Status);
        Assert.Equal(reason, _step.FailureReason);
        Assert.Equal(4, _step.ServerId);
        _encryption.DidNotReceiveWithAnyArgs().DecryptValue(default!);
    }
}
