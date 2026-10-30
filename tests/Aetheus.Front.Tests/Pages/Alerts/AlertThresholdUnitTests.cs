// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Alerts;
using Bunit;

namespace Aetheus.Front.Tests.Pages.AlertsDeep;

/// <summary>
/// SITE-FUNC-ALERT-UNIT-001: free disk space and build cache thresholds are compared in GiB by the backend
/// evaluator; the list, its export and the edit dialog showed them as percentages.
/// </summary>
public sealed class AlertThresholdUnitTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public AlertThresholdUnitTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Theory]
    [InlineData(MetricType.Cpu, "%")]
    [InlineData(MetricType.Memory, "%")]
    [InlineData(MetricType.Disk, "%")]
    [InlineData(MetricType.DiskFree, "Go")]
    [InlineData(MetricType.BuildCache, "Go")]
    public void EachMetric_HasTheUnitTheEvaluatorComparesIn(MetricType metric, string unit) =>
        Assert.Equal(unit, AlertThresholdUnit.Of(metric));

    [Fact]
    public void AlertList_ShowsGo_ForAFreeDiskSpaceThreshold_AndPercent_ForCpu()
    {
        _handler.SetJsonResponse("api/alerts", new List<AlertRuleDto>
        {
            new() { Id = 1, Name = "Free disk", Metric = MetricType.DiskFree, Operator = ComparisonOperator.LessThan, Threshold = 20, SustainedSeconds = 60, IsEnabled = true, CreatedAt = DateTime.UtcNow },
            new() { Id = 2, Name = "Hot CPU", Metric = MetricType.Cpu, Operator = ComparisonOperator.GreaterThan, Threshold = 90, SustainedSeconds = 60, IsEnabled = true, CreatedAt = DateTime.UtcNow }
        });

        var cut = Render<Aetheus.Front.Components.Alerts.Alerts>();

        cut.WaitForAssertion(() =>
        {
            var disk = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Free disk", StringComparison.Ordinal));
            var cpu = cut.FindAll("tbody tr").Single(r => r.TextContent.Contains("Hot CPU", StringComparison.Ordinal));
            // R-113: the comparison reads as a sign, never as the enum name ("LessThan").
            Assert.Contains("< 20 Go", disk.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("20%", disk.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("LessThan", disk.TextContent, StringComparison.Ordinal);
            Assert.Contains("> 90%", cpu.TextContent, StringComparison.Ordinal);
            Assert.DoesNotContain("GreaterThan", cpu.TextContent, StringComparison.Ordinal);
        });
    }

    [Fact]
    public void EditDialog_LabelsTheThresholdWithTheMetricsUnit()
    {
        var alert = new AlertRuleDto { Id = 3, Name = "Cache", Metric = MetricType.BuildCache, Operator = ComparisonOperator.GreaterThan, Threshold = 50, SustainedSeconds = 60 };

        var cut = Render<AlertEditDialog>(p => p.Add(x => x.Alert, alert));

        Assert.Contains("Threshold (Go)", cut.Find("label[for=oe-pages-alerts-alerteditdialog-numeric-1]").TextContent, StringComparison.Ordinal);
    }
}
