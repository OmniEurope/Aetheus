// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.Tests.Users;

public sealed class UserServicePersistenceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly UserService _service;

    public UserServicePersistenceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        _db = new AppDbContext(options);
        _service = new UserService(
            new UserRepository(_db),
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<IAuditService>(),
            Substitute.For<IResourceAuthorizationService>(),
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Substitute.For<IAdminChangeNotifier>(),
            Substitute.For<IUserChangeNotifier>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(),
            new Aetheus.Back.Services.OrganizationRoleAssignmentService(
                new Aetheus.Back.Services.OrganizationRoleAssignmentRepository(_db)));
    }

    [Fact]
    public async Task UpdateUserAsync_WithUnchangedRoles_DoesNotCreateTrackingConflict()
    {
        var (user, _) = await SeedUserAsync();

        var result = await _service.UpdateUserAsync(user.Id, new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = true,
            Roles = ["Admin"]
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["Admin"], result.Roles);
        Assert.Single(await _db.UserRoles.ToListAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateUserAsync_AddingRoleForAnOrganization_PreservesTrackedRoleAndMakesTheUserAMember()
    {
        var (user, _) = await SeedUserAsync();
        _db.Organizations.Add(new Organization { Id = 7, Name = "Acme", Slug = "acme" });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();

        var result = await _service.UpdateUserAsync(user.Id, new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = true,
            Roles = ["Admin"],
            OrganizationRoles = [new OrganizationRoleAssignmentDto { OrganizationId = 7, Role = "Viewer" }]
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["Admin"], result.Roles);
        Assert.Equal(1, await _db.UserRoles.CountAsync(TestContext.Current.CancellationToken));
        // R-47: the role is held for organization 7, and holding it made the user a member.
        var assignment = Assert.Single(await _db.UserOrganizationRoles.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal(7, assignment.OrganizationId);
        var membership = Assert.Single(await _db.OrganizationMembers.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Equal((user.Id, 7), (membership.UserId, membership.OrganizationId));
    }

    [Fact]
    public async Task UpdateUserAsync_RoleOtherThanAdminHeldSiteWide_IsRefusedAndChangesNothing()
    {
        var (user, _) = await SeedUserAsync();

        await Assert.ThrowsAsync<Aetheus.Back.Exceptions.BadRequestException>(() => _service.UpdateUserAsync(user.Id, new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = true,
            Roles = ["Admin", "Viewer"]
        }, TestContext.Current.CancellationToken));

        Assert.Equal(1, await _db.UserRoles.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateUserAsync_KeepsTheInertLegacySiteWideRow_OfARoleOtherThanAdmin()
    {
        // M1 (ADR-059): the legacy UserRoles row of a non-Admin role does nothing any more, but the
        // migration's Down needs it; editing the user must leave it in place.
        var (user, _) = await SeedUserAsync();
        var viewerId = await _db.Roles.Where(role => role.Name == "Viewer").Select(role => role.Id).SingleAsync(TestContext.Current.CancellationToken);
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = viewerId });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();

        await _service.UpdateUserAsync(user.Id, new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = true,
            Roles = ["Admin"]
        }, TestContext.Current.CancellationToken);

        Assert.Contains(await _db.UserRoles.ToListAsync(TestContext.Current.CancellationToken), row => row.RoleId == viewerId);
    }

    [Fact]
    public async Task UpdateUserAsync_RemovingAdmin_RemovesOnlyTheAdminRow()
    {
        var (user, admin) = await SeedUserAsync();
        var viewerId = await _db.Roles.Where(role => role.Name == "Viewer").Select(role => role.Id).SingleAsync(TestContext.Current.CancellationToken);
        var otherAdmin = new User { Username = "other-admin", PasswordHash = "hash", IsActive = true };
        _db.Users.Add(otherAdmin);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.UserRoles.AddRange(
            new UserRole { UserId = user.Id, RoleId = viewerId },
            new UserRole { UserId = otherAdmin.Id, RoleId = admin.Id });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();

        await _service.UpdateUserAsync(user.Id, new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = true,
            Roles = []
        }, TestContext.Current.CancellationToken);

        var rows = await _db.UserRoles.Where(row => row.UserId == user.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal([viewerId], rows.Select(row => row.RoleId));
    }

    private async Task<(User User, Role Admin)> SeedUserAsync()
    {
        var admin = new Role { Name = "Admin", Description = "Administrator" };
        var viewer = new Role { Name = "Viewer", Description = "Viewer" };
        var user = new User
        {
            Username = "tracked-user",
            PasswordHash = "hash",
            Email = "tracked@example.test",
            IsActive = true
        };
        _db.AddRange(admin, viewer, user);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = admin.Id });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
        _db.ChangeTracker.Clear();
        return (user, admin);
    }

    public void Dispose() => _db.Dispose();
}
