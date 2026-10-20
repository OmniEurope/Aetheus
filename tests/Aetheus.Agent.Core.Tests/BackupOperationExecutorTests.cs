// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;
using Aetheus.Agent.Core.Operations;
using Aetheus.Agent.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Agent.Core.Tests;

public class BackupOperationExecutorTests
{
    [Fact]
    public async Task BackupExecute_FilesOnly_CreatesArchiveAndReportsSuccess()
    {
        using var fixture = new BackupExecutorFixture();
        var source = fixture.CreateSource("before");
        var environment = fixture.BackupEnvironment(source, runId: 101, policyId: 7);

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupExecute,
            environment,
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        var archive = Path.Combine(fixture.BackupRoot, "7", "backup-101.aetheus-backup");
        Assert.True(File.Exists(archive));
        await fixture.Api.Received(1).ReportBackupResultAsync(
            101,
            Arg.Is<BackupExecuteResultDto>(report =>
                report.Success
                && report.ArchivePath == archive
                && report.SizeBytes > 0
                && (report.Sha256 ?? string.Empty).Length == 64),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreCheck_FilesOnly_VerifiesArchiveAndReportsResult()
    {
        using var fixture = new BackupExecutorFixture();
        var archive = await fixture.CreateFilesOnlyArchiveAsync("verified");

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupRestoreCheck,
            fixture.RestoreEnvironment(archive, runId: 202),
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        await fixture.Api.Received(1).ReportRestoreCheckResultAsync(
            202,
            Arg.Is<RestoreCheckResultDto>(report => report.Verified),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackupRestore_FilesOnly_RestoresSnapshot()
    {
        using var fixture = new BackupExecutorFixture();
        var source = fixture.CreateSource("snapshot");
        var archive = await fixture.CreateArchiveFromSourceAsync(source);
        await File.WriteAllTextAsync(
            Path.Combine(source, "value.txt"),
            "mutated",
            TestContext.Current.CancellationToken);

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupRestore,
            fixture.RestoreEnvironment(archive, runId: 303),
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(
            "snapshot",
            await File.ReadAllTextAsync(
                Path.Combine(source, "value.txt"),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task BackupRestore_DatabaseAndFiles_FileFailureCompensatesDatabaseAndEveryFileSource()
    {
        using var fixture = new BackupExecutorFixture();
        var first = fixture.CreateSource("snapshot-first");
        var second = fixture.CreateSource("snapshot-second");
        var databaseDump = Path.Combine(fixture.Root, "database.dump");
        await File.WriteAllTextAsync(databaseDump, "desired-database", TestContext.Current.CancellationToken);
        var archive = Path.Combine(fixture.Root, "combined.aetheus-backup");
        await BackupArchiveBundle.CreateAsync(
            archive, databaseDump, [first, second], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(first, "value.txt"), "current-first", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(second, "value.txt"), "current-second", TestContext.Current.CancellationToken);
        var commands = new List<(string FileName, IReadOnlyList<string> Argv)>();
        fixture.Executor.ProcessInvoker = (fileName, argv, _, _, _, _, _) =>
        {
            commands.Add((fileName, argv));
            if (fileName == "pg_dump")
            {
                var output = argv[Array.IndexOf(argv.ToArray(), "-f") + 1];
                File.WriteAllText(output, "pre-restore-database");
            }
            return Task.FromResult(new ExecutorResult(0, false));
        };
        fixture.Executor.FileRestoreBoundaryHook = (boundary, index) =>
        {
            if (boundary == BackupFileRestoreBoundary.SourceSwapped && index == 1)
                throw new IOException("Injected late file failure.");
        };

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupRestore,
            fixture.DatabaseRestoreEnvironment(archive),
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Equal(["pg_dump", "pg_restore", "pg_restore"], commands.Select(command => command.FileName).ToArray());
        Assert.Equal("current-first", await File.ReadAllTextAsync(
            Path.Combine(first, "value.txt"), TestContext.Current.CancellationToken));
        Assert.Equal("current-second", await File.ReadAllTextAsync(
            Path.Combine(second, "value.txt"), TestContext.Current.CancellationToken));
        Assert.Contains(fixture.Output, line => line == "Database compensation completed.");
        Assert.Empty(Directory.EnumerateFiles(fixture.BackupRoot, "database-*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task BackupRestore_DatabaseMutationFailureCompensatesBeforeFilesChange()
    {
        using var fixture = new BackupExecutorFixture();
        var source = fixture.CreateSource("snapshot");
        var databaseDump = Path.Combine(fixture.Root, "database.dump");
        await File.WriteAllTextAsync(databaseDump, "desired-database", TestContext.Current.CancellationToken);
        var archive = Path.Combine(fixture.Root, "combined.aetheus-backup");
        await BackupArchiveBundle.CreateAsync(
            archive, databaseDump, [source], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "value.txt"), "current", TestContext.Current.CancellationToken);
        var restoreCalls = 0;
        fixture.Executor.ProcessInvoker = (fileName, argv, _, _, _, _, _) =>
        {
            if (fileName == "pg_dump")
            {
                var output = argv[Array.IndexOf(argv.ToArray(), "-f") + 1];
                File.WriteAllText(output, "pre-restore-database");
                return Task.FromResult(new ExecutorResult(0, false));
            }
            restoreCalls++;
            return Task.FromResult(new ExecutorResult(restoreCalls == 1 ? 17 : 0, false));
        };

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupRestore,
            fixture.DatabaseRestoreEnvironment(archive),
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(17, result.ExitCode);
        Assert.Equal(2, restoreCalls);
        Assert.Equal("current", await File.ReadAllTextAsync(
            Path.Combine(source, "value.txt"), TestContext.Current.CancellationToken));
        Assert.Contains(fixture.Output, line => line == "Database compensation completed.");
    }

    [Fact]
    public async Task BackupRestore_DatabaseSnapshotFailureRefusesBeforeAnyMutation()
    {
        using var fixture = new BackupExecutorFixture();
        var source = fixture.CreateSource("snapshot");
        var databaseDump = Path.Combine(fixture.Root, "database.dump");
        await File.WriteAllTextAsync(databaseDump, "desired-database", TestContext.Current.CancellationToken);
        var archive = Path.Combine(fixture.Root, "combined.aetheus-backup");
        await BackupArchiveBundle.CreateAsync(
            archive, databaseDump, [source], TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(source, "value.txt"), "current", TestContext.Current.CancellationToken);
        var commands = new List<string>();
        fixture.Executor.ProcessInvoker = (fileName, _, _, _, _, _, _) =>
        {
            commands.Add(fileName);
            return Task.FromResult(new ExecutorResult(23, false));
        };

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupRestore,
            fixture.DatabaseRestoreEnvironment(archive),
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(23, result.ExitCode);
        Assert.Equal(["pg_dump"], commands);
        Assert.Equal("current", await File.ReadAllTextAsync(
            Path.Combine(source, "value.txt"), TestContext.Current.CancellationToken));
        Assert.Contains(fixture.Output, line => line == "Restore refused: current database snapshot failed.");
    }

    [Fact]
    public async Task BackupExecute_CancellationPropagatesAndDoesNotReportSuccess()
    {
        using var fixture = new BackupExecutorFixture();
        var source = fixture.CreateSource("before");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            fixture.Executor.ExecuteSupportedPlatformAsync(
                OperationKind.BackupExecute,
                fixture.BackupEnvironment(source, runId: 404, policyId: 9),
                30,
                fixture.CaptureOutput,
                cancellation.Token));

        await fixture.Api.DidNotReceiveWithAnyArgs().ReportBackupResultAsync(
            default,
            default!,
            TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BackupExecute_MissingSourceReportsTerminalFailure()
    {
        using var fixture = new BackupExecutorFixture();
        var missing = Path.Combine(fixture.Root, "missing");

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupExecute,
            fixture.BackupEnvironment(missing, runId: 505, policyId: 11),
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        await fixture.Api.Received(1).ReportBackupResultAsync(
            505,
            Arg.Is<BackupExecuteResultDto>(report => !report.Success),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RestoreCheck_CancelledDuringRestore_StillDropsScratchDatabaseAndExtraction()
    {
        using var fixture = new BackupExecutorFixture();
        var databaseDump = Path.Combine(fixture.Root, "database.dump");
        await File.WriteAllTextAsync(databaseDump, "desired-database", TestContext.Current.CancellationToken);
        var archive = Path.Combine(fixture.Root, "restore-check.aetheus-backup");
        await BackupArchiveBundle.CreateAsync(archive, databaseDump, [], TestContext.Current.CancellationToken);
        using var taskCts = new CancellationTokenSource();
        var commands = new List<(string FileName, bool TokenCancelled)>();
        string? extractedDump = null;
        fixture.Executor.ProcessInvoker = (fileName, argv, _, _, _, token, _) =>
        {
            commands.Add((fileName, token.IsCancellationRequested));
            if (fileName == "pg_restore")
            {
                extractedDump = argv[^1];
                taskCts.Cancel();
                token.ThrowIfCancellationRequested();
            }
            return Task.FromResult(new ExecutorResult(0, false));
        };
        var environment = fixture.DatabaseRestoreEnvironment(archive);
        environment[BackupConstants.RunIdEnvVar] = "707";

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupRestoreCheck, environment, 30, fixture.CaptureOutput, taskCts.Token));

        Assert.Equal(
            [("dropdb", false), ("createdb", false), ("pg_restore", false), ("dropdb", false)],
            commands);
        Assert.NotNull(extractedDump);
        Assert.False(File.Exists(extractedDump));
    }

    [Fact]
    public async Task RestoreCheck_CorruptArchiveReportsTerminalFailure()
    {
        using var fixture = new BackupExecutorFixture();
        var archive = Path.Combine(fixture.Root, "corrupt.aetheus-backup");
        await File.WriteAllTextAsync(archive, "not a zip", TestContext.Current.CancellationToken);

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupRestoreCheck,
            fixture.RestoreEnvironment(archive, runId: 606),
            30,
            fixture.CaptureOutput,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        await fixture.Api.Received(1).ReportRestoreCheckResultAsync(
            606,
            Arg.Is<RestoreCheckResultDto>(report => !report.Verified),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void GetArchivesToPrune_IgnoresPartialDumpAndKeepsExactCompletedCount(int keep)
    {
        var root = Path.Combine(Path.GetTempPath(), $"aetheus-backup-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            for (var index = 0; index < 9; index++)
            {
                var archive = Path.Combine(root, $"backup-{index}.aetheus-backup");
                File.WriteAllText(archive, index.ToString());
                File.SetLastWriteTimeUtc(archive, new DateTime(2024, 1, 1, 0, 0, index, DateTimeKind.Utc));
            }
            var partial = Path.Combine(root, "backup-99.dump.partial");
            File.WriteAllText(partial, "in progress");
            File.SetLastWriteTimeUtc(partial, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            var selected = BackupOperationExecutor.GetArchivesToPrune(root, keep);

            Assert.Equal(9 - keep, selected.Count);
            Assert.DoesNotContain(selected, file => file.FullName == partial);
            Assert.Equal(
                Enumerable.Range(0, 9 - keep).Select(index => $"backup-{index}.aetheus-backup").Order(),
                selected.Select(file => file.Name).Order());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(BackupConstants.RunIdEnvVar)]
    [InlineData(BackupConstants.PolicyIdEnvVar)]
    public async Task BackupExecute_WithoutItsRunOrPolicyId_FailsWithoutReporting(string missing)
    {
        using var fixture = new BackupExecutorFixture();
        var environment = fixture.BackupEnvironment(fixture.CreateSource("data"), runId: 5, policyId: 3);
        environment.Remove(missing);

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupExecute, environment, 30, fixture.CaptureOutput, TestContext.Current.CancellationToken);

        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("Missing backup run/policy id", fixture.Output);
        await fixture.Api.DidNotReceive().ReportBackupResultAsync(
            Arg.Any<int>(), Arg.Any<BackupExecuteResultDto>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackupExecute_WithNothingToBackUp_ReportsTheFailure()
    {
        using var fixture = new BackupExecutorFixture();
        var environment = fixture.BackupEnvironment(fixture.CreateSource("data"), runId: 6, policyId: 3);
        environment[BackupConstants.FilePathsEnvVar] = "[]";

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupExecute, environment, 30, fixture.CaptureOutput, TestContext.Current.CancellationToken);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Backup failed: No database or file paths are configured.", fixture.Output);
        await fixture.Api.Received(1).ReportBackupResultAsync(
            6,
            Arg.Is<BackupExecuteResultDto>(report => !report.Success && report.Message == "No database or file paths are configured."),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackupExecute_MySql_DumpsWithThePasswordInTheEnvironment_BundlesTheDump_AndRemovesThePartialFile()
    {
        using var fixture = new BackupExecutorFixture();
        var environment = fixture.DatabaseBackupEnvironment(BackupDbEngine.MySql, runId: 8, policyId: 4);
        var partial = Path.Combine(fixture.BackupRoot, "4", "backup-8.sql.partial");
        (string FileName, IReadOnlyList<string> Argv, (string Key, string Value)? Environment)? dump = null;
        fixture.Executor.ProcessInvoker = (fileName, argv, extra, _, _, _, _) =>
        {
            dump = (fileName, argv, extra);
            File.WriteAllText(partial, "-- mysql dump");
            return Task.FromResult(new ExecutorResult(0, false));
        };

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupExecute, environment, 30, fixture.CaptureOutput, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(dump);
        Assert.Equal("mysqldump", dump.Value.FileName);
        Assert.Contains("--result-file=" + partial, dump.Value.Argv);
        Assert.Equal(("MYSQL_PWD", "secret"), dump.Value.Environment);
        Assert.False(File.Exists(partial));
        Assert.True(File.Exists(Path.Combine(fixture.BackupRoot, "4", "backup-8.aetheus-backup")));
        await fixture.Api.Received(1).ReportBackupResultAsync(
            8, Arg.Is<BackupExecuteResultDto>(report => report.Success), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task BackupExecute_AFailedPostgresDump_IsReportedWithItsExitCode_AndNoArchiveIsWritten()
    {
        using var fixture = new BackupExecutorFixture();
        var environment = fixture.DatabaseBackupEnvironment(BackupDbEngine.Postgres, runId: 9, policyId: 4);
        var partial = Path.Combine(fixture.BackupRoot, "4", "backup-9.dump.partial");
        fixture.Executor.ProcessInvoker = (fileName, argv, extra, _, _, _, _) =>
        {
            Assert.Equal("pg_dump", fileName);
            Assert.Equal(("PGPASSWORD", "secret"), extra);
            File.WriteAllText(partial, "half a dump");
            return Task.FromResult(new ExecutorResult(3, false));
        };

        var result = await fixture.Executor.ExecuteSupportedPlatformAsync(
            OperationKind.BackupExecute, environment, 30, fixture.CaptureOutput, TestContext.Current.CancellationToken);

        Assert.Equal(3, result.ExitCode);
        Assert.False(File.Exists(partial));
        Assert.False(File.Exists(Path.Combine(fixture.BackupRoot, "4", "backup-9.aetheus-backup")));
        await fixture.Api.Received(1).ReportBackupResultAsync(
            9,
            Arg.Is<BackupExecuteResultDto>(report => !report.Success && report.Message == "Dump failed (exit 3)."),
            Arg.Any<CancellationToken>());
    }

    private sealed class BackupExecutorFixture : IDisposable
    {
        internal BackupExecutorFixture()
        {
            Root = Directory.CreateTempSubdirectory("aetheus-backup-executor-").FullName;
            BackupRoot = Path.Combine(Root, "backups");
            Api = Substitute.For<IServerApiClient>();
            Executor = new BackupOperationExecutor(
                Api,
                NullLogger<BackupOperationExecutor>.Instance)
            {
                BackupBaseDirectory = BackupRoot
            };
        }

        internal string Root { get; }
        internal string BackupRoot { get; }
        internal IServerApiClient Api { get; }
        internal BackupOperationExecutor Executor { get; }
        internal List<string> Output { get; } = [];

        internal Task CaptureOutput(string message, TaskLogLevel _)
        {
            Output.Add(message);
            return Task.CompletedTask;
        }

        internal string CreateSource(string content)
        {
            var source = Directory.CreateDirectory(
                Path.Combine(Root, $"source-{Guid.NewGuid():N}")).FullName;
            File.WriteAllText(Path.Combine(source, "value.txt"), content);
            return source;
        }

        internal async Task<string> CreateFilesOnlyArchiveAsync(string content) =>
            await CreateArchiveFromSourceAsync(CreateSource(content));

        internal async Task<string> CreateArchiveFromSourceAsync(string source)
        {
            var archive = Path.Combine(Root, $"archive-{Guid.NewGuid():N}.aetheus-backup");
            await BackupArchiveBundle.CreateAsync(
                archive,
                databaseDumpPath: null,
                [source],
                TestContext.Current.CancellationToken);
            return archive;
        }

        internal Dictionary<string, string> BackupEnvironment(
            string source,
            int runId,
            int policyId) => new()
            {
                [BackupConstants.EngineEnvVar] = BackupDbEngine.None.ToString(),
                [BackupConstants.RunIdEnvVar] = runId.ToString(),
                [BackupConstants.PolicyIdEnvVar] = policyId.ToString(),
                [BackupConstants.FilePathsEnvVar] = System.Text.Json.JsonSerializer.Serialize(new[] { source }),
                [BackupConstants.RetentionEnvVar] = "2"
            };

        internal Dictionary<string, string> RestoreEnvironment(string archive, int runId) => new()
        {
            [BackupConstants.EngineEnvVar] = BackupDbEngine.None.ToString(),
            [BackupConstants.RunIdEnvVar] = runId.ToString(),
            [BackupConstants.ArchivePathEnvVar] = archive
        };

        internal Dictionary<string, string> DatabaseBackupEnvironment(BackupDbEngine engine, int runId, int policyId) => new()
        {
            [BackupConstants.EngineEnvVar] = engine.ToString(),
            [BackupConstants.RunIdEnvVar] = runId.ToString(),
            [BackupConstants.PolicyIdEnvVar] = policyId.ToString(),
            [BackupConstants.DbHostEnvVar] = "db.local",
            [BackupConstants.DbPortEnvVar] = "3306",
            [BackupConstants.DbNameEnvVar] = "shop",
            [BackupConstants.DbUserEnvVar] = "backup-user",
            [BackupConstants.DbPasswordEnvVar] = "secret"
        };

        internal Dictionary<string, string> DatabaseRestoreEnvironment(string archive) => new()
        {
            [BackupConstants.EngineEnvVar] = BackupDbEngine.Postgres.ToString(),
            [BackupConstants.ArchivePathEnvVar] = archive,
            [BackupConstants.DbHostEnvVar] = "db.local",
            [BackupConstants.DbPortEnvVar] = "5432",
            [BackupConstants.DbNameEnvVar] = "aetheus",
            [BackupConstants.DbUserEnvVar] = "restore-user",
            [BackupConstants.DbPasswordEnvVar] = "secret"
        };

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
