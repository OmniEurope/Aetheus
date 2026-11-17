// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Services;

/// <summary>
/// R-33: the organization a site Admin works in. An Admin belongs to no organization by default, so the list the
/// server gives for membership is empty, yet the Admin may pick any organization (the server lets a site Admin
/// read and write in each) or "all of them". The list comes from the server's own answer (administered
/// organizations); the client never invents an organization, and a user who is not a site Admin never gets the
/// Admin's list nor the "all" choice. The mocked HTTP proves what the service keeps for a given answer; the
/// refusal of a foreign organization by the real endpoints is proved by the integration tests.
/// </summary>
public sealed class ActiveOrganizationServiceAdminTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    private readonly InMemoryUserPreferences _preferences = new();

    public ActiveOrganizationServiceAdminTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
    }

    private ActiveOrganizationService CreateSut() => new(
        Services.GetRequiredService<ApiClient>(), _preferences, Services.GetRequiredService<AuthStateProvider>());

    private void ServerAnswers(IEnumerable<MyOrganizationDto> memberships, IEnumerable<(int Id, string Name)> administered)
    {
        _handler.SetJsonResponse("api/organizations/me", memberships.ToList());
        _handler.SetJsonResponse("api/organizations/administered",
            administered.Select(organization => new AdministeredOrganizationDto(organization.Id, organization.Name, [])).ToList());
    }

    [Fact]
    public async Task AnAdminWithoutMembership_CanChooseAmongEveryOrganization_AndStartsOnAll()
    {
        ServerAnswers([], [(3, "Beta"), (1, "Alpha")]);
        var sut = CreateSut();

        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(["Alpha", "Beta"], sut.Available.Select(organization => organization.Name));
        Assert.Null(sut.Active);
        Assert.True(sut.CanChooseAll);
        Assert.True(sut.IsLoaded);
    }

    [Fact]
    public async Task AnAdminWithAMembership_StartsOnItsOwnOrganization_NotOnTheFirstOfTheList()
    {
        ServerAnswers([new MyOrganizationDto(3, "Beta", "beta", OrganizationRole.Owner)], [(1, "Alpha"), (3, "Beta")]);
        var sut = CreateSut();

        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(3, sut.Active!.Id);
        Assert.Equal(OrganizationRole.Owner, sut.Available.Single(organization => organization.Id == 3).Role);
        Assert.Equal(2, sut.Available.Count);
    }

    [Fact]
    public async Task AnAdminChoosingAnOrganizationThenAll_KeepsEachChoiceAcrossAReload()
    {
        ServerAnswers([], [(1, "Alpha"), (3, "Beta")]);
        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        var changed = 0;
        sut.Changed += () => changed++;

        await sut.SetActiveAsync(3);
        Assert.Equal(3, sut.Active!.Id);
        Assert.Equal(3, _preferences.Read<int?>(PreferenceKeys.ActiveOrganization));
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.Equal(3, sut.Active!.Id);

        await sut.SetActiveAsync(ActiveOrganizationService.AllOrganizationsId);
        Assert.Null(sut.Active);
        Assert.Equal(ActiveOrganizationService.AllOrganizationsId, _preferences.Read<int?>(PreferenceKeys.ActiveOrganization));
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.Null(sut.Active);
        Assert.Equal(4, changed);
    }

    [Fact]
    public async Task AnAdmin_CannotChooseAnOrganizationTheServerDidNotList()
    {
        ServerAnswers([], [(1, "Alpha")]);
        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        await sut.SetActiveAsync(1);

        await sut.SetActiveAsync(999);

        Assert.Equal(1, sut.Active!.Id);
        Assert.Equal(1, _preferences.Read<int?>(PreferenceKeys.ActiveOrganization));
    }

    [Fact]
    public async Task WhenTheAdministeredListIsRefused_TheAdminKeepsItsMemberships_AndNothingMore()
    {
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto> { new(3, "Beta", "beta", OrganizationRole.Member) });
        _handler.SetResponse("api/organizations/administered", System.Net.HttpStatusCode.InternalServerError);
        var sut = CreateSut();

        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal([3], sut.Available.Select(organization => organization.Id));
        Assert.Equal(3, sut.Active!.Id);
        Assert.True(sut.IsLoaded);
    }
}
