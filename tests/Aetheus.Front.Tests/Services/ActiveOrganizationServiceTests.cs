// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;
using Aetheus.Shared.Components.Organizations;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Services;

public class ActiveOrganizationServiceTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;
    // R1-66: the active organization is a user preference; none is stored unless a test seeds one.
    private readonly InMemoryUserPreferences _preferences = new();

    public ActiveOrganizationServiceTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private ActiveOrganizationService CreateSut() =>
        new(Services.GetRequiredService<ApiClient>(), _preferences);

    private static MyOrganizationDto MakeOrg(int id, string name = "OrgName") =>
        new(id, name, name.ToLower(), OrganizationRole.Member);

    // === Initial state ===

    [Fact]
    public void InitialState_IsEmpty()
    {
        var sut = CreateSut();
        Assert.Empty(sut.Available);
        Assert.Null(sut.Active);
        Assert.False(sut.IsLoaded);
    }

    // === RefreshAsync ===

    [Fact]
    public async Task RefreshAsync_SetsAvailableAndActive_AndFiresChanged()
    {
        var orgs = new List<MyOrganizationDto> { MakeOrg(1, "Org1"), MakeOrg(2, "Org2") };
        _handler.SetJsonResponse("api/organizations/me", orgs);

        var sut = CreateSut();
        var changedCount = 0;
        sut.Changed += () => changedCount++;

        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(2, sut.Available.Count);
        Assert.NotNull(sut.Active);
        Assert.Equal(1, sut.Active!.Id); // first org is default when no stored id
        Assert.True(sut.IsLoaded);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public async Task RefreshAsync_EmptyOrgs_ActiveIsNull()
    {
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto>());

        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Empty(sut.Available);
        Assert.Null(sut.Active);
        Assert.True(sut.IsLoaded);
    }

    [Fact]
    public async Task RefreshAsync_WithStoredId_SelectsMatchingOrg()
    {
        var orgs = new List<MyOrganizationDto> { MakeOrg(1, "Org1"), MakeOrg(2, "Org2") };
        _handler.SetJsonResponse("api/organizations/me", orgs);

        _preferences.Seed(PreferenceKeys.ActiveOrganization, 2);

        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        Assert.Equal(2, sut.Active!.Id);
    }

    // === SetActiveAsync ===

    [Fact]
    public async Task SetActiveAsync_ValidId_SetsActiveAndFiresChanged()
    {
        var orgs = new List<MyOrganizationDto> { MakeOrg(1, "Org1"), MakeOrg(2, "Org2") };
        _handler.SetJsonResponse("api/organizations/me", orgs);

        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        var changedCount = 0;
        sut.Changed += () => changedCount++;

        await sut.SetActiveAsync(2);

        Assert.Equal(2, sut.Active!.Id);
        Assert.Equal(1, changedCount);
        Assert.Equal(2, _preferences.Read<int?>(PreferenceKeys.ActiveOrganization));
    }

    [Fact]
    public async Task SetActiveAsync_InvalidId_LeavesTheValueUnchanged()
    {
        var orgs = new List<MyOrganizationDto> { MakeOrg(1, "Org1") };
        _handler.SetJsonResponse("api/organizations/me", orgs);

        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        var changedCount = 0;
        sut.Changed += () => changedCount++;

        await sut.SetActiveAsync(999); // no such org

        Assert.Equal(1, sut.Active!.Id); // unchanged
        Assert.Equal(0, changedCount);
    }

    [Fact]
    public async Task SetActiveAsync_SameId_DoesNotFireChanged()
    {
        var orgs = new List<MyOrganizationDto> { MakeOrg(1, "Org1") };
        _handler.SetJsonResponse("api/organizations/me", orgs);

        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken); // sets Active to 1

        var changedCount = 0;
        sut.Changed += () => changedCount++;

        await sut.SetActiveAsync(1); // already active

        Assert.Equal(0, changedCount);
    }

    // === Clear ===

    [Fact]
    public async Task Clear_ResetsState_AndFiresChanged()
    {
        var orgs = new List<MyOrganizationDto> { MakeOrg(1, "Org1") };
        _handler.SetJsonResponse("api/organizations/me", orgs);

        var sut = CreateSut();
        await sut.RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        Assert.True(sut.IsLoaded);

        var changedCount = 0;
        sut.Changed += () => changedCount++;

        sut.Clear();

        Assert.Empty(sut.Available);
        Assert.Null(sut.Active);
        Assert.False(sut.IsLoaded);
        Assert.Equal(1, changedCount);
    }

    [Fact]
    public void Clear_WhenAlreadyEmpty_StillFiresChanged()
    {
        var sut = CreateSut();
        var changedCount = 0;
        sut.Changed += () => changedCount++;

        sut.Clear();

        Assert.Equal(1, changedCount);
    }
}
