// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.IntegrationTests;

/// <summary>
/// SITE-FUNC-ROLE-001: the users grid of a role searched, filtered and sorted on PostgreSQL. The
/// unit suite runs the repository on the in-memory provider, which evaluates a positional
/// <see cref="RoleUserDto"/> projection on the client; only Npgsql proves the query is translatable.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class RoleUsersGridIntegrationTests(PostgresFixture fixture) : RelationalTestBase(fixture)
{
    [Fact]
    public async Task UsersInRole_FilterSearchSortAndPage_AreTranslatedByNpgsql()
    {
        var ct = TestContext.Current.CancellationToken;
        var (roleId, _) = await SeedAsync(ct);
        await using var db = NewContext();
        var repo = new RoleRepository(db);

        var (active, activeTotal) = await repo.GetUsersInRolePagedAsync(
            roleId, null, 1, 10, "email", true, ct,
            [new GridFilter { Field = "isActive", Operator = GridFilterOperator.Equals, Value = bool.TrueString }]);
        Assert.Equal(2, activeTotal);
        Assert.Equal(["carol", "alice"], active.Select(user => user.Username));

        var (byEmail, byEmailTotal) = await repo.GetUsersInRolePagedAsync(
            roleId, null, 1, 10, "username", false, ct,
            [new GridFilter { Field = "email", Operator = GridFilterOperator.Contains, Value = "EXAMPLE" }]);
        Assert.Equal(2, byEmailTotal);
        Assert.Equal(["alice", "carol"], byEmail.Select(user => user.Username));

        var (searched, searchedTotal) = await repo.GetUsersInRolePagedAsync(
            roleId, "BO", 1, 10, "isactive", false, ct);
        Assert.Equal(1, searchedTotal);
        var bob = Assert.Single(searched);
        Assert.Equal(("bob", (string?)"bob@other.test", false), (bob.Username, bob.Email, bob.IsActive));

        var (secondPage, total) = await repo.GetUsersInRolePagedAsync(roleId, null, 2, 2, null, false, ct);
        Assert.Equal(3, total);
        Assert.Equal(["carol"], secondPage.Select(user => user.Username));
    }

    [Fact]
    public async Task UsersAvailableForRole_SearchSortAndPage_AreTranslatedByNpgsql()
    {
        var ct = TestContext.Current.CancellationToken;
        var (roleId, outsiderId) = await SeedAsync(ct);
        await using var db = NewContext();

        var (items, total) = await new RoleRepository(db).GetUsersAvailableForRolePagedAsync(
            roleId, "dave", 1, 10, "email", true, ct);

        Assert.Equal(1, total);
        var dave = Assert.Single(items);
        Assert.Equal((outsiderId, "dave", "dave@example.test", true), (dave.UserId, dave.Username, dave.Email, dave.IsActive));
    }

    private async Task<(int RoleId, int OutsiderId)> SeedAsync(CancellationToken ct)
    {
        await ResetAndMigrateAsync();
        await using var db = NewContext();
        var role = new Role { Name = $"grid-{Guid.NewGuid():N}", Description = "Role users grid" };
        var alice = new User { Username = "alice", Email = "alice@example.test", IsActive = true };
        var bob = new User { Username = "bob", Email = "bob@other.test", IsActive = false };
        var carol = new User { Username = "carol", Email = "carol@example.test", IsActive = true };
        var dave = new User { Username = "dave", Email = "dave@example.test", IsActive = true };
        var organization = new Organization { Name = $"grid-{Guid.NewGuid():N}", Slug = $"grid{Guid.NewGuid():N}"[..20] };
        db.AddRange(role, alice, bob, carol, dave, organization);
        await db.SaveChangesAsync(ct);
        // ADR-059: a role other than Admin is held for an organization.
        db.UserOrganizationRoles.AddRange(
            new UserOrganizationRole { UserId = alice.Id, OrganizationId = organization.Id, RoleId = role.Id },
            new UserOrganizationRole { UserId = bob.Id, OrganizationId = organization.Id, RoleId = role.Id },
            new UserOrganizationRole { UserId = carol.Id, OrganizationId = organization.Id, RoleId = role.Id });
        await db.SaveChangesAsync(ct);
        return (role.Id, dave.Id);
    }
}
