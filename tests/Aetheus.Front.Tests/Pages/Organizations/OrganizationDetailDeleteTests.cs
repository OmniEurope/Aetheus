// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Components.Organizations;
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Organizations;

/// <summary>Deleting an organization asks first; a deletion the server accepts returns to the list, a
/// refused one is reported and stays on the page.</summary>
public sealed class OrganizationDetailDeleteTests : BunitContext
{
    private const string DeleteUrl = "api/organizations/1";
    private readonly BunitTestHelper.TestHandler _handler;

    public OrganizationDetailDeleteTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        _handler.SetJsonResponse("api/organizations/1", new OrganizationDetailDto(
            Id: 1, Name: "Aetheus", Slug: "aetheus", Description: "",
            CreatedAt: DateTime.UtcNow, UpdatedAt: DateTime.UtcNow, Members: [], Projects: []));
        _handler.SetJsonResponse("api/users", new PaginatedResult<UserDto> { Items = [], TotalCount = 0 });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto> { Items = [], TotalCount = 0 });
    }

    private ImmediateDialogService Dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    private string Location => Services.GetRequiredService<NavigationManager>().Uri;

    private bool DeleteSent() => _handler.Requests.Any(request => request.Method == "DELETE" && request.Url.EndsWith(DeleteUrl, StringComparison.Ordinal));

    private async Task DeleteAsync()
    {
        var cut = Render<OrganizationDetail>(p => p.Add(c => c.Id, 1));
        // STD-BTN detection 11: Delete organization is an entry of the general form's "More actions" menu.
        cut.WaitForAssertion(() => Assert.NotNull(cut.FindMenuItem("DeleteOrganization")));
        await cut.FindMenuItem("DeleteOrganization")!.ClickAsync(new());
    }

    [Fact]
    public async Task ADeclinedDeletion_DeletesNothing()
    {
        Dialog.ConfirmResult = false;

        await DeleteAsync();

        Assert.NotNull(Dialog.LastConfirmMessage);
        Assert.False(DeleteSent());
    }

    [Fact]
    public async Task AnAcceptedDeletion_ReturnsToTheList()
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, DeleteUrl, HttpStatusCode.NoContent);

        await DeleteAsync();

        Assert.True(DeleteSent());
        Assert.EndsWith("/admin/organizations", Location, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Success);
    }

    [Fact]
    public async Task ARefusedDeletion_IsReportedAndStaysOnThePage()
    {
        Dialog.ConfirmResult = true;
        _handler.SetResponse(HttpMethod.Delete, DeleteUrl, HttpStatusCode.Conflict);

        await DeleteAsync();

        Assert.True(DeleteSent());
        Assert.DoesNotContain("/admin/organizations", Location, StringComparison.Ordinal);
        Assert.Contains(Services.Toasts(), toast => toast.Severity == OmniSeverity.Danger && toast.Detail == "DeleteFailed");
    }
}
