// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Front.Components.Settings;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Settings;

public class PersonalAccessTokensPageTests : BunitContext
{
    private static PersonalAccessTokenDto Active(int id = 1, string name = "ci") => new()
    {
        Id = id,
        Name = name,
        Scope = PatScope.ReadOnly,
        TokenPrefix = "aeth_pat_ab",
        CreatedAt = DateTime.Now.AddDays(-1),
        ExpiresAt = DateTime.Now.AddDays(30)
    };

    private static PersonalAccessTokenDto Revoked(int id = 2, string name = "old") => new()
    {
        Id = id,
        Name = name,
        Scope = PatScope.ReadWrite,
        TokenPrefix = "aeth_pat_cd",
        CreatedAt = DateTime.Now.AddDays(-5),
        ExpiresAt = DateTime.Now.AddDays(30),
        RevokedAt = DateTime.Now.AddDays(-1)
    };

    [Fact]
    public void InitialRender_LoadsTokenRows_WithStatusAndRevokeOnlyForActive()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "personal-access-tokens",
            new[] { Active(1, "ci"), Revoked(2, "old") });

        var cut = Render<PersonalAccessTokens>();

        cut.WaitForAssertion(() => Assert.Multiple(() =>
        {
            Assert.Contains("ci", cut.Markup);
            Assert.Contains("old", cut.Markup);
            Assert.Contains("PatStatusActive", cut.Markup);
            Assert.Contains("PatStatusRevoked", cut.Markup);
            Assert.DoesNotContain("rz-data-grid-loading", cut.Markup);
            Assert.Contains(handler.Requests, request =>
                request.Method == "GET" && request.Url.Contains("personal-access-tokens", StringComparison.Ordinal));
            // Exactly one revoke button (only the active token is revocable).
            Assert.Single(cut.FindAll("button[title=\"PatRevoke\"]"));
        }));
    }

    [Fact]
    public async Task EmptyList_ShowsEmptyState()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "personal-access-tokens", Array.Empty<PersonalAccessTokenDto>());

        var cut = Render<PersonalAccessTokens>();
        var grid = cut.FindComponent<OmniDataGrid<PersonalAccessTokenDto>>();
        await cut.InvokeAsync(grid.Instance.ReloadAsync);

        Assert.Contains("PatEmptyTitle", cut.Markup);
    }

    [Fact]
    public void Create_RevealsPlaintextTokenOnce_AndPostsRequest()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "personal-access-tokens", Array.Empty<PersonalAccessTokenDto>());
        handler.SetJsonResponse(HttpMethod.Post, "personal-access-tokens", new CreatedPersonalAccessTokenDto
        {
            Token = Active(3, "deploy"),
            PlaintextToken = "aeth_pat_SUPERSECRETVALUE1234567890"
        });

        var cut = Render<PersonalAccessTokens>();

        cut.Find("input#Name").Input("deploy");
        cut.Find("button[type=\"submit\"]").Click();

        Assert.Contains("aeth_pat_SUPERSECRETVALUE1234567890", cut.Markup);
        Assert.Contains("PatRevealTitle", cut.Markup);
        Assert.Contains(handler.Requests, r => r.Method == "POST" && r.Url.Contains("personal-access-tokens"));
        var body = handler.RequestDetails.Last(request =>
            request.Method == "POST" && request.Url.EndsWith("api/personal-access-tokens", StringComparison.Ordinal)).Body;
        var request = System.Text.Json.JsonSerializer.Deserialize<CreatePersonalAccessTokenRequest>(
            body!, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.Equal("deploy", request!.Name);
    }

    [Fact]
    public async Task HeaderFilters_AreSentAsColumnFilters()
    {
        // The scope list and the three date ranges are column filters of the endpoint.
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetPaginatedJsonResponse(HttpMethod.Get, "personal-access-tokens", new[] { Active(1, "ci") });
        var cut = Render<PersonalAccessTokens>();
        var grid = cut.FindComponent<AetheusDataGrid<PersonalAccessTokenDto>>();

        await cut.InvokeAsync(() => grid.Instance.LoadData.InvokeAsync(new GridLoadArgs
        {
            Top = 25,
            Filters =
            [
                new GridFilterDescriptor(nameof(PersonalAccessTokenDto.Scope), "ReadWrite", OmniDataGridFilterOperator.In),
                new GridFilterDescriptor(nameof(PersonalAccessTokenDto.ExpiresAt), "2026-10-01",
                    OmniDataGridFilterOperator.GreaterThanOrEquals, OmniDataGridFilterOperator.LessThan, "2026-11-01")
            ]
        }));

        cut.WaitForAssertion(() => Assert.Contains(handler.Requests, request =>
        {
            var url = Uri.UnescapeDataString(request.Url);
            return url.Contains("api/personal-access-tokens?", StringComparison.Ordinal)
                && url.Contains("Filters[0].Field=Scope", StringComparison.Ordinal)
                && url.Contains("Filters[0].Value=ReadWrite", StringComparison.Ordinal)
                && url.Contains("Filters[1].Field=ExpiresAt", StringComparison.Ordinal)
                && url.Contains("Filters[1].SecondValue=2026-11-01", StringComparison.Ordinal);
        }));
    }
}
