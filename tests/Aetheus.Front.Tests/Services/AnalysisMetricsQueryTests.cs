// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;

namespace Aetheus.Front.Tests.Services;

/// <summary>The metrics page asks only for the filters the reader set.</summary>
public sealed class AnalysisMetricsQueryTests
{
    private static (AnalysisApi Api, BunitTestHelper.TestHandler Handler) Create()
    {
        var handler = new BunitTestHelper.TestHandler();
        handler.SetJsonResponse("api/analysis/projects/4/metrics", new PaginatedResult<AnalysisMetricDto>
        {
            Items = [new AnalysisMetricDto { Key = "coverage" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 25,
        });
        return (new AnalysisApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    [Fact]
    public async Task EveryFilter_IsSent()
    {
        var (api, handler) = Create();

        var page = await api.GetAnalysisMetricsAsync(4, 2, 25, "coverage", "csharp", "main", "value", true, latestReportOnly: true,
            ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("coverage", Assert.Single(page.Items).Key);
        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("key=coverage", url, StringComparison.Ordinal);
        Assert.Contains("language=csharp", url, StringComparison.Ordinal);
        Assert.Contains("branch=main", url, StringComparison.Ordinal);
        Assert.Contains("latestReportOnly=True", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlankFilters_AreLeftOut()
    {
        var (api, handler) = Create();

        await api.GetAnalysisMetricsAsync(4, 1, 25, " ", null, "", ct: Xunit.TestContext.Current.CancellationToken);

        var url = Assert.Single(handler.Requests).Url;
        Assert.DoesNotContain("key=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("language=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("branch=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("latestReportOnly", url, StringComparison.Ordinal);
    }
}
