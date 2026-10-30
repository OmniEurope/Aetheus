// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class PermissionRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PermissionRepository _repo;

    public PermissionRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PermissionRepository(_db);
    }

    [Fact]
    public async Task GetRoleIdsForUserAsync_ReturnsTheSiteWideAdminRoleOnly()
    {
        var role1 = new Role { Name = "Admin", Description = "d" };
        var role2 = new Role { Name = "User", Description = "d" };
        _db.Roles.AddRange(role1, role2);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var user = new User { Username = "testuser", PasswordHash = "h", IsActive = true };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.UserRoles.AddRange(
            new UserRole { UserId = user.Id, RoleId = role1.Id },
            new UserRole { UserId = user.Id, RoleId = role2.Id }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRoleIdsForUserAsync("testuser", ct: TestContext.Current.CancellationToken);
        // ADR-059: a role other than Admin held without an organization grants nothing.
        Assert.Equal([role1.Id], result);
    }

    [Fact]
    public async Task GetRoleIdsForUserAsync_InactiveUser_ReturnsEmpty()
    {
        var role = new Role { Name = "Admin", Description = "d" };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var user = new User { Username = "inactive", PasswordHash = "h", IsActive = false };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRoleIdsForUserAsync("inactive", ct: TestContext.Current.CancellationToken);
        Assert.Empty(result);
    }

    [Fact]
    public async Task HasPermissionAsync_WithMatchingPermission_ReturnsTrue()
    {
        var role = new Role { Name = "Admin", Description = "d" };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.ResourcePermissions.Add(new ResourcePermission
        {
            RoleId = role.Id,
            ResourceType = ResourceType.Server,
            ResourceId = 1,
            Permission = Permission.Admin
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.HasPermissionAsync([role.Id], ResourceType.Server, 1, Permission.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(result);
    }

    [Fact]
    public async Task HasPermissionAsync_WithWildcard_ReturnsTrue()
    {
        var role = new Role { Name = "Admin", Description = "d" };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.ResourcePermissions.Add(new ResourcePermission
        {
            RoleId = role.Id,
            ResourceType = ResourceType.Server,
            ResourceId = null,
            Permission = Permission.Admin
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.HasPermissionAsync([role.Id], ResourceType.Server, 42, Permission.Read, ct: TestContext.Current.CancellationToken);
        Assert.True(result);
    }

    [Fact]
    public async Task HasPermissionAsync_NoPermission_ReturnsFalse()
    {
        var result = await _repo.HasPermissionAsync([999], ResourceType.Server, 1, Permission.Read, ct: TestContext.Current.CancellationToken);
        Assert.False(result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_WithWildcard_ReturnsNull()
    {
        var role = new Role { Name = "Admin", Description = "d" };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.ResourcePermissions.Add(new ResourcePermission
        {
            RoleId = role.Id,
            ResourceType = ResourceType.Server,
            ResourceId = null,
            Permission = Permission.Admin
        });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetAccessibleResourceIdsAsync([role.Id], ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_WithSpecificIds_ReturnsIds()
    {
        var role = new Role { Name = "User", Description = "d" };
        _db.Roles.Add(role);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _db.ResourcePermissions.AddRange(
            new ResourcePermission { RoleId = role.Id, ResourceType = ResourceType.Server, ResourceId = 1, Permission = Permission.Read },
            new ResourcePermission { RoleId = role.Id, ResourceType = ResourceType.Server, ResourceId = 2, Permission = Permission.Read }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetAccessibleResourceIdsAsync([role.Id], ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetAccessibleResourceIdsAsync_NoPermissions_ReturnsEmpty()
    {
        var result = await _repo.GetAccessibleResourceIdsAsync([999], ResourceType.Server, Permission.Read, ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Empty(result);
    }

    public void Dispose() => _db.Dispose();
}
