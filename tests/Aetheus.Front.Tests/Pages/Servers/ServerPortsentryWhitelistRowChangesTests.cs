// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-10 on the PortSentry whitelist grid: an added address shows at once from the answer, a removed one
/// leaves at once, the read behind them is quiet (no bar), and a refused removal says so.</summary>
public sealed class ServerPortsentryWhitelistRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string WhitelistUrl = "api/servers/50/portsentry/whitelist";
    private readonly BunitTestHelper.TestHandler _handler;

    public ServerPortsentryWhitelistRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        ((ImmediateDialogService)Services.GetRequiredService<OmniDialogService>()).ConfirmResult = true;
        _handler.SetJsonResponse("portsentry/filter-values", new PortsentryFilterValuesDto());
        _handler.SetJsonResponse("api/servers/50/portsentry/blocked", new PaginatedResult<PortsentryBlockedIpDto>());
    }

    private static PortsentryWhitelistIpDto Entry(int id, string ip) => new() { Id = id, IpAddress = ip, Description = "d" };

    private static PaginatedResult<PortsentryWhitelistIpDto> Page(params PortsentryWhitelistIpDto[] items) =>
        new() { Items = [.. items], TotalCount = items.Length };

    private IRenderedComponent<PageHeaderOutletHost> RenderSection(params PortsentryWhitelistIpDto[] whitelist)
    {
        _handler.SetJsonResponse(HttpMethod.Get, WhitelistUrl, Page(whitelist));
        var cut = this.RenderWithPageHeader<ServerPortsentrySection>(p => p
            .Add(x => x.ServerId, 50)
            .Add(x => x.Ps, new PortsentryDataDto { IsInstalled = true, IsRunning = true, Version = "2.0", Mode = "atcp" }));
        // The whitelist grid lives in its own tab.
        cut.Find("[role='tab'][data-key='whitelist']").Click();
        cut.WaitForAssertion(() => Assert.Contains(whitelist[0].IpAddress, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    /// <summary>The next read of the whitelist waits for the returned release; a test that ends early lets it go,
    /// so a failed assertion is reported instead of the context waiting on a read nobody answers.</summary>
    private (TaskCompletionSource<PaginatedResult<PortsentryWhitelistIpDto>> Gate, IDisposable Release) HoldTheNextWhitelistRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<PortsentryWhitelistIpDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, WhitelistUrl, _ => gate.Task);
        return (gate, new ReleaseOnDispose(() => gate.TrySetResult(Page())));
    }

    private static AetheusDataGrid<PortsentryWhitelistIpDto> GridOf(IRenderedComponent<PageHeaderOutletHost> cut) =>
        cut.FindComponent<AetheusDataGrid<PortsentryWhitelistIpDto>>().Instance;

    [Fact]
    public async Task AnAddedAddress_ShowsAtOnce_AndTheReadBehindItDrawsNoBar()
    {
        var cut = RenderSection(Entry(1, "10.0.0.1"));
        _handler.SetJsonResponse(HttpMethod.Post, WhitelistUrl, Entry(2, "10.0.0.2"));
        var (gate, release) = HoldTheNextWhitelistRead();
        using var _ = release;

        cut.Find("input#WlIp").Input("10.0.0.2");
        await cut.FindAll("button").First(button => button.TextContent.Trim() == "Add").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("10.0.0.2", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("10.0.0.1", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Entry(1, "10.0.0.1"), Entry(2, "10.0.0.2")));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.Contains("10.0.0.2", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovedAddress_LeavesAtOnce_AndARefusedRemovalKeepsItAndSaysSo()
    {
        var cut = RenderSection(Entry(1, "10.0.0.1"), Entry(2, "10.0.0.2"));
        _handler.SetResponse(HttpMethod.Delete, WhitelistUrl + "/2", System.Net.HttpStatusCode.Conflict);

        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());
        Assert.Contains("10.0.0.2", cut.Markup, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger);

        _handler.SetResponse(HttpMethod.Delete, WhitelistUrl + "/2", System.Net.HttpStatusCode.NoContent);
        var (gate, release) = HoldTheNextWhitelistRead();
        using var _ = release;
        await cut.FindAll("button[title='Delete']")[1].ClickAsync(new());

        cut.WaitForAssertion(() => Assert.DoesNotContain("10.0.0.2", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("10.0.0.1", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Entry(1, "10.0.0.1")));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.DoesNotContain("10.0.0.2", cut.Markup, StringComparison.Ordinal);
    }

    private sealed class ReleaseOnDispose(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
