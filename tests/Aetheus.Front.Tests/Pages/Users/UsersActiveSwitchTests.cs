// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Front.Components.Users;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using UsersPage = Aetheus.Front.Components.Users.Users;

namespace Aetheus.Front.Tests.Pages.Users;

/// <summary>R-25 and R-18 on the Users grid: the "Active" switch of a row saves without a dialog, the
/// signed-in user's own switch cannot be turned off, and the grid shows the last modification and the
/// last activity of each account.</summary>
public class UsersActiveSwitchTests : BunitContext
{
    private const int SignedInUserId = 1;
    private readonly BunitTestHelper.TestHandler _handler;

    public UsersActiveSwitchTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
    }

    private async Task<IRenderedComponent<UsersPage>> RenderPageAsync()
    {
        // The signed-in user is user 1 ("alice").
        await Services.GetRequiredService<AuthStateProvider>().LoginAsync(TokenFor(SignedInUserId));
        _handler.SetJsonResponse("api/pipelines/ownership/owned", new List<PipelineOwnershipDto>());
        _handler.SetJsonResponse("api/users/roles", new List<string> { "Admin", "Reader" });
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto>
        {
            Items =
            [
                new UserDto { Id = 1, Username = "alice", Email = "alice@example.org", IsActive = true, Roles = ["Admin"], UpdatedAt = new DateTime(2026, 10, 1, 8, 30, 0), LastActivityAt = new DateTime(2026, 10, 27, 14, 5, 0) },
                new UserDto { Id = 2, Username = "bob", Email = "bob@example.org", IsActive = true, Roles = ["Reader"] }
            ],
            TotalCount = 2
        });
        var cut = Render<UsersPage>();
        cut.WaitForState(() => cut.Markup.Contains("bob"));
        return cut;
    }

    [Fact]
    public async Task TheActiveSwitchOfAnotherUser_SavesWithoutConfirmation()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/users/2", new UserDto { Id = 2, Username = "bob", IsActive = false });
        var cut = await RenderPageAsync();

        var bobSwitch = SwitchOf(cut, "bob");
        Assert.True(IsOn(bobSwitch));
        await cut.InvokeAsync(() => bobSwitch.Click());

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails,
            request => request.Method == "PUT" && request.Url.EndsWith("api/users/2", StringComparison.Ordinal)));
        var body = _handler.RequestDetails.Last(request => request.Method == "PUT").Body!;
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal("bob", json.RootElement.GetProperty("username").GetString());
        Assert.Equal("Reader", json.RootElement.GetProperty("roles")[0].GetString());
    }

    [Fact]
    public async Task SwitchingOffAnOwnerOfPipelines_OpensTheHandoverDialog_AndConfirmingSendsTheHandoverWithTheDeactivation()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/users/2", new UserDto { Id = 2, Username = "bob", IsActive = false });
        var cut = await RenderPageAsync();
        _handler.SetJsonResponse("api/pipelines/ownership/owned", new List<PipelineOwnershipDto> { new() { PipelineId = 7, PipelineName = "build", OwnerUsername = "bob" } });
        var dialog = Services.GetRequiredService<OmniDialogService>();
        Type? opened = null;
        dialog.OnOpen += (_, type, _, _) => opened = type;

        var change = cut.InvokeAsync(() => SwitchOf(cut, "bob").Click());
        cut.WaitForAssertion(() => Assert.Equal(typeof(UserPipelineHandoverDialog), opened));
        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "PUT");
        await cut.InvokeAsync(() => dialog.Close(new UserPipelineHandover("alice", false)));
        await change;

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == "PUT"));
        var body = _handler.RequestDetails.Last(request => request.Method == "PUT").Body!;
        using var json = JsonDocument.Parse(body);
        Assert.False(json.RootElement.GetProperty("isActive").GetBoolean());
        Assert.Equal("alice", json.RootElement.GetProperty("transferPipelinesToUsername").GetString());
    }

    [Fact]
    public async Task DismissingTheHandoverDialog_SendsNothing_AndTheSwitchStaysOn()
    {
        var cut = await RenderPageAsync();
        _handler.SetJsonResponse("api/pipelines/ownership/owned", new List<PipelineOwnershipDto> { new() { PipelineId = 7, PipelineName = "build", OwnerUsername = "bob" } });
        var dialog = Services.GetRequiredService<OmniDialogService>();
        Type? opened = null;
        dialog.OnOpen += (_, type, _, _) => opened = type;

        var change = cut.InvokeAsync(() => SwitchOf(cut, "bob").Click());
        cut.WaitForAssertion(() => Assert.Equal(typeof(UserPipelineHandoverDialog), opened));
        await cut.InvokeAsync(() => dialog.Close(null));
        await change;

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "PUT");
        cut.WaitForAssertion(() => Assert.True(IsOn(SwitchOf(cut, "bob"))));
    }

    [Fact]
    public async Task TheSignedInUsersOwnSwitch_IsDisabled_AndSendsNothing()
    {
        var cut = await RenderPageAsync();

        var ownSwitch = SwitchOf(cut, "alice");
        Assert.True(ownSwitch.HasAttribute("disabled"));
        await cut.InvokeAsync(() => ownSwitch.Click());

        Assert.DoesNotContain(_handler.RequestDetails, request => request.Method == "PUT");
    }

    [Fact]
    public async Task TheGrid_ShowsTheLastModificationAndTheLastActivityColumns()
    {
        var cut = await RenderPageAsync();

        Assert.Contains("LastModified", cut.Markup);
        Assert.Contains("LastActivity", cut.Markup);
        var aliceCells = cut.FindAll("tr").First(row => row.TextContent.Contains("alice", StringComparison.Ordinal)).QuerySelectorAll("td").Select(cell => cell.TextContent.Trim()).ToList();
        // Dates arrive as UTC and the grid shows them in local time, as it does for Created.
        var activity = DateTime.SpecifyKind(new DateTime(2026, 10, 27, 14, 5, 0), DateTimeKind.Utc).ToLocalTime();
        Assert.Contains(activity.ToString("g", System.Globalization.CultureInfo.CurrentCulture), aliceCells);
    }

    private static AngleSharp.Dom.IElement SwitchOf(IRenderedComponent<UsersPage> cut, string username)
    {
        var row = cut.FindAll("tr").First(candidate => candidate.TextContent.Contains(username, StringComparison.Ordinal));
        return row.QuerySelector("[role=switch]")!;
    }

    private static bool IsOn(AngleSharp.Dom.IElement element) =>
        string.Equals(element.GetAttribute("aria-checked"), "true", StringComparison.Ordinal);

    private static string TokenFor(int userId)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(Encoding.UTF8.GetBytes("{\"alg\":\"HS256\"}"));
        var payload = Encode(JsonSerializer.SerializeToUtf8Bytes(new
        {
            sub = "alice",
            role = "Admin",
            unique_name = "alice",
            nameid = userId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds()
        }));
        return $"{header}.{payload}.sig";
    }
}
