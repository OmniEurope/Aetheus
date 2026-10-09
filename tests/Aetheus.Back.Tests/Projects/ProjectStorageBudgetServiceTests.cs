// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.AppMonitoring;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Projects;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Projects;

/// <summary>PLAN-013 lot 5: the usage of a project against its budget, the 80 % alert, the oldest-first purge.</summary>
public sealed class ProjectStorageBudgetServiceTests
{
    private const long Megabyte = 1024 * 1024;
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private readonly IProjectStorageRepository _repository = Substitute.For<IProjectStorageRepository>();
    private readonly IAnalysisDetailRetentionRepository _analysis = Substitute.For<IAnalysisDetailRetentionRepository>();
    private readonly IAppTelemetryService _telemetry = Substitute.For<IAppTelemetryService>();
    private readonly INotificationService _notifications = Substitute.For<INotificationService>();

    public ProjectStorageBudgetServiceTests()
    {
        _repository.DatabaseName.Returns("aetheus");
        _repository.GetAppsMeasuringADatabaseAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([]);
    }

    private ProjectStorageBudgetService Service() => new(
        _repository, _analysis, _telemetry, _notifications, new FakeTimeProvider(new DateTimeOffset(Now)),
        NullLogger<ProjectStorageBudgetService>.Instance);

    private void Project(long budgetMegabytes, long sampleRows, int alertLevel = 0) =>
        Setup(budgetMegabytes, alertLevel, new ProjectTableRows(1, ProjectStorageTables.MetricSamples, sampleRows));

    private void Setup(long budgetMegabytes, int alertLevel, params ProjectTableRows[] rows)
    {
        _repository.GetBudgetsAsync(Arg.Any<CancellationToken>())
            .Returns([new ProjectStorageBudget(1, "Atlas", budgetMegabytes * Megabyte, alertLevel)]);
        _repository.CountRowsAsync(Arg.Any<CancellationToken>()).Returns(rows);
    }

    [Fact]
    public void Usage_IsRowsTimesBytesPerRow_PerTable()
    {
        var usages = ProjectStorageUsage.Build(
            [new ProjectStorageBudget(1, "Atlas", 200 * Megabyte, 0)],
            [new ProjectTableRows(1, ProjectStorageTables.MetricSamples, 1_000), new ProjectTableRows(1, ProjectStorageTables.AnalysisMetrics, 2_000)],
            ProjectStorageTables.BytesPerRow(null));

        var usage = Assert.Single(usages);
        Assert.Equal(1_000 * 480 + 2_000 * 300, usage.UsedBytes);
        Assert.Equal(ProjectStorageTables.AnalysisMetrics, usage.Tables[0].Table);
    }

    [Fact]
    public void BytesPerRow_ComesFromTheDatabaseMeasurementWhenItNamesTheTable()
    {
        var bytes = ProjectStorageTables.BytesPerRow(new AppDatabaseSizeReportDto
        {
            Tables = [new AppDatabaseTableSizeDto { Name = ProjectStorageTables.AnalysisMetrics, TotalBytes = 1_000_000, EstimatedRows = 2_000 }]
        });

        Assert.Equal(500, bytes[ProjectStorageTables.AnalysisMetrics]);
        Assert.Equal(480, bytes[ProjectStorageTables.MetricSamples]);
    }

    [Fact]
    public void ATableBloatedByPurges_CountsAtMostTwiceTheFallbackPerRow()
    {
        // 3 GB on disk for 100,000 remaining rows: 32 KB a row, until the space is reclaimed.
        var bytes = ProjectStorageTables.BytesPerRow(new AppDatabaseSizeReportDto
        {
            Tables = [new AppDatabaseTableSizeDto { Name = ProjectStorageTables.MetricSamples, TotalBytes = 3_200_000_000, EstimatedRows = 100_000 }]
        });

        Assert.Equal(960, bytes[ProjectStorageTables.MetricSamples]);
    }

    [Fact]
    public async Task TheOwnDatabaseMeasurement_IsTheOneNamedLikeTheAetheusDatabase()
    {
        _repository.GetAppsMeasuringADatabaseAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([3, 4]);
        _telemetry.GetDatabaseSizeAsync(3, Arg.Any<CancellationToken>())
            .Returns(new AppDatabaseSizeReportDto { DatabaseName = "atlas", MeasuredAt = Now });
        _telemetry.GetDatabaseSizeAsync(4, Arg.Any<CancellationToken>())
            .Returns(new AppDatabaseSizeReportDto { DatabaseName = "aetheus", MeasuredAt = Now.AddHours(-1) });
        Project(200, 10);

        var report = await Service().GetReportAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(Now.AddHours(-1), report.MeasuredAt);
    }

