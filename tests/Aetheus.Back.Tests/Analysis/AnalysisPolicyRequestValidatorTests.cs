// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>A policy targets findings or exactly one metric, never both, and a metric policy
/// carries its operator and threshold.</summary>
public sealed class AnalysisPolicyRequestValidatorTests
{
    public static TheoryData<string, UpsertAnalysisPolicyRequest> AcceptedRequests() => new()
    {
        { "severity", new UpsertAnalysisPolicyRequest { Name = "p1", SeverityThreshold = AnalysisSeverity.High } },
        { "rule", new UpsertAnalysisPolicyRequest { Name = "p2", RuleId = "CA2100", NewFindingsOnly = true } },
        { "category", new UpsertAnalysisPolicyRequest { Name = "p3", Category = AnalysisCategory.Secrets } },
        { "scanner", new UpsertAnalysisPolicyRequest { Name = "p4", ScannerKey = "gitleaks" } },
        {
            "metric",
            new UpsertAnalysisPolicyRequest
            {
                Name = "p5", MetricKey = "coverage.line.percent", Category = AnalysisCategory.Coverage,
                ScannerKey = "coverlet", Operator = AnalysisPolicyOperator.LessThan, Threshold = 80
            }
        },
    };

    [Theory]
    [MemberData(nameof(AcceptedRequests))]
    public void Validate_AcceptsAFindingOrAMetricTarget(string target, UpsertAnalysisPolicyRequest request)
    {
        var error = Record.Exception(() => AnalysisPolicyRequestValidator.Validate(request));

        Assert.True(error is null, $"{target}: {error?.Message}");
    }

    public static TheoryData<UpsertAnalysisPolicyRequest, string> RejectedRequests() => new()
    {
        { new UpsertAnalysisPolicyRequest { Name = "none", ScannerKey = "  " }, "A policy must target findings or one metric." },
        {
            new UpsertAnalysisPolicyRequest
            {
                Name = "both", MetricKey = "m", SeverityThreshold = AnalysisSeverity.Low,
                Operator = AnalysisPolicyOperator.GreaterThan, Threshold = 1
            },
            "A metric policy cannot also target finding severity or rule id."
        },
        {
            new UpsertAnalysisPolicyRequest
            {
                Name = "rule", MetricKey = "m", RuleId = "R1", Operator = AnalysisPolicyOperator.GreaterThan, Threshold = 1
            },
            "A metric policy cannot also target finding severity or rule id."
        },
        {
            new UpsertAnalysisPolicyRequest
            {
                Name = "new", MetricKey = "m", NewFindingsOnly = true, Operator = AnalysisPolicyOperator.GreaterThan, Threshold = 1
            },
            "A metric policy cannot be limited to new findings."
        },
        {
            new UpsertAnalysisPolicyRequest { Name = "no-op", MetricKey = "m", Threshold = 1 },
            "A metric policy requires an operator and threshold."
        },
        {
            new UpsertAnalysisPolicyRequest { Name = "no-threshold", MetricKey = "m", Operator = AnalysisPolicyOperator.Changed },
            "A metric policy requires an operator and threshold."
        },
    };

    [Theory]
    [MemberData(nameof(RejectedRequests))]
    public void Validate_RejectsAmbiguousOrIncompletePolicies(UpsertAnalysisPolicyRequest request, string expected)
    {
        var error = Assert.Throws<BadRequestException>(() => AnalysisPolicyRequestValidator.Validate(request));

        Assert.Equal(expected, error.Message);
    }
}
