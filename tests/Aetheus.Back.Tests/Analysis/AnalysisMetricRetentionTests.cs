// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>Which metrics of a report reach <c>AnalysisMetrics</c>.</summary>
public sealed class AnalysisMetricRetentionTests
{
    private static readonly HashSet<AnalysisMetric> None = new(ReferenceEqualityComparer.Instance);

    [Fact]
    public void Select_KeepsEveryProjectTotal()
    {
        var totals = Enumerable.Range(0, 300)
            .Select(index => Metric($"total.{index}", index, AnalysisMetricDirection.LowerIsBetter))
            .ToList();

        Assert.Equal(300, AnalysisMetricRetention.Select(totals, None).Count);
    }

    [Fact]
    public void Select_KeepsTheFiftyHighestOfALowerIsBetterKey()
    {
        var metrics = Files("complexity.file", 1_000, AnalysisMetricDirection.LowerIsBetter);

        var kept = AnalysisMetricRetention.Select(metrics, None);

        // The 50 worst (951..1000) plus the lowest value, kept as an extreme.
        Assert.Equal(51, kept.Count);
        Assert.All(Enumerable.Range(951, 50), value => Assert.Contains(kept, metric => metric.Value == value));
        Assert.Contains(kept, metric => metric.Value == 1);
    }

    [Fact]
    public void Select_KeepsTheFiftyLowestOfAHigherIsBetterKey()
    {
        var metrics = Files("coverage.file", 1_000, AnalysisMetricDirection.HigherIsBetter);

        var kept = AnalysisMetricRetention.Select(metrics, None);

        Assert.Equal(51, kept.Count);
        Assert.All(Enumerable.Range(1, 50), value => Assert.Contains(kept, metric => metric.Value == value));
        Assert.Contains(kept, metric => metric.Value == 1_000);
    }

    [Fact]
    public void Select_KeepsAFlaggedMetricOutsideTheWorst()
    {
        var metrics = Files("complexity.file", 1_000, AnalysisMetricDirection.LowerIsBetter);
        var flagged = new HashSet<AnalysisMetric>(ReferenceEqualityComparer.Instance) { metrics[499] };

        var kept = AnalysisMetricRetention.Select(metrics, flagged);

        Assert.Equal(52, kept.Count);
        Assert.Contains(metrics[499], kept);
    }

    [Fact]
    public void Select_CountsTheWorstPerKey()
    {
        var metrics = Files("a.file", 200, AnalysisMetricDirection.LowerIsBetter)
            .Concat(Files("b.file", 200, AnalysisMetricDirection.LowerIsBetter))
            .ToList();

        Assert.Equal(102, AnalysisMetricRetention.Select(metrics, None).Count);
    }

    [Fact]
    public void Select_ArchitectureSizedReport_WritesAtLeastTenTimesFewerRows()
    {
        // The shape of candidate #2328: 39,240 edge records of one key, informational.
        var metrics = Enumerable.Range(0, 39_240)
            .Select(index => new AnalysisMetric
            {
                Key = "architecture.dependencies.namespace",
                Value = index % 97 + 1,
                Symbol = $"Module{index % 400}->Target{index}",
                Direction = AnalysisMetricDirection.Informational
            })
            .Append(Metric("architecture.cycles", 0, AnalysisMetricDirection.LowerIsBetter))
            .ToList();

        var kept = AnalysisMetricRetention.Select(metrics, None);

        Assert.True(kept.Count * 10 <= metrics.Count, $"{kept.Count} rows kept out of {metrics.Count}");
        Assert.Contains(kept, metric => metric.Key == "architecture.cycles");
        Assert.Equal(metrics.Max(metric => metric.Value), kept.Max(metric => metric.Value));
        Assert.Equal(metrics.Min(metric => metric.Value), kept.Min(metric => metric.Value));
    }

    private static List<AnalysisMetric> Files(string key, int count, AnalysisMetricDirection direction) =>
        [.. Enumerable.Range(1, count).Select(value => new AnalysisMetric
        {
            Key = key,
            Value = value,
            FilePath = $"src/File{value}.cs",
            Direction = direction
        })];

    private static AnalysisMetric Metric(string key, double value, AnalysisMetricDirection direction) => new()
    {
        Key = key,
        Value = value,
        Scope = "project",
        Direction = direction
    };
}
