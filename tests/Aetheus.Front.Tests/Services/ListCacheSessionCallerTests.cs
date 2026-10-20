// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Git;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// Front follow-up of FRONT-020: the callers that fetched on their own and then wrote the list cache
/// (dashboard, repository lists, API reference, monitored apps, vault and library lists, page states)
/// stored an answer that arrived after the sign-out under the previous account and showed it. They now
/// take the cache's ticket before the request, so such an answer is neither stored nor applied.
/// </summary>
public sealed class ListCacheSessionCallerTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ListCacheSessionCallerTests() => _handler = BunitTestHelper.RegisterServices(this);

    [Fact]
    public void Ticket_TakenBeforeASignOut_StoresNothing()
    {
        var cache = new ListCacheService(new FakeTimeProvider());
        var ticket = cache.Begin();

        cache.Clear();

        Assert.False(cache.TryStore(ticket, "k", new List<int> { 1 }));
        Assert.False(cache.TryGet<List<int>>("k", out _));
        Assert.True(cache.TryStore(cache.Begin(), "k", new List<int> { 2 }));
    }

    [Fact]
    public async Task GitPagedList_BlockArrivingAfterASignOut_IsNeitherCachedNorShown()
    {
        var cache = new ListCacheService(new FakeTimeProvider());
        var list = new GitPagedList<string>(exception => exception is HttpRequestException);
        var answer = new TaskCompletionSource<PaginatedResult<string>>(TaskCreationOptions.RunContinuationsAsynchronously);

        var load = list.LoadCachedAsync(cache, "commits:1", () => answer.Task);
        cache.Clear();
        answer.SetResult(new PaginatedResult<string> { Items = ["previous account's commit"], TotalCount = 1 });
        await load;

        Assert.False(cache.TryGet<PaginatedResult<string>>("commits:1", out _));
        Assert.Empty(list.Items);
    }

    [Fact]
    public void Home_DashboardArrivingAfterASignOut_IsNeitherCachedNorShown()
    {
        _handler.SetJsonResponse(HttpMethod.Get, "api/appmonitoring/summary", new AppMonitoringSummaryDto());
        BunitTestHelper.SetOnboardingWizardResponses(_handler);
        var dashboard = new TaskCompletionSource<DashboardOverviewDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        // Only the request issued before the sign-out answers with the previous account's data. A reload
        // that starts after it (permissions or realtime events, timing-dependent under a loaded test run)
        // is a new account's request: answering it with the same 4242 made this test fail 1 run in 6.
        var dashboardRequests = 0;
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "monitoring/dashboard", _ =>
            Interlocked.Increment(ref dashboardRequests) == 1
                ? dashboard.Task
                : Task.FromResult(new DashboardOverviewDto { TotalServers = 7, Servers = [], RecentRuns = [] }));
        var cache = Services.GetRequiredService<ListCacheService>();
        var cut = Render<Home>();

        cache.Clear();
        dashboard.SetResult(new DashboardOverviewDto { TotalServers = 4242, Servers = [], RecentRuns = [] });
        cut.WaitForState(() => _handler.Requests.Any(r => r.Url.Contains("appmonitoring/summary", StringComparison.Ordinal)));

        Assert.False(cache.TryGet<DashboardOverviewDto>("dashboard:overview", out _));
        // The rendered text, not the markup: a generated element id (a GUID) can hold "4242" by chance.
        Assert.DoesNotContain("4242", string.Concat(cut.Nodes.Select(node => node.TextContent)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VaultsList_ServerVaultsArrivingAfterASignOut_AreNeitherCachedNorShown()
    {
        var vaults = new TaskCompletionSource<List<VaultDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, "api/servers/3/vaults", _ => vaults.Task);
        var cache = Services.GetRequiredService<ListCacheService>();
        var cut = Render<VaultsList>(p => p.Add(x => x.ServerId, (int?)3));

        cache.Clear();
        vaults.SetResult([new VaultDto { Id = 9, Name = "previous-account-vault" }]);
        await Task.Delay(200, Xunit.TestContext.Current.CancellationToken);

        Assert.DoesNotContain("previous-account-vault", cut.Markup, StringComparison.Ordinal);
        Assert.False(cache.TryGet<List<VaultDto>>("vaults:server:3", out _));
    }
}
