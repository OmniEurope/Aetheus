// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Alerts;

public class AlertEvaluatorIsThresholdBreachedTests
{
    private static AlertRule MakeRule(MetricType metric, ComparisonOperator op, double threshold) => new()
    {
        Id = 1,
        Name = "Test Rule",
        Metric = metric,
        Operator = op,
        Threshold = threshold,
        SustainedSeconds = 60
    };

    private static ServerMetric MakeMetric(double cpu = 0, double memUsed = 0, double memTotal = 0, double diskUsed = 0, double diskTotal = 0) => new()
    {
        CpuPercent = cpu,
        MemoryUsedMb = memUsed,
        MemoryTotalMb = memTotal,
        DiskUsedGb = diskUsed,
        DiskTotalGb = diskTotal,
        Timestamp = DateTime.UtcNow
    };

    [Fact]
    public void Cpu_GreaterThan_Breached()
    {
        var rule = MakeRule(MetricType.Cpu, ComparisonOperator.GreaterThan, 80);
        var metric = MakeMetric(cpu: 90);
        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void Cpu_GreaterThan_NotBreached()
    {
        var rule = MakeRule(MetricType.Cpu, ComparisonOperator.GreaterThan, 80);
        var metric = MakeMetric(cpu: 70);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void Cpu_LessThan_Breached()
    {
        var rule = MakeRule(MetricType.Cpu, ComparisonOperator.LessThan, 20);
        var metric = MakeMetric(cpu: 10);
        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void Cpu_LessThan_NotBreached()
    {
        var rule = MakeRule(MetricType.Cpu, ComparisonOperator.LessThan, 20);
        var metric = MakeMetric(cpu: 30);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void Cpu_GreaterThanOrEqual_AtThreshold()
    {
        var rule = MakeRule(MetricType.Cpu, ComparisonOperator.GreaterThanOrEqual, 80);
        var metric = MakeMetric(cpu: 80);
        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void Cpu_LessThanOrEqual_AtThreshold()
    {
        var rule = MakeRule(MetricType.Cpu, ComparisonOperator.LessThanOrEqual, 20);
        var metric = MakeMetric(cpu: 20);
        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void Memory_GreaterThan_CalculatesPercentage()
    {
        var rule = MakeRule(MetricType.Memory, ComparisonOperator.GreaterThan, 50);
        var metric = MakeMetric(memUsed: 8000, memTotal: 16000);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));

        var metricHigh = MakeMetric(memUsed: 12000, memTotal: 16000);
        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metricHigh));
    }

    [Fact]
    public void Memory_ZeroTotal_DoesNotTrigger()
    {
        var rule = MakeRule(MetricType.Memory, ComparisonOperator.GreaterThan, 0);
        var metric = MakeMetric(memUsed: 100, memTotal: 0);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void Disk_GreaterThan_CalculatesPercentage()
    {
        var rule = MakeRule(MetricType.Disk, ComparisonOperator.GreaterThan, 90);
        var metric = MakeMetric(diskUsed: 450, diskTotal: 500);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));

        var metricHigh = MakeMetric(diskUsed: 480, diskTotal: 500);
        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metricHigh));
    }

    [Fact]
    public void Disk_ZeroTotal_DoesNotTrigger()
    {
        var rule = MakeRule(MetricType.Disk, ComparisonOperator.GreaterThan, 0);
        var metric = MakeMetric(diskUsed: 100, diskTotal: 0);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void DiskFree_MissingDiskMeasurement_DoesNotTriggerLowSpaceAlert()
    {
        var rule = MakeRule(MetricType.DiskFree, ComparisonOperator.LessThan, 20);

        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, MakeMetric()));
    }

    [Fact]
    public void DiskFree_LessThan_UsesAbsoluteFreeGiB()
    {
        var rule = MakeRule(MetricType.DiskFree, ComparisonOperator.LessThan, 20);

        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, MakeMetric(diskUsed: 385, diskTotal: 400)));
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, MakeMetric(diskUsed: 375, diskTotal: 400)));
    }

    [Fact]
    public void BuildCache_GreaterThan_ConvertsBytesToGiB()
    {
        var rule = MakeRule(MetricType.BuildCache, ComparisonOperator.GreaterThan, 80);
        var metric = MakeMetric();
        metric.BuildCacheAvailable = true;
        metric.BuildCacheBytes = 81L * 1024 * 1024 * 1024;

        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void BuildCache_UnavailableMeasurement_DoesNotTriggerThreshold()
    {
        var rule = MakeRule(MetricType.BuildCache, ComparisonOperator.LessThan, 1);

        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, MakeMetric()));
    }

    [Fact]
    public void Inodes_GreaterThanOrEqual_UsesTheStoredPercent()
    {
        var rule = MakeRule(MetricType.Inodes, ComparisonOperator.GreaterThanOrEqual, 90);
        var full = MakeMetric();
        full.InodeUsedPercent = 90;
        var roomy = MakeMetric();
        roomy.InodeUsedPercent = 89.9;

        Assert.True(AlertEvaluatorService.IsThresholdBreached(rule, full));
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, roomy));
    }

    [Fact]
    public void Inodes_UnknownMeasurement_NeverTriggers()
    {
        // A "below" rule on an unmeasured table would fire on a reading of 0 if unknown became 0.
        var rule = MakeRule(MetricType.Inodes, ComparisonOperator.LessThan, 100);

        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, MakeMetric()));
    }

    [Fact]
    public void UnknownMetric_ReturnsZero()
    {
        var rule = MakeRule((MetricType)999, ComparisonOperator.GreaterThan, 0);
        var metric = MakeMetric(cpu: 100);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }

    [Fact]
    public void UnknownOperator_ReturnsFalse()
    {
        var rule = MakeRule(MetricType.Cpu, (ComparisonOperator)999, 0);
        var metric = MakeMetric(cpu: 100);
        Assert.False(AlertEvaluatorService.IsThresholdBreached(rule, metric));
    }
}
