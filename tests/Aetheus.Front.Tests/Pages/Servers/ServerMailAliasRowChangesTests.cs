// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>R-10 on the mail aliases grid: an alias the server created shows at once from the answer, a deleted one
/// leaves at once, and the read behind them is quiet (no bar).</summary>
public sealed class ServerMailAliasRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string AliasesUrl = "api/servers/10/mail/aliases";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public ServerMailAliasRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _handler.SetJsonResponse("api/servers/10/mail/domains", new PaginatedResult<MailDomainDto>
        {
            Items = [new() { Id = 1, Name = "example.com", IsActive = true, DkimSelector = "default" }],
            TotalCount = 1
        });
        _handler.SetJsonResponse("api/servers/10/mail/accounts", new PaginatedResult<MailAccountDto>());
        _handler.SetJsonResponse("api/servers/10/mail/diagnostics", new MailDiagnosticsDto());
    }

    private static MailAliasDto Alias(int id, string source) => new() { Id = id, SourceEmail = source, DestinationEmail = "admin@example.com" };

    private static PaginatedResult<MailAliasDto> Page(params MailAliasDto[] items) => new() { Items = [.. items], TotalCount = items.Length };

    private IRenderedComponent<ServerMailSection> RenderAliases(params MailAliasDto[] aliases)
    {
        _handler.SetJsonResponse(HttpMethod.Get, AliasesUrl, Page(aliases));
        var server = ServerTestData.MakeMailServer(true, true, true);
        var cut = Render<ServerMailSection>(p => p.Add(x => x.Server, server).Add(x => x.ServerId, server.Id));
        cut.FindAll("[role='tab']").First(tab => tab.TextContent.Contains("Aliases", StringComparison.Ordinal)).Click();
        cut.WaitForAssertion(() => Assert.Contains(aliases[0].SourceEmail, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private (TaskCompletionSource<PaginatedResult<MailAliasDto>> Gate, IDisposable Release) HoldTheNextAliasRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<MailAliasDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, AliasesUrl, _ => gate.Task);
        return (gate, new ReleaseOnDispose(() => gate.TrySetResult(Page())));
    }

    private static AetheusDataGrid<MailAliasDto> GridOf(IRenderedComponent<ServerMailSection> cut) =>
        cut.FindComponent<AetheusDataGrid<MailAliasDto>>().Instance;

    [Fact]
    public async Task ACreatedAlias_ShowsAtOnce_AndTheReadBehindItDrawsNoBar()
    {
        _dialog.OpenResult = new MailDialogModel
        {
            Mode = MailDialogMode.AddAlias,
            Source = "new@example.com",
            Destination = "admin@example.com",
            DomainId = 1
        };
        _handler.SetJsonResponse(HttpMethod.Post, "api/servers/10/mail/domains/1/aliases", Alias(2, "new@example.com"));
        var cut = RenderAliases(Alias(1, "old@example.com"));
        var (gate, release) = HoldTheNextAliasRead();
        using var _ = release;

        cut.FindAll("button").First(b => b.Names().Contains("AddAlias", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("new@example.com", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("old@example.com", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Alias(1, "old@example.com"), Alias(2, "new@example.com")));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.Contains("new@example.com", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ADeletedAlias_LeavesAtOnce_AndTheReadBehindItDrawsNoBar()
    {
        _dialog.ConfirmResult = true;
        _handler.SetJsonResponse(HttpMethod.Delete, AliasesUrl + "/2", new { });
        var cut = RenderAliases(Alias(1, "keep@example.com"), Alias(2, "gone@example.com"));
        var (gate, release) = HoldTheNextAliasRead();
        using var _ = release;

        cut.FindAll("tr").Single(row => row.TextContent.Contains("gone@example.com", StringComparison.Ordinal))
            .QuerySelector("button[title='Delete']")!.Click();

        cut.WaitForAssertion(() => Assert.DoesNotContain("gone@example.com", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("keep@example.com", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Alias(1, "keep@example.com")));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.DoesNotContain("gone@example.com", cut.Markup, StringComparison.Ordinal);
    }

    private sealed class ReleaseOnDispose(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
}