    [Fact]
    public async Task At80Percent_TheProjectIsAlertedOnce()
    {
        // 180,000 samples x 480 bytes = 82 % of 100 MB.
        Project(100, 180_000);

        await Service().EnforceAsync(TestContext.Current.CancellationToken);

        await _notifications.Received(1).SendEventAsync("project.storage.quota", Arg.Any<object>(), Arg.Any<CancellationToken>());
        await _repository.Received(1).SetAlertLevelAsync(1, 80, Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().DeleteOldestSamplesAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());

        _notifications.ClearReceivedCalls();
        Project(100, 180_000, alertLevel: 80);
        await Service().EnforceAsync(TestContext.Current.CancellationToken);
        await _notifications.DidNotReceive().SendEventAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UnderTheAlert_TheLevelIsReset()
    {
        Project(100, 10, alertLevel: 80);

        await Service().EnforceAsync(TestContext.Current.CancellationToken);

        await _repository.Received(1).SetAlertLevelAsync(1, 0, Arg.Any<CancellationToken>());
        await _notifications.DidNotReceive().SendEventAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverBudget_TheOldestDetailGoesFirst_UntilNinetyPercent()
    {
        // 100 MB budget, 230,000 samples x 480 bytes = 105 MB: 15 MB to free, about 33,000 samples.
        Project(100, 230_000, alertLevel: 100);
        _repository.GetOldestSampleAsync(1, Arg.Any<CancellationToken>()).Returns(Now.AddDays(-6));
        _repository.GetOldestLogAsync(1, Arg.Any<CancellationToken>()).Returns(Now.AddDays(-2));
        _analysis.GetOldestPurgeableReportDateAsync(1, Now, Arg.Any<CancellationToken>()).Returns(Now.AddDays(-3));
        _repository.DeleteOldestSamplesAsync(1, ProjectStorageBudgetService.BatchSize, Arg.Any<CancellationToken>())
            .Returns(ProjectStorageBudgetService.BatchSize);

        var sweep = await Service().EnforceAsync(TestContext.Current.CancellationToken);

        Assert.Equal(1, sweep.ProjectsOverBudget);
        Assert.Equal(7, sweep.Batches); // 7 x 5,000 x 480 bytes = 16 MB >= 15 MB
        await _repository.DidNotReceive().DeleteOldestLogsAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _analysis.DidNotReceive().DeleteMetricDetailBatchAsync(
            Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OverBudget_AnOlderAnalysisReport_LosesItsDetailBeforeNewerTelemetry()
    {
        var order = new List<string>();
        Project(10, 30_000, alertLevel: 100);
        _repository.GetOldestSampleAsync(1, Arg.Any<CancellationToken>()).Returns(Now.AddDays(-1));
        _analysis.GetOldestPurgeableReportDateAsync(1, Now, Arg.Any<CancellationToken>()).Returns(Now.AddDays(-20), (DateTime?)null);
        _analysis.GetReportsWithExpiredDetailAsync(Now, Arg.Any<IReadOnlyCollection<int>>(), 1, Arg.Any<CancellationToken>()).Returns([42]);
        _analysis.DeleteMetricDetailBatchAsync(Arg.Any<IReadOnlyCollection<int>>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(3).AndDoes(_ => order.Add("analysis"));
        _repository.DeleteOldestSamplesAsync(1, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(ProjectStorageBudgetService.BatchSize).AndDoes(_ => order.Add("samples"));

        await Service().EnforceAsync(TestContext.Current.CancellationToken);

        Assert.Equal("analysis", order[0]);
        Assert.All(order.Skip(1), step => Assert.Equal("samples", step));
        await _analysis.Received(1).DeleteMetricDetailBatchAsync(
            Arg.Is<IReadOnlyCollection<int>>(ids => ids.Single() == 42), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task NothingLeftToPurge_EndsTheSweep()
    {
        Project(10, 30_000, alertLevel: 100);

        var sweep = await Service().EnforceAsync(TestContext.Current.CancellationToken);

        Assert.Equal((1, 0), (sweep.ProjectsOverBudget, sweep.Batches));
    }

    [Fact]
    public async Task TheReport_ListsOnlyTheProjectsTheCallerCanRead()
    {
        _repository.GetBudgetsAsync(Arg.Any<CancellationToken>()).Returns(
            [new ProjectStorageBudget(1, "Atlas", 200 * Megabyte, 0), new ProjectStorageBudget(2, "Hidden", 200 * Megabyte, 0)]);
        _repository.CountRowsAsync(Arg.Any<CancellationToken>()).Returns([]);

        var report = await Service().GetReportAsync([1], TestContext.Current.CancellationToken);

        Assert.Equal("Atlas", Assert.Single(report.Projects).ProjectName);
        Assert.Null(report.MeasuredAt);
    }
}
