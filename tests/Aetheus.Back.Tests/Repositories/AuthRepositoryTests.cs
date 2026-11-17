// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Aetheus.Back.Tests.Repositories;

public class AuthRepositoryTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly AuthRepository _repo;

    public AuthRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new AppDbContext(options);
        _repo = new AuthRepository(_db, TimeProvider.System);
    }

    [Fact]
    public async Task FindValidRegistrationTokenAsync_ValidToken_ReturnsToken()
    {
        _db.RegistrationTokens.Add(new RegistrationToken { Token = "abc", IsUsed = false, ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindValidRegistrationTokenAsync(["abc"], ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindValidRegistrationTokenAsync_UsedToken_ReturnsNull()
    {
        _db.RegistrationTokens.Add(new RegistrationToken { Token = "abc", IsUsed = true, ExpiresAt = DateTime.UtcNow.AddHours(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.FindValidRegistrationTokenAsync(["abc"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindValidRegistrationTokenAsync_ExpiredToken_ReturnsNull()
    {
        _db.RegistrationTokens.Add(new RegistrationToken { Token = "abc", IsUsed = false, ExpiresAt = DateTime.UtcNow.AddHours(-1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.FindValidRegistrationTokenAsync(["abc"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindServerByHostnameAsync_Found()
    {
        _db.Servers.Add(new Server { Name = "srv", Hostname = "srv.local" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindServerByHostnameAsync("srv.local", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task FindServerByHostnameAsync_NotFound()
    {
        Assert.Null(await _repo.FindServerByHostnameAsync("notexist", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindUserWithRolesAsync_ActiveUser_ReturnsWithRoles()
    {
        var role = new Role { Name = "Admin", Description = "" };
        _db.Roles.Add(role);
        var user = new User { Username = "alice", PasswordHash = "x", IsActive = true };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.Set<UserRole>().Add(new UserRole { UserId = user.Id, RoleId = role.Id });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindUserWithRolesAsync("alice", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Single(result.UserRoles);
        Assert.Equal("Admin", result.UserRoles[0].Role.Name);
    }

    [Fact]
    public async Task FindUserWithRolesAsync_InactiveUser_ReturnsForAuditableInteractiveRejection()
    {
        _db.Users.Add(new User { Username = "bob", PasswordHash = "x", IsActive = false });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindUserWithRolesAsync("bob", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.False(result.IsActive);
    }

    [Fact]
    public async Task FindUserWithRolesAsync_LegacyMixedCaseUsername_IsFoundCaseInsensitively()
    {
        _db.Users.Add(new User { Username = "LegacyAdmin", PasswordHash = "x", IsActive = true });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindUserWithRolesAsync(
            "legacyadmin", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("LegacyAdmin", result.Username);
    }

    [Fact]
    public async Task AnyUsersExistAsync_NoUsers_ReturnsFalse()
    {
        Assert.False(await _repo.AnyUsersExistAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnyUsersExistAsync_WithUsers_ReturnsTrue()
    {
        _db.Users.Add(new User { Username = "a", PasswordHash = "x", IsActive = true });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.AnyUsersExistAsync(ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddUserAsync_PersistsUser()
    {
        await _repo.AddUserAsync(new User { Username = "new", PasswordHash = "x", IsActive = true }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Users.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddServer_AddsToContext()
    {
        _repo.AddServer(new Server { Name = "s", Hostname = "h" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.Servers.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddServerToken_AddsToContext()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        _repo.AddServerToken(new ServerToken { ServerId = server.Id, TokenHash = "hash", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.ServerTokens.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TryPersistServerEnrollmentAsync_ConsumesTokenAndRejectsReplayWithoutExtraServer()
    {
        var registrationToken = new RegistrationToken
        {
            Token = "registration-hash",
            ExpiresAt = DateTime.UtcNow.AddHours(1)
        };
        _db.RegistrationTokens.Add(registrationToken);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var winner = new Server { Name = "winner", Hostname = "winner" };
        var won = await _repo.TryPersistServerEnrollmentAsync(
            registrationToken.Id,
            winner,
            new ServerToken
            {
                Server = winner,
                TokenHash = "winner-token",
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            },
            TestContext.Current.CancellationToken);
        var loser = new Server { Name = "loser", Hostname = "loser" };
        var lost = await _repo.TryPersistServerEnrollmentAsync(
            registrationToken.Id,
            loser,
            new ServerToken
            {
                Server = loser,
                TokenHash = "loser-token",
                ExpiresAt = DateTime.UtcNow.AddDays(1)
            },
            TestContext.Current.CancellationToken);

        Assert.True(won);
        Assert.False(lost);
        Assert.Single(await _db.Servers.ToListAsync(TestContext.Current.CancellationToken));
        Assert.Single(await _db.ServerTokens.ToListAsync(TestContext.Current.CancellationToken));
        Assert.True(registrationToken.IsUsed);
        Assert.Equal(winner.Id, registrationToken.UsedByServerId);
    }

    [Fact]
    public async Task AddRegistrationToken_AddsToContext()
    {
        _repo.AddRegistrationToken(new RegistrationToken { Token = "tok", ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.RegistrationTokens.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetRegistrationTokensAsync_ReturnsOrderedByCreatedAtDesc()
    {
        // Save both entities first (SaveChangesAsync stamps all Added entities with the same CreatedAt).
        _db.RegistrationTokens.AddRange(
            new RegistrationToken { Token = "old", ExpiresAt = DateTime.UtcNow.AddDays(1) },
            new RegistrationToken { Token = "new", ExpiresAt = DateTime.UtcNow.AddDays(1) }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        // Back-date the "old" token (Modified state does not re-stamp CreatedAt).
        var oldToken = _db.RegistrationTokens.First(t => t.Token == "old");
        oldToken.CreatedAt = DateTime.UtcNow.AddDays(-2);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.GetRegistrationTokensAsync(ct: TestContext.Current.CancellationToken);
        Assert.Equal(2, result.Count);
        Assert.Equal("new", result[0].Token);
    }

    [Fact]
    public async Task ValidateServerTokenHashAsync_ValidToken_ReturnsServerId()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ServerTokens.Add(new ServerToken { ServerId = server.Id, TokenHash = "hash", IsRevoked = false, ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.ValidateServerTokenHashAsync(["hash"], ct: TestContext.Current.CancellationToken);
        Assert.Equal(server.Id, result);
    }

    [Fact]
    public async Task ValidateServerTokenHashAsync_RevokedToken_ReturnsNull()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ServerTokens.Add(new ServerToken { ServerId = server.Id, TokenHash = "hash", IsRevoked = true, ExpiresAt = DateTime.UtcNow.AddDays(1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.ValidateServerTokenHashAsync(["hash"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ValidateServerTokenHashAsync_ExpiredToken_ReturnsNull()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ServerTokens.Add(new ServerToken { ServerId = server.Id, TokenHash = "hash", IsRevoked = false, ExpiresAt = DateTime.UtcNow.AddHours(-1) });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(await _repo.ValidateServerTokenHashAsync(["hash"], ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RevokeServerTokensAsync_RevokesActiveTokens()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ServerTokens.AddRange(
            new ServerToken { ServerId = server.Id, TokenHash = "a", IsRevoked = false, ExpiresAt = DateTime.UtcNow.AddDays(1) },
            new ServerToken { ServerId = server.Id, TokenHash = "b", IsRevoked = false, ExpiresAt = DateTime.UtcNow.AddDays(1) },
            new ServerToken { ServerId = server.Id, TokenHash = "c", IsRevoked = true, ExpiresAt = DateTime.UtcNow.AddDays(1) }
        );
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.RevokeServerTokensAsync(server.Id, ct: TestContext.Current.CancellationToken);

        var tokens = await _db.ServerTokens.Where(t => t.ServerId == server.Id).ToListAsync(cancellationToken: TestContext.Current.CancellationToken);
        Assert.All(tokens, t => Assert.True(t.IsRevoked));
    }

    [Fact]
    public async Task ServerExistsAsync_Exists_ReturnsTrue()
    {
        var server = new Server { Name = "s", Hostname = "h" };
        _db.Servers.Add(server);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(await _repo.ServerExistsAsync(server.Id, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ServerExistsAsync_NotExists_ReturnsFalse()
    {
        Assert.False(await _repo.ServerExistsAsync(999, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task FindExternalLoginAsync_Found_ReturnsWithUser()
    {
        var user = new User { Username = "ext", PasswordHash = "x", IsActive = true };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);
        _db.ExternalLogins.Add(new ExternalLogin { UserId = user.Id, Provider = "github", ProviderSubjectId = "123" });
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        var result = await _repo.FindExternalLoginAsync("github", "123", ct: TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.Equal("ext", result.User.Username);
    }

    [Fact]
    public async Task FindExternalLoginAsync_NotFound_ReturnsNull()
    {
        Assert.Null(await _repo.FindExternalLoginAsync("github", "nope", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddExternalLoginAsync_Persists()
    {
        var user = new User { Username = "ext", PasswordHash = "x", IsActive = true };
        _db.Users.Add(user);
        await _db.SaveChangesAsync(cancellationToken: TestContext.Current.CancellationToken);

        await _repo.AddExternalLoginAsync(new ExternalLogin { UserId = user.Id, Provider = "google", ProviderSubjectId = "456" }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(1, await _db.ExternalLogins.CountAsync(cancellationToken: TestContext.Current.CancellationToken));
    }

    public void Dispose() => _db.Dispose();
}
