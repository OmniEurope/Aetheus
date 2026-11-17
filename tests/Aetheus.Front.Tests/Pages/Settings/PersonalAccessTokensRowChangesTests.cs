// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Settings;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Settings;

/// <summary>R-10 on the personal access token grid: a created token shows at once from the answer, a revoked
/// one is read again quietly (no loading bar, the rows stay), and a refused revocation says so.</summary>
public sealed class PersonalAccessTokensRowChangesTests : BunitContext
{
    private const string Bar = "thead > tr.omni-data-grid__progress .omni-loading-bar";
    private const string ListUrl = "api/personal-access-tokens?";
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly ImmediateDialogService _dialog;

    public PersonalAccessTokensRowChangesTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialog = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
        _dialog.ConfirmResult = true;
    }

    private static PersonalAccessTokenDto Token(int id, string name, DateTime? revokedAt = null) => new()
    {
        Id = id,
        Name = name,
        Scope = PatScope.ReadOnly,
        TokenPrefix = "aeth_pat_ab",
        CreatedAt = DateTime.Now.AddDays(-1),
        ExpiresAt = DateTime.Now.AddDays(30),
        RevokedAt = revokedAt
    };

    private static PaginatedResult<PersonalAccessTokenDto> Page(params PersonalAccessTokenDto[] items) =>
        new() { Items = [.. items], TotalCount = items.Length };

    private IRenderedComponent<PersonalAccessTokens> RenderPage(params PersonalAccessTokenDto[] tokens)
    {
        _handler.SetJsonResponse(HttpMethod.Get, ListUrl, Page(tokens));
        var cut = Render<PersonalAccessTokens>();
        cut.WaitForAssertion(() => Assert.Contains(tokens[0].Name, cut.Markup, StringComparison.Ordinal));
        return cut;
    }

    private TaskCompletionSource<PaginatedResult<PersonalAccessTokenDto>> HoldTheNextListRead()
    {
        var gate = new TaskCompletionSource<PaginatedResult<PersonalAccessTokenDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        _handler.SetAsyncJsonResponse(HttpMethod.Get, ListUrl, _ => gate.Task);
        return gate;
    }

    private static AetheusDataGrid<PersonalAccessTokenDto> GridOf(IRenderedComponent<PersonalAccessTokens> cut) =>
        cut.FindComponent<AetheusDataGrid<PersonalAccessTokenDto>>().Instance;

    [Fact]
    public async Task ACreatedToken_ShowsAtOnce_AndTheReadBehindItDrawsNoBar()
    {
        var cut = RenderPage(Token(1, "ci"));
        var created = Token(2, "deploy");
        _handler.SetJsonResponse(HttpMethod.Post, "personal-access-tokens",
            new CreatedPersonalAccessTokenDto { Token = created, PlaintextToken = "aeth_pat_PLAINTEXT" });
        var gate = HoldTheNextListRead();

        cut.Find("input#Name").Input("deploy");
        await cut.Find("button[type=\"submit\"]").ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("deploy", cut.Markup, StringComparison.Ordinal));
        Assert.False(GridOf(cut).BackgroundRefresh.IsCompleted);
        Assert.Contains("ci", cut.Markup, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(Bar));

        gate.SetResult(Page(Token(1, "ci"), created));
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        Assert.Contains("deploy", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARevokedToken_IsReadAgainQuietly_NoBarAndTheRowsStay_AndARefusedRevocationSaysSo()
    {
        var cut = RenderPage(Token(1, "ci"), Token(2, "old", DateTime.Now.AddDays(-2)));
        _handler.SetResponse(HttpMethod.Delete, "api/personal-access-tokens/1", System.Net.HttpStatusCode.Conflict);

        await cut.Find("button[title=\"PatRevoke\"]").ClickAsync(new());
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniEurope.Blazor.Components.OmniSeverity.Danger);
        Assert.Contains("PatStatusActive", cut.Markup, StringComparison.Ordinal);

        _handler.SetResponse(HttpMethod.Delete, "api/personal-access-tokens/1", System.Net.HttpStatusCode.NoContent);
        var gate = HoldTheNextListRead();
        var revoking = cut.Find("button[title=\"PatRevoke\"]").ClickAsync(new());
        await _handler.WhenRequestedAsync(
            request => request.Method == "GET" && request.Url.Contains(ListUrl, StringComparison.Ordinal), 2, "the read behind the revocation");

        Assert.Empty(cut.FindAll(Bar));
        Assert.Contains("ci", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("old", cut.Markup, StringComparison.Ordinal);

        gate.SetResult(Page(Token(1, "ci", DateTime.Now), Token(2, "old", DateTime.Now.AddDays(-2))));
        await revoking;
        await cut.InvokeAsync(() => GridOf(cut).BackgroundRefresh);
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll("button[title=\"PatRevoke\"]")));
    }
}
