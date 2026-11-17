// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Services;

/// <summary>R-33: the active-organization service for a user who is not a site Admin: its memberships only, never "all".</summary>
public sealed class ActiveOrganizationServiceMemberTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly InMemoryUserPreferences _preferences = new();

    public ActiveOrganizationServiceMemberTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false);
    }

    private ActiveOrganizationService CreateSut() => new(
        Services.GetRequiredService<ApiClient>(), _preferences, Services.GetRequiredService<AuthStateProvider>());

    [Fact]
    public async Task AMember_SeesItsOwnOrganizationsOnly_AndNeverReadsTheAdministeredList()
    {
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(1, "Alpha", "alpha", OrganizationRole.Member) });
        // Were it read, the answer would hand this user another organization.
        _handler.SetJsonResponse("api/organizations/administered", new List<AdministeredOrganizationDto> { new(2, "Foreign", []) });
        var sut = CreateSut();

        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal([1], sut.Available.Select(organization => organization.Id));
        Assert.False(sut.CanChooseAll);
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Url.Contains("administered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AMember_CannotChooseAllNorAForeignOrganization()
    {
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(1, "Alpha", "alpha", OrganizationRole.Member) });
        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        await sut.SetActiveAsync(ActiveOrganizationService.AllOrganizationsId);
        await sut.SetActiveAsync(2);

        Assert.Equal(1, sut.Active!.Id);
        Assert.Null(_preferences.Read<int?>(PreferenceKeys.ActiveOrganization));
    }

    [Fact]
    public async Task AStoredChoiceOfAll_IsIgnoredForAMember()
    {
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(1, "Alpha", "alpha", OrganizationRole.Member) });
        _preferences.Seed(PreferenceKeys.ActiveOrganization, ActiveOrganizationService.AllOrganizationsId);
        var sut = CreateSut();

        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(1, sut.Active!.Id);
    }
}
