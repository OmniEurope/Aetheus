// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.PersonalAccessTokens;

public class PersonalAccessTokenRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly PersonalAccessTokenRepository _repo;
    private readonly DateTime _now = new(2026, 7, 9, 12, 0, 0, DateTimeKind.Utc);

    public PersonalAccessTokenRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new PersonalAccessTokenRepository(_db);

        var role = new Role { Id = 1, Name = "Admin" };
        var active = new User { Id = 1, Username = "alice", IsActive = true, UserRoles = [new UserRole { UserId = 1, RoleId = 1, Role = role }] };
        var inactive = new User { Id = 2, Username = "mallory", IsActive = false, UserRoles = [] };
        _db.Roles.Add(role);
        _db.Users.AddRange(active, inactive);

        _db.PersonalAccessTokens.AddRange(
            new PersonalAccessToken { Id = 10, UserId = 1, Name = "valid", TokenHash = "hash-valid", TokenPrefix = "aeth_pat_aa", Scope = PatScope.ReadOnly, CreatedAt = _now.AddDays(-3), ExpiresAt = _now.AddDays(10) },
            new PersonalAccessToken { Id = 11, UserId = 1, Name = "revoked", TokenHash = "hash-revoked", TokenPrefix = "aeth_pat_bb", Scope = PatScope.ReadWrite, CreatedAt = _now.AddDays(-2), ExpiresAt = _now.AddDays(10), RevokedAt = _now.AddDays(-1) },
            new PersonalAccessToken { Id = 12, UserId = 1, Name = "expired", TokenHash = "hash-expired", TokenPrefix = "aeth_pat_cc", Scope = PatScope.ReadWrite, CreatedAt = _now.AddDays(-5), ExpiresAt = _now.AddDays(-1) },
            new PersonalAccessToken { Id = 13, UserId = 2, Name = "inactive-owner", TokenHash = "hash-inactive", TokenPrefix = "aeth_pat_dd", Scope = PatScope.ReadWrite, CreatedAt = _now.AddDays(-1), ExpiresAt = _now.AddDays(10) }
        );
        _db.SaveChanges();
    }

    [Fact]
    public async Task FindActiveByHash_ReturnsActiveToken_WithUserAndRoles()
    {
        var token = await _repo.FindActiveByHashWithUserAsync("hash-valid", _now, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(token);
        Assert.Equal(10, token.Id);
        Assert.Equal("alice", token.User.Username);
        Assert.Equal("Admin", token.User.UserRoles.Single().Role.Name);
    }

    [Fact]
    public async Task FindActiveByHash_ExcludesRevoked()
        => Assert.Null(await _repo.FindActiveByHashWithUserAsync("hash-revoked", _now, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task FindActiveByHash_ExcludesExpired()
        => Assert.Null(await _repo.FindActiveByHashWithUserAsync("hash-expired", _now, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task FindActiveByHash_ExcludesTokenOfInactiveUser()
        => Assert.Null(await _repo.FindActiveByHashWithUserAsync("hash-inactive", _now, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task FindActiveByHash_UnknownHash_ReturnsNull()
        => Assert.Null(await _repo.FindActiveByHashWithUserAsync("nope", _now, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task GetForUser_ReturnsOwnTokensNewestFirst()
    {
        var tokens = await _repo.GetForUserAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, tokens.Count); // ids 10,11,12 belong to user 1; 13 is user 2
        Assert.All(tokens, t => Assert.Equal(1, t.UserId));
        Assert.True(tokens[0].CreatedAt >= tokens[1].CreatedAt);
    }

    [Fact]
    public async Task GetForUserPaged_ReturnsRequestedPageAndOwnTotal()
    {
        var (tokens, total) = await _repo.GetForUserPagedAsync(
            userId: 1,
            search: null,
            page: 2,
            pageSize: 1,
            sortBy: "CreatedAt",
            sortDescending: true, ct: TestContext.Current.CancellationToken);

        Assert.Equal(3, total);
        var token = Assert.Single(tokens);
        Assert.Equal(11, token.Id);
        Assert.Equal(1, token.UserId);
    }

    [Fact]
    public async Task GetForUserPaged_AppliesColumnFilters_InsideTheOwnersTokens()
    {
        // The scope list and the expiry range are column filters; the owner scope stays.
        var (tokens, total) = await _repo.GetForUserPagedAsync(
            userId: 1, search: null, page: 1, pageSize: 10, sortBy: null, sortDescending: true,
            ct: TestContext.Current.CancellationToken,
            columnFilters:
            [
                new GridFilter { Field = "Scope", Operator = GridFilterOperator.In, Value = "ReadWrite" },
                new GridFilter { Field = "ExpiresAt", Operator = GridFilterOperator.GreaterThanOrEqual, Value = "2026-07-09T12:00:00Z" }
            ]);

        Assert.Equal(1, total);
        Assert.Equal(11, Assert.Single(tokens).Id);
    }

    [Fact]
    public async Task FindByIdForUser_WrongOwner_ReturnsNull()
        => Assert.Null(await _repo.FindByIdForUserAsync(13, userId: 1, ct: TestContext.Current.CancellationToken));

    [Fact]
    public async Task TouchLastUsed_UpdatesTimestamp()
    {
        await _repo.TouchLastUsedAsync(10, _now, ct: TestContext.Current.CancellationToken);

        var reloaded = await _repo.FindByIdForUserAsync(10, 1, ct: TestContext.Current.CancellationToken);
        Assert.Equal(_now, reloaded!.LastUsedAt);
    }

    public void Dispose() => _db.Dispose();
}
