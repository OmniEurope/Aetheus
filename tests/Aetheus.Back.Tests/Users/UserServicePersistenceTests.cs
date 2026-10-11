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
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>());
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
    public async Task UpdateUserAsync_AddingRole_PreservesTrackedRoleAndPersistsNewRole()
    {
        var (user, _) = await SeedUserAsync();

        var result = await _service.UpdateUserAsync(user.Id, new UpdateUserRequest
        {
            Username = user.Username,
            Email = user.Email,
            IsActive = true,
            Roles = ["Admin", "Viewer"]
        }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(["Admin", "Viewer"], result.Roles.OrderBy(role => role));
        Assert.Equal(2, await _db.UserRoles.CountAsync(TestContext.Current.CancellationToken));
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
