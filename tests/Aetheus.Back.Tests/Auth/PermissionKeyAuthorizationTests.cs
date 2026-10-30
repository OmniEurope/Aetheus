// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

/// <summary>R-11: a named permission is checked like a level, in the organization owning the resource,
/// with the site Admin holding every key and an unknown key refused loudly rather than ignored.</summary>
public sealed class PermissionKeyAuthorizationTests
{
    private const int Org = 4;
    private const int OtherOrg = 9;
    private const int Server = 70;
    private const int RoleInOrg = 11;

    private readonly IPermissionRepository _repo = Substitute.For<IPermissionRepository>();
    private readonly AuthzCacheEvictor _evictor = new();
    private readonly ResourceAuthorizationService _sut;

    public PermissionKeyAuthorizationTests()
    {
        _sut = new ResourceAuthorizationService(_repo, new MemoryCache(new MemoryCacheOptions()), _evictor);
        _repo.GetRoleIdsForUserAsync("bob", Arg.Any<CancellationToken>()).Returns([]);
        _repo.GetOrganizationRoleGrantsAsync("bob", Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(Org, RoleInOrg, "Server contributor")]);
        _repo.GetResourceOrganizationIdAsync(ResourceType.Server, Server, Arg.Any<CancellationToken>()).Returns(Org);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal User(string name, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, name) };
        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private void Grant(string key, int? resourceId, bool held) => _repo
        .HasPermissionKeyAsync(Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { RoleInOrg })), key, resourceId, Arg.Any<CancellationToken>())
        .Returns(held);

    [Fact]
    public async Task AKeyHeldInTheServersOrganization_Passes()
    {
        Grant(PermissionKeys.ServerFirewallManage, Server, held: true);

        Assert.True(await _sut.HasPermissionAsync(User("bob"), PermissionKeys.ServerFirewallManage, Server, Ct));
    }

    [Fact]
    public async Task AKeyNotHeld_IsRefused_EvenWithTheLevelItUsedToNeed()
    {
        Grant(PermissionKeys.ServerFirewallManage, Server, held: false);
        _repo.HasPermissionAsync(Arg.Any<List<int>>(), ResourceType.Server, Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);

        Assert.False(await _sut.HasPermissionAsync(User("bob"), PermissionKeys.ServerFirewallManage, Server, Ct));
    }

    [Fact]
    public async Task AKeyHeldInOneOrganization_GrantsNothingOnAServerOfAnother()
    {
        Grant(PermissionKeys.ServerFirewallManage, Server, held: true);
        _repo.GetResourceOrganizationIdAsync(ResourceType.Server, Server, Arg.Any<CancellationToken>()).Returns(OtherOrg);

        Assert.False(await _sut.HasPermissionAsync(User("bob"), PermissionKeys.ServerFirewallManage, Server, Ct));
        await _repo.DidNotReceiveWithAnyArgs().HasPermissionKeyAsync(default!, default!, default, Ct);
    }

    [Fact]
    public async Task TheSiteAdmin_HoldsEveryKey_ByClaimOrByUsername()
    {
        Assert.True(await _sut.HasPermissionAsync(User("root", RoleNames.Admin), PermissionKeys.ServerDockerContainerEnvView, Server, Ct));

        // Non-interactive callers (scheduler, webhooks) pass a username: the Admin role id is what counts.
        _repo.GetRoleIdsForUserAsync("root", Arg.Any<CancellationToken>()).Returns([1]);
        Assert.True(await _sut.HasPermissionAsync("root", PermissionKeys.ServerSystemUpgrade, Server, Ct));
    }

    [Fact]
    public async Task ALevelKey_IsTheLevelItself()
    {
        _repo.HasPermissionAsync(Arg.Any<List<int>>(), ResourceType.Server, Server, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(true);

        Assert.True(await _sut.HasPermissionAsync(User("bob"), PermissionKeys.ServerEdit, Server, Ct));
        await _repo.DidNotReceiveWithAnyArgs().HasPermissionKeyAsync(default!, default!, default, Ct);
    }

    [Fact]
    public async Task AnOrganizationKey_IsCheckedOnTheOrganizationNamed()
    {
        Grant(PermissionKeys.OrganizationMembersManage, null, held: true);

        Assert.True(await _sut.HasPermissionAsync(User("bob"), PermissionKeys.OrganizationMembersManage, Org, Ct));
        Assert.False(await _sut.HasPermissionAsync(User("bob"), PermissionKeys.OrganizationMembersManage, OtherOrg, Ct));
    }

    [Fact]
    public async Task AnUnknownKey_Throws_InsteadOfAnsweringFalseOrTrue()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.HasPermissionAsync(User("root", RoleNames.Admin), "server.everything", Server, Ct));
    }

    [Fact]
    public async Task TheGrantsAreCached_UntilTheUsersRolesChange()
    {
        Grant(PermissionKeys.ServerFirewallManage, Server, held: true);
        await _sut.HasPermissionAsync(User("bob"), PermissionKeys.ServerFirewallManage, Server, Ct);
        await _sut.HasPermissionAsync(User("bob"), PermissionKeys.ServerFirewallManage, Server, Ct);
        await _repo.Received(1).GetOrganizationRoleGrantsAsync("bob", Arg.Any<CancellationToken>());

        _sut.InvalidateRoleCache("bob");
        _repo.GetOrganizationRoleGrantsAsync("bob", Arg.Any<CancellationToken>()).Returns([]);

        Assert.False(await _sut.HasPermissionAsync(User("bob"), PermissionKeys.ServerFirewallManage, Server, Ct));
    }
}
