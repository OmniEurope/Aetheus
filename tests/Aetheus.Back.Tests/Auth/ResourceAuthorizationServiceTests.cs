// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Security.Claims;
using Aetheus.Back.Services;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class ResourceAuthorizationServiceTests
{
    private readonly IPermissionRepository _permissionRepoMock = Substitute.For<IPermissionRepository>();
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());
    private readonly AuthzCacheEvictor _evictor = new();
    private readonly ResourceAuthorizationService _sut;

    public ResourceAuthorizationServiceTests()
    {
        _sut = new ResourceAuthorizationService(_permissionRepoMock, _cache, _evictor);
    }

    private static ClaimsPrincipal CreateUser(string username, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.Name, username) };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public async Task HasPermissionAsync_AdminRole_ReturnsTrue()
    {
        var user = CreateUser("admin", "Admin");

        var result = await _sut.HasPermissionAsync(user, ResourceType.Vault, 1, Permission.Admin, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task HasPermissionAsync_NoUsername_ReturnsFalse()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _sut.HasPermissionAsync(user, ResourceType.Vault, 1, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task HasPermissionAsync_UserWithNoRoles_ReturnsFalse()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("noroles", Arg.Any<CancellationToken>())
            .Returns([]);

        var user = CreateUser("noroles");

        var result = await _sut.HasPermissionAsync(user, ResourceType.Vault, 1, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task HasPermissionAsync_UserWithPermission_ReturnsTrue()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("editor", Arg.Any<CancellationToken>())
            .Returns([1]);
        _permissionRepoMock.HasPermissionAsync(Arg.Any<List<int>>(), ResourceType.Vault, 5, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);

        var user = CreateUser("editor");

        var result = await _sut.HasPermissionAsync(user, ResourceType.Vault, 5, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task HasPermissionAsync_InsufficientPermission_ReturnsFalse()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("viewer", Arg.Any<CancellationToken>())
            .Returns([2]);
        _permissionRepoMock.HasPermissionAsync(Arg.Any<List<int>>(), ResourceType.Vault, 5, Permission.Write, Arg.Any<CancellationToken>())
            .Returns(false);

        var user = CreateUser("viewer");

        var result = await _sut.HasPermissionAsync(user, ResourceType.Vault, 5, Permission.Write, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task HasPermissionAsync_WildcardResourceId_MatchesAnyResource()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("globalreader", Arg.Any<CancellationToken>())
            .Returns([3]);
        _permissionRepoMock.HasPermissionAsync(Arg.Any<List<int>>(), ResourceType.VariableLibrary, 42, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);

        var user = CreateUser("globalreader");

        var result = await _sut.HasPermissionAsync(user, ResourceType.VariableLibrary, 42, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public async Task HasPermissionAsync_WrongResourceType_ReturnsFalse()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("vaultonly", Arg.Any<CancellationToken>())
            .Returns([4]);
        _permissionRepoMock.HasPermissionAsync(Arg.Any<List<int>>(), ResourceType.VariableLibrary, 1, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(false);

        var user = CreateUser("vaultonly");

        var result = await _sut.HasPermissionAsync(user, ResourceType.VariableLibrary, 1, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    // ADR-059 (R-46): a role held for organization 1 never reaches a resource organization 2 owns,
    // even when the role's wildcard would otherwise grant it.
    [Fact]
    public async Task HasPermissionAsync_RoleHeldForAnotherOrganization_IsDeniedBeforeWildcardRole()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("contributor", Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationRoleGrantsAsync("contributor", Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(1, 2, RoleNames.Contributor)]);
        _permissionRepoMock.GetResourceOrganizationIdAsync(ResourceType.PipelineTemplate, 9, Arg.Any<CancellationToken>())
            .Returns(2);
        _permissionRepoMock.HasPermissionAsync(
            Arg.Any<List<int>>(), ResourceType.PipelineTemplate, 9, Permission.Write,
            Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.HasPermissionAsync(
            CreateUser("contributor"), ResourceType.PipelineTemplate, 9, Permission.Write, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
        await _permissionRepoMock.DidNotReceive().HasPermissionAsync(
            Arg.Any<List<int>>(), ResourceType.PipelineTemplate, 9, Permission.Write,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task HasPermissionAsync_RoleHeldForOwningOrganization_StillRequiresWriteRole()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("contributor", Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationRoleGrantsAsync("contributor", Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(2, 7, RoleNames.Contributor)]);
        _permissionRepoMock.GetResourceOrganizationIdAsync(ResourceType.PipelineTemplate, 9, Arg.Any<CancellationToken>())
            .Returns(2);
        _permissionRepoMock.HasPermissionAsync(
            Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 7 })), ResourceType.PipelineTemplate, 9, Permission.Write,
            Arg.Any<CancellationToken>()).Returns(true);

        Assert.True(await _sut.HasPermissionAsync(
            CreateUser("contributor"), ResourceType.PipelineTemplate, 9, Permission.Write, ct: TestContext.Current.CancellationToken));
        Assert.False(await _sut.HasPermissionAsync(
            CreateUser("contributor"), ResourceType.PipelineTemplate, 9, Permission.Admin, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task HasPermissionAsync_ResourceWithoutOrganization_IsSiteAdminOnly()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("contributor", Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationRoleGrantsAsync("contributor", Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(2, 7, RoleNames.OrganizationAdmin)]);
        _permissionRepoMock.GetResourceOrganizationIdAsync(ResourceType.Vault, 3, Arg.Any<CancellationToken>())
            .Returns((int?)null);
        _permissionRepoMock.HasPermissionAsync(
            Arg.Any<List<int>>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(),
            Arg.Any<CancellationToken>()).Returns(true);

        Assert.False(await _sut.HasPermissionAsync(
            CreateUser("contributor"), ResourceType.Vault, 3, Permission.Read, ct: TestContext.Current.CancellationToken));
        Assert.False(await _sut.HasPermissionAsync(
            CreateUser("contributor"), ResourceType.AgentPool, 1, Permission.Read, ct: TestContext.Current.CancellationToken));
    }

    // --- GetAccessibleResourceIdsAsync ---

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_AdminRole_ReturnsNull()
    {
        var user = CreateUser("admin", "Admin");

        var result = await _sut.GetAccessibleResourceIdsAsync(user, ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_NoUsername_ReturnsEmpty()
    {
        var user = new ClaimsPrincipal(new ClaimsIdentity());

        var result = await _sut.GetAccessibleResourceIdsAsync(user, ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_NoRoles_ReturnsEmpty()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("noroles", Arg.Any<CancellationToken>())
            .Returns([]);

        var user = CreateUser("noroles");

        var result = await _sut.GetAccessibleResourceIdsAsync(user, ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_WithRoles_ReturnsIds()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("editor", Arg.Any<CancellationToken>())
            .Returns([1]);
        _permissionRepoMock.GetAccessibleResourceIdsAsync(
                Arg.Any<List<int>>(), ResourceType.Server, Permission.Read, Arg.Any<CancellationToken>())
            .Returns([10, 20, 30]);

        var user = CreateUser("editor");

        var result = await _sut.GetAccessibleResourceIdsAsync(user, ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(3, result.Count);
    }

    // R-45 (ADR-059): membership alone grants nothing; the rights come from the role a member holds.
    [Fact]
    public async Task GetAccessibleResourceIdsAsync_MembershipWithoutRole_GrantsNothing()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("member", Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationRoleGrantsAsync("member", Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationIdsForUsernameAsync("member", Arg.Any<CancellationToken>())
            .Returns([4]);

        var result = await _sut.GetAccessibleResourceIdsAsync(
            CreateUser("member"), ResourceType.Server, Permission.Read,
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Empty(result);
        await _permissionRepoMock.DidNotReceive().GetResourceIdsByOrganizationsAsync(
            ResourceType.Server, Arg.Any<List<int>>(), Arg.Any<CancellationToken>());
    }

    // A role held for organization 4 that names a resource of another organization grants nothing.
    [Fact]
    public async Task GetAccessibleResourceIdsAsync_NamedResourceOfAnotherOrganization_IsDropped()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("reader", Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationRoleGrantsAsync("reader", Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(4, 3, "Custom")]);
        _permissionRepoMock.GetAccessibleResourceIdsAsync(
            Arg.Any<List<int>>(), ResourceType.Server, Permission.Read,
            Arg.Any<CancellationToken>()).Returns([10, 20]);
        _permissionRepoMock.GetResourceIdsByOrganizationsAsync(
            ResourceType.Server, Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 4 })),
            Arg.Any<CancellationToken>()).Returns([10, 11]);

        var result = await _sut.GetAccessibleResourceIdsAsync(
            CreateUser("reader"), ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.Equal([10], result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_TemplateWildcard_IsRestrictedToTheRoleOrganization()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("reader", Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationRoleGrantsAsync("reader", Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(4, 3, RoleNames.Reader)]);
        _permissionRepoMock.GetAccessibleResourceIdsAsync(
            Arg.Any<List<int>>(), ResourceType.PipelineTemplate, Permission.Read,
            Arg.Any<CancellationToken>()).Returns((List<int>?)null);
        _permissionRepoMock.GetResourceIdsByOrganizationsAsync(
            ResourceType.PipelineTemplate, Arg.Is<List<int>>(ids => ids.SequenceEqual(new[] { 4 })),
            Arg.Any<CancellationToken>()).Returns([10, 11]);

        var result = await _sut.GetAccessibleResourceIdsAsync(
            CreateUser("reader"), ResourceType.PipelineTemplate, Permission.Read, ct: TestContext.Current.CancellationToken);

        Assert.Equal([10, 11], result);
    }

    [Fact]
    public async Task HasPermissionAsync_CachesRoleIds()
    {
        _permissionRepoMock.GetRoleIdsForUserAsync("cached", Arg.Any<CancellationToken>())
            .Returns([1]);
        _permissionRepoMock.HasPermissionAsync(Arg.Any<List<int>>(), Arg.Any<ResourceType>(), Arg.Any<int?>(), Arg.Any<Permission>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var user = CreateUser("cached");

        await _sut.HasPermissionAsync(user, ResourceType.Vault, 1, Permission.Read, ct: TestContext.Current.CancellationToken);
        await _sut.HasPermissionAsync(user, ResourceType.Vault, 2, Permission.Read, ct: TestContext.Current.CancellationToken);

        await _permissionRepoMock.Received(1).GetRoleIdsForUserAsync("cached", Arg.Any<CancellationToken>());
    }

    // --- Organization-role cache coherence (a removed member must lose its rights on invalidation) ---

    private void GrantServerReadInOrganization(string username)
    {
        _permissionRepoMock.GetRoleIdsForUserAsync(username, Arg.Any<CancellationToken>()).Returns([]);
        _permissionRepoMock.GetOrganizationRoleGrantsAsync(username, Arg.Any<CancellationToken>())
            .Returns([new OrganizationRoleGrant(4, 3, RoleNames.Reader)]);
        _permissionRepoMock.GetResourceOrganizationIdAsync(ResourceType.Server, 5, Arg.Any<CancellationToken>())
            .Returns(4);
        _permissionRepoMock.HasPermissionAsync(
            Arg.Any<List<int>>(), ResourceType.Server, 5, Permission.Read, Arg.Any<CancellationToken>())
            .Returns(true);
    }

    [Fact]
    public async Task HasPermissionAsync_CachesOrganizationRoles()
    {
        GrantServerReadInOrganization("member");
        var user = CreateUser("member");

        await _sut.HasPermissionAsync(user, ResourceType.Server, 5, Permission.Read, ct: TestContext.Current.CancellationToken);
        await _sut.HasPermissionAsync(user, ResourceType.Server, 5, Permission.Read, ct: TestContext.Current.CancellationToken);

        await _permissionRepoMock.Received(1)
            .GetOrganizationRoleGrantsAsync("member", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateRoleCache_EvictsOrganizationRoleCache()
    {
        GrantServerReadInOrganization("member");
        var user = CreateUser("member");

        // First read caches the role held for organization 4.
        Assert.True(await _sut.HasPermissionAsync(user, ResourceType.Server, 5, Permission.Read, ct: TestContext.Current.CancellationToken));
        await _permissionRepoMock.Received(1)
            .GetOrganizationRoleGrantsAsync("member", Arg.Any<CancellationToken>());

        // Removing the member invalidates the cache; the previously-cached roles must be dropped.
        _sut.InvalidateRoleCache("member");

        await _sut.HasPermissionAsync(user, ResourceType.Server, 5, Permission.Read, ct: TestContext.Current.CancellationToken);
        await _permissionRepoMock.Received(2)
            .GetOrganizationRoleGrantsAsync("member", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateRoleCache_EvictsAcrossServiceInstances()
    {
        // Production reality: the request that POPULATES the authz cache and the request that INVALIDATES
        // it run in different DI scopes = different ResourceAuthorizationService instances. The eviction
        // must still cross that boundary, which only works because the eviction tokens live in the SHARED
        // singleton AuthzCacheEvictor (a per-instance dictionary would leave the orgroles entry stranded).
        GrantServerReadInOrganization("member");
        var user = CreateUser("member");

        // Instance A (scope 1) caches the role held for organization 4.
        var reader = new ResourceAuthorizationService(_permissionRepoMock, _cache, _evictor);
        Assert.True(await reader.HasPermissionAsync(user, ResourceType.Server, 5, Permission.Read, ct: TestContext.Current.CancellationToken));

        // Instance B (scope 2, e.g. the OrganizationService handling the removal) invalidates.
        var invalidator = new ResourceAuthorizationService(_permissionRepoMock, _cache, _evictor);
        invalidator.InvalidateRoleCache("member");

        // Instance A must now re-query the repository - the stale roles are gone across scopes.
        await reader.HasPermissionAsync(user, ResourceType.Server, 5, Permission.Read, ct: TestContext.Current.CancellationToken);
        await _permissionRepoMock.Received(2)
            .GetOrganizationRoleGrantsAsync("member", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AuthzCacheEvictor_Expiration_RemovesDormantUsername()
    {
        var evictor = new AuthzCacheEvictor();
        // One second, not 20 ms: on a loaded machine a 20 ms token expired before the first assertion
        // could see it tracked (failed once on 2026-09-25, passed alone right after).
        var token = evictor.TokenFor("one-shot-user", TimeSpan.FromSeconds(1));

        Assert.Equal(1, evictor.TrackedUserCount);
        // Cancellation and registered callbacks are observed on separate thread-pool turns. A fixed
        // sleep races on a loaded CI runner: HasChanged can already be true while the dictionary-removal
        // callback is still queued. Wait for the behavior under test with a strict upper bound instead.
        var wait = Stopwatch.StartNew();
        while (evictor.TrackedUserCount != 0 && wait.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.True(token.HasChanged);
        Assert.Equal(0, evictor.TrackedUserCount);
    }
}
