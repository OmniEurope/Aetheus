// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class PermissionRepositoryOrgTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PermissionRepository _repo;

    public PermissionRepositoryOrgTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PermissionRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private async Task SeedRoleHeldForOrganizationAsync(bool member, bool active = true)
    {
        _db.Users.Add(new User { Id = 1, Username = "bob", IsActive = active });
        _db.Organizations.AddRange(
            new Organization { Id = 5, Name = "A", Slug = "a" },
            new Organization { Id = 6, Name = "B", Slug = "b" });
        _db.Roles.Add(new Role { Id = 3, Name = RoleNames.Reader });
        _db.UserOrganizationRoles.Add(new UserOrganizationRole { Id = 1, UserId = 1, OrganizationId = 5, RoleId = 3 });
        if (member)
            _db.OrganizationMembers.Add(new OrganizationMember { Id = 1, UserId = 1, OrganizationId = 5, Role = OrganizationRole.Member });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task GetOrganizationRoleGrantsAsync_Member_ReturnsTheRoleForItsOrganization()
    {
        await SeedRoleHeldForOrganizationAsync(member: true);

        var grants = await _repo.GetOrganizationRoleGrantsAsync("bob", TestContext.Current.CancellationToken);

        Assert.Equal([new OrganizationRoleGrant(5, 3, RoleNames.Reader)], grants);
    }

    [Fact]
    public async Task GetOrganizationRoleGrantsAsync_NoLongerMember_ReturnsNothing()
    {
        await SeedRoleHeldForOrganizationAsync(member: false);

        Assert.Empty(await _repo.GetOrganizationRoleGrantsAsync("bob", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetOrganizationRoleGrantsAsync_InactiveUser_ReturnsNothing()
    {
        await SeedRoleHeldForOrganizationAsync(member: true, active: false);

        Assert.Empty(await _repo.GetOrganizationRoleGrantsAsync("bob", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRoleIdsForUserAsync_HonoursTheSiteWideAdminRoleOnly()
    {
        _db.Users.Add(new User { Id = 1, Username = "bob", IsActive = true });
        _db.Roles.AddRange(new Role { Id = 1, Name = RoleNames.Admin }, new Role { Id = 3, Name = RoleNames.Reader });
        _db.UserRoles.AddRange(new UserRole { UserId = 1, RoleId = 1 }, new UserRole { UserId = 1, RoleId = 3 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal([1], await _repo.GetRoleIdsForUserAsync("bob", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetResourceOrganizationIdAsync_ServerAndMissingResource()
    {
        _db.Servers.Add(new Server { Id = 10, Name = "web", Hostname = "web.local", OrganizationId = 5, Status = ServerStatus.Online });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(5, await _repo.GetResourceOrganizationIdAsync(ResourceType.Server, 10, TestContext.Current.CancellationToken));
        Assert.Null(await _repo.GetResourceOrganizationIdAsync(ResourceType.Project, 999, TestContext.Current.CancellationToken));
        Assert.Null(await _repo.GetResourceOrganizationIdAsync(ResourceType.AgentPool, 1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetResourceIdsByOrganizationsAsync_EmptyOrgs_ReturnsEmpty()
        => Assert.Empty(await _repo.GetResourceIdsByOrganizationsAsync(ResourceType.Server, [], ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetResourceIdsByOrganizationsAsync_Server_ReturnsIdsInOrgs()
    {
        _db.Servers.AddRange(
            new Server { Id = 10, Name = "a", Hostname = "a", OrganizationId = 5, Status = ServerStatus.Online },
            new Server { Id = 11, Name = "b", Hostname = "b", OrganizationId = 6, Status = ServerStatus.Online },
            new Server { Id = 12, Name = "c", Hostname = "c", OrganizationId = 9, Status = ServerStatus.Online });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.GetResourceIdsByOrganizationsAsync(ResourceType.Server, [5, 6], ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, ids.Count);
        Assert.DoesNotContain(12, ids);
    }

    [Fact]
    public async Task GetResourceIdsByOrganizationsAsync_Project_ReturnsIdsInOrgs()
    {
        _db.Projects.AddRange(
            new Project { Id = 1, Name = "p1", OrganizationId = 5 },
            new Project { Id = 2, Name = "p2", OrganizationId = 7 });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var ids = await _repo.GetResourceIdsByOrganizationsAsync(ResourceType.Project, [5], ct: TestContext.Current.CancellationToken);

        Assert.Single(ids);
        Assert.Contains(1, ids);
    }

    [Fact]
    public async Task GetResourceIdsByOrganizationsAsync_TypeNoOrganizationOwns_ReturnsEmpty()
        => Assert.Empty(await _repo.GetResourceIdsByOrganizationsAsync(ResourceType.AgentPool, [5], ct: TestContext.Current.CancellationToken));
}
