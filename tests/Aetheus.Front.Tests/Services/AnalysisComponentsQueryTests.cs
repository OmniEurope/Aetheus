// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Analysis;

namespace Aetheus.Front.Tests.Services;

/// <summary>The components list asks only for the filters the reader set, and an empty answer is an empty page.</summary>
public sealed class AnalysisComponentsQueryTests
{
    private static (AnalysisApi Api, BunitTestHelper.TestHandler Handler) Create(string body)
    {
        var handler = new BunitTestHelper.TestHandler();
        handler.SetRawResponse("api/analysis/projects/4/components", body);
        return (new AnalysisApi(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }), handler);
    }

    [Fact]
    public async Task EveryFilter_IsSent()
    {
        var (api, handler) = Create("""{"items":[{"name":"Newtonsoft.Json"}],"totalCount":1,"page":2,"pageSize":25}""");

        var page = await api.GetAnalysisComponentsAsync(4, 2, 25, "json", "nuget", "main", "name", true,
            Xunit.TestContext.Current.CancellationToken);

        Assert.Equal("Newtonsoft.Json", Assert.Single(page.Items).Name);
        var url = Assert.Single(handler.Requests).Url;
        Assert.Contains("api/analysis/projects/4/components?", url, StringComparison.Ordinal);
        Assert.Contains("page=2", url, StringComparison.Ordinal);
        Assert.Contains("componentType=nuget", url, StringComparison.Ordinal);
        Assert.Contains("branch=main", url, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BlankFilters_AreLeftOut_AndANullAnswerIsAnEmptyPage()
    {
        var (api, handler) = Create("null");

        var page = await api.GetAnalysisComponentsAsync(4, 1, 25, componentType: " ", branch: "",
            ct: Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(page.Items);
        var url = Assert.Single(handler.Requests).Url;
        Assert.DoesNotContain("componentType=", url, StringComparison.Ordinal);
        Assert.DoesNotContain("branch=", url, StringComparison.Ordinal);
    }
}
