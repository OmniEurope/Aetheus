// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NSubstitute;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// Audit AUD-05 (recette R1-50) on real PostgreSQL: two requests that each remove the Admin role from
/// a different one of the last two administrators, each on its own context as in production. Without
/// the administrator-set lock both counted two administrators and both committed, leaving none.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AdministratorSetLockIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task ConcurrentRemovalsOfTheLastTwoAdministrators_LeaveOneAdministrator()
    {
        await ResetAndMigrateAsync();
        var ct = TestContext.Current.CancellationToken;
        var (roleId, firstId, secondId) = await SeedTwoAdministratorsAsync(ct);

        await using var first = NewContext();
        await using var second = NewContext();
        var outcomes = await Task.WhenAll(
            TryUnassignAsync(first, firstId, roleId, ct),
            TryUnassignAsync(second, secondId, roleId, ct));

        Assert.Single(outcomes, removed => removed);
        await using var verify = NewContext();
        var administrators = await verify.Users
            .CountAsync(u => u.IsActive && u.UserRoles.Any(ur => ur.Role.Name == "Admin"), ct);
        Assert.Equal(1, administrators);
    }

    private static async Task<bool> TryUnassignAsync(
        Aetheus.Back.Data.AppDbContext db, int userId, int roleId, CancellationToken ct)
    {
        var service = new UserService(
            new UserRepository(db),
            new HttpContextAccessor(),
            Substitute.For<IAuditService>(),
            Substitute.For<IResourceAuthorizationService>(),
            new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System,
            Substitute.For<IAdminChangeNotifier>(),
            Substitute.For<IUserChangeNotifier>(),
            Substitute.For<IDomainEventDispatcher>(),
            new OrganizationRoleAssignmentService(new OrganizationRoleAssignmentRepository(db)));
        try
        {
            return await service.UnassignRoleAsync(userId, roleId, null, ct);
        }
        catch (BadRequestException)
        {
            return false;
        }
    }

    private async Task<(int RoleId, int FirstId, int SecondId)> SeedTwoAdministratorsAsync(CancellationToken ct)
    {
        await using var seed = NewContext();
        var role = await seed.Roles.FirstOrDefaultAsync(r => r.Name == "Admin", ct);
        if (role is null)
        {
            role = new Role { Name = "Admin", Description = "Full access" };
            seed.Roles.Add(role);
        }
        // Only these two may be administrators, or the count would never reach the last one.
        var others = await seed.UserRoles.Where(ur => ur.Role.Name == "Admin").ToListAsync(ct);
        seed.UserRoles.RemoveRange(others);
        var users = Enumerable.Range(0, 2).Select(_ => Guid.NewGuid().ToString("N")).Select(id => new User
        {
            Username = $"admin-{id}",
            Email = $"admin-{id}@example.test",
            PasswordHash = "not-used",
            IsActive = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        }).ToList();
        seed.Users.AddRange(users);
        await seed.SaveChangesAsync(ct);
        foreach (var user in users)
            seed.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await seed.SaveChangesAsync(ct);
        return (role.Id, users[0].Id, users[1].Id);
    }
}
