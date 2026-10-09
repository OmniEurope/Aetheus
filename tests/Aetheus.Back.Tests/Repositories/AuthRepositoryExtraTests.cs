// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests;

public class AuthRepositoryExtraTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AuthRepository _repo;

    public AuthRepositoryExtraTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new AuthRepository(_db, TimeProvider.System);
    }

    public void Dispose() => _db.Dispose();

    private User SeedUser(int id = 1, bool active = true, string stamp = "stamp-1", int failed = 0, DateTime? lockout = null)
    {
        var u = new User
        {
            Id = id,
            Username = $"user{id}",
            IsActive = active,
            SecurityStamp = stamp,
            FailedLoginCount = failed,
            LockoutEndUtc = lockout,
            UserRoles = []
        };
        _db.Users.Add(u);
        _db.SaveChanges();
        return u;
    }

    [Fact]
    public async Task FindUserByIdWithRolesAsync_ActiveFound_InactiveOrMissingNull()
    {
        SeedUser(1, active: true);
        SeedUser(2, active: false);

        Assert.NotNull(await _repo.FindUserByIdWithRolesAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindUserByIdWithRolesAsync(2, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindUserByIdWithRolesAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetSecurityStampAsync_ReturnsStampOrNull()
    {
        SeedUser(1, stamp: "abc");

        Assert.Equal("abc", await _repo.GetSecurityStampAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.GetSecurityStampAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindUserByIdTrackedAsync_FoundAndMissing()
    {
        SeedUser(1);

        Assert.NotNull(await _repo.FindUserByIdTrackedAsync(1, ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindUserByIdTrackedAsync(99, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindRefreshTokenByHashAsync_FoundAndMissing()
    {
        SeedUser(1);
        _db.RefreshTokens.Add(new RefreshToken { Id = 1, UserId = 1, TokenHash = "hash-x", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.NotNull(await _repo.FindRefreshTokenByHashAsync("hash-x", ct: TestContext.Current.CancellationToken));
        Assert.Null(await _repo.FindRefreshTokenByHashAsync("nope", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ClearLockoutAsync_ResetsFailedCountAndLockout()
    {
        var u = SeedUser(1, failed: 5, lockout: DateTime.UtcNow.AddMinutes(30));

        await _repo.ClearLockoutAsync(1, ct: TestContext.Current.CancellationToken);
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);

        var reloaded = await _db.Users.AsNoTracking().FirstAsync(x => x.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, reloaded.FailedLoginCount);
        Assert.Null(reloaded.LockoutEndUtc);
    }

    [Fact]
    public async Task LockoutUserAsync_SetsLockoutEnd()
    {
        SeedUser(1);
        var until = DateTime.UtcNow.AddMinutes(15);

        await _repo.LockoutUserAsync(1, until, ct: TestContext.Current.CancellationToken);
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);

        var reloaded = await _db.Users.AsNoTracking().FirstAsync(x => x.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(reloaded.LockoutEndUtc);
    }

    [Fact]
    public async Task BumpSecurityStampAsync_ChangesStamp()
    {
        SeedUser(1, stamp: "old-stamp");

        await _repo.BumpSecurityStampAsync(1, ct: TestContext.Current.CancellationToken);
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);

        var reloaded = await _db.Users.AsNoTracking().FirstAsync(x => x.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotEqual("old-stamp", reloaded.SecurityStamp);
    }

    [Fact]
    public async Task RevokeAllUserRefreshTokensAsync_RevokesActiveTokens()
    {
        SeedUser(1);
        _db.RefreshTokens.Add(new RefreshToken { Id = 1, UserId = 1, TokenHash = "a", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        _db.RefreshTokens.Add(new RefreshToken { Id = 2, UserId = 1, TokenHash = "b", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RevokeAllUserRefreshTokensAsync(1, ct: TestContext.Current.CancellationToken);
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);

        var stillActive = await _db.RefreshTokens.AsNoTracking().CountAsync(r => r.RevokedAt == null, cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(0, stillActive);
    }

    [Fact]
    public async Task RevokeRefreshTokenAsync_MarksRevokedWithReplacement()
    {
        SeedUser(1);
        _db.RefreshTokens.Add(new RefreshToken { Id = 1, UserId = 1, TokenHash = "a", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RevokeRefreshTokenAsync(1, 99, ct: TestContext.Current.CancellationToken);
        await _repo.SaveChangesAsync(ct: TestContext.Current.CancellationToken);

        var token = await _db.RefreshTokens.AsNoTracking().FirstAsync(r => r.Id == 1, cancellationToken: TestContext.Current.CancellationToken);
        Assert.NotNull(token.RevokedAt);
        Assert.Equal(99, token.ReplacedById);
    }
}
