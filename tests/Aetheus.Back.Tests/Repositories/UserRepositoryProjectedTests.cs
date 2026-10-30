// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Users;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class UserRepositoryProjectedTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly UserRepository _repo;

    public UserRepositoryProjectedTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new UserRepository(_db);
    }

    public void Dispose() => _db.Dispose();

    private void SeedUser(int id, string username, string email, params string[] roles)
    {
        var user = new User { Id = id, Username = username, Email = email, IsActive = true };
        _db.Users.Add(user);
        foreach (var roleName in roles)
        {
            var role = new Role { Id = id * 100 + roleName.GetHashCode() % 50, Name = roleName };
            _db.Roles.Add(role);
            _db.UserRoles.Add(new UserRole { UserId = id, RoleId = role.Id, Role = role });
        }
        _db.SaveChanges();
    }

    [Fact]
    public async Task GetUsersPagedProjectedAsync_NoSearch_ReturnsAllOrderedByUsername()
    {
        SeedUser(1, "zoe", "zoe@x.com");
        SeedUser(2, "amy", "amy@x.com", "Admin");

        var (items, total) = await _repo.GetUsersPagedProjectedAsync(null, 1, 10, ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, total);
        Assert.Equal("amy", items[0].Username);
        Assert.Contains("Admin", items[0].Roles);
    }

    [Fact]
    public async Task GetUsersPagedProjectedAsync_Search_FiltersByUsernameOrEmail()
    {
        SeedUser(1, "alice", "alice@corp.com");
        SeedUser(2, "bob", "bob@other.com");

        var (byName, _) = await _repo.GetUsersPagedProjectedAsync("alice", 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Single(byName);

        var (byEmail, _) = await _repo.GetUsersPagedProjectedAsync("other.com", 1, 10, ct: TestContext.Current.CancellationToken);
        Assert.Single(byEmail);
        Assert.Equal("bob", byEmail[0].Username);
    }

    [Fact]
    public async Task GetUsersPagedProjectedAsync_Paginates()
    {
        for (var i = 1; i <= 5; i++) SeedUser(i, $"user{i:D2}", $"u{i}@x.com");

        var (page1, total) = await _repo.GetUsersPagedProjectedAsync(null, 1, 2, ct: TestContext.Current.CancellationToken);

        Assert.Equal(5, total);
        Assert.Equal(2, page1.Count);
    }
}
