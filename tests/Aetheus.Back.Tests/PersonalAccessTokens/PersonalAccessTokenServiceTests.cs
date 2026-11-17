// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.PersonalAccessTokens;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.PersonalAccessTokens;

public class PersonalAccessTokenServiceTests
{
    private const string Key = "aetheus-dev-key-minimum-32-bytes!!";
    private readonly IPersonalAccessTokenRepository _repo = Substitute.For<IPersonalAccessTokenRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero));
    private readonly PersonalAccessTokenService _sut;

    public PersonalAccessTokenServiceTests()
        => _sut = new PersonalAccessTokenService(_repo, new TokenHashKeys(new JwtOptions { SigningKey = Key }), _time, _audit);

    private static PersonalAccessToken WithUser(PatScope scope, params string[] roles)
        => new()
        {
            Id = 5,
            UserId = 7,
            Scope = scope,
            ExpiresAt = new DateTime(2027, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            User = new User
            {
                Id = 7,
                Username = "alice",
                IsActive = true,
                UserRoles = roles.Select(r => new UserRole { RoleId = 1, Role = new Role { Name = r } }).ToList()
            }
        };

    [Fact]
    public async Task Create_ReturnsPlaintextOnce_StoresHashNotPlaintext()
    {
        PersonalAccessToken? captured = null;
        _repo.When(r => r.Add(Arg.Any<PersonalAccessToken>())).Do(ci => captured = ci.Arg<PersonalAccessToken>());

        var result = await _sut.CreateAsync(7, new CreatePersonalAccessTokenRequest
        {
            Name = "  ci-token  ",
            Scope = PatScope.ReadOnly,
            ExpirationDays = 30
        }, ct: TestContext.Current.CancellationToken);

        Assert.StartsWith(PatConstants.TokenPrefix, result.PlaintextToken);
        Assert.NotNull(captured);
        Assert.Equal("ci-token", captured!.Name); // trimmed
        Assert.False(string.IsNullOrEmpty(captured.TokenHash));
        Assert.NotEqual(result.PlaintextToken, captured.TokenHash); // hash, not plaintext
        Assert.Equal(result.PlaintextToken[..PatConstants.PrefixDisplayLength], captured.TokenPrefix);
        Assert.Equal(PatScope.ReadOnly, captured.Scope);
        Assert.Equal(_time.GetUtcNow().UtcDateTime.AddDays(30), captured.ExpiresAt);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Created", "PersonalAccessToken", Arg.Any<int?>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Create_RejectsLifetimeLongerThanNinetyDays()
    {
        await Assert.ThrowsAsync<System.ComponentModel.DataAnnotations.ValidationException>(() =>
            _sut.CreateAsync(
                7,
                new CreatePersonalAccessTokenRequest
                {
                    Name = "too-long",
                    ExpirationDays = 91
                },
                TestContext.Current.CancellationToken));

        _repo.DidNotReceive().Add(Arg.Any<PersonalAccessToken>());
    }

    [Fact]
    public async Task Create_ThenValidate_HashesConsistently_RoundTrip()
    {
        PersonalAccessToken? captured = null;
        _repo.When(r => r.Add(Arg.Any<PersonalAccessToken>())).Do(ci => captured = ci.Arg<PersonalAccessToken>());

        var created = await _sut.CreateAsync(7, new CreatePersonalAccessTokenRequest { Name = "x", Scope = PatScope.ReadWrite }, ct: TestContext.Current.CancellationToken);

        // Validate the same plaintext and capture the hash the repo is queried with - it must equal the stored hash.
        IReadOnlyCollection<string>? lookupHashes = null;
        _repo.FindActiveByHashWithUserAsync(Arg.Do<IReadOnlyCollection<string>>(h => lookupHashes = h), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((PersonalAccessToken?)null);

        await _sut.ValidateAsync(created.PlaintextToken, ct: TestContext.Current.CancellationToken);

        // R-53: the stored hash is the per-purpose one, tried first; the legacy hash follows it.
        Assert.Equal(captured!.TokenHash, lookupHashes!.First());
        Assert.Equal(2, lookupHashes!.Count);
    }

    [Fact]
    public async Task GetForUserAsync_ReturnsNormalizedRepositoryPage()
    {
        _repo.GetForUserPagedAsync(
                7, "ci", 2, 10, "Name", true, Arg.Any<CancellationToken>())
            .Returns((new List<PersonalAccessToken>
            {
                new()
                {
                    Id = 5,
                    UserId = 7,
                    Name = "ci-token",
                    TokenPrefix = "aeth_pat_ci",
                    Scope = PatScope.ReadOnly,
                    CreatedAt = _time.GetUtcNow().UtcDateTime,
                    ExpiresAt = _time.GetUtcNow().UtcDateTime.AddDays(30)
                }
            }, 12));

        var result = await _sut.GetForUserAsync(7, new PaginationRequest
        {
            Search = "ci",
            Page = 2,
            PageSize = 10,
            SortBy = "Name",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal("ci-token", Assert.Single(result.Items).Name);
        Assert.Equal(12, result.TotalCount);
        Assert.Equal(2, result.Page);
        Assert.Equal(10, result.PageSize);
    }

    [Fact]
    public async Task Validate_WrongPrefix_ReturnsNull_WithoutHittingRepo()
    {
        var result = await _sut.ValidateAsync("eyJ.jwt.looking.token", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.DidNotReceive().FindActiveByHashWithUserAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_Unknown_ReturnsNull()
    {
        _repo.FindActiveByHashWithUserAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((PersonalAccessToken?)null);

        Assert.Null(await _sut.ValidateAsync(PatConstants.TokenPrefix + "abc", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validate_Active_ReturnsPrincipalWithCurrentRolesAndScope()
    {
        _repo.FindActiveByHashWithUserAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(WithUser(PatScope.ReadOnly, "Admin", "Ops"));

        var principal = await _sut.ValidateAsync(PatConstants.TokenPrefix + "abc", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(principal);
        Assert.Equal(7, principal!.UserId);
        Assert.Equal("alice", principal.Username);
        Assert.Equal(PatScope.ReadOnly, principal.Scope);
        Assert.Equal(["Admin", "Ops"], principal.Roles);
    }

    [Fact]
    public async Task Validate_TouchesLastUsed_WhenStale()
    {
        _repo.FindActiveByHashWithUserAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(WithUser(PatScope.ReadWrite, "Admin")); // LastUsedAt null => stale

        await _sut.ValidateAsync(PatConstants.TokenPrefix + "abc", ct: TestContext.Current.CancellationToken);

        await _repo.Received(1).TouchLastUsedAsync(5, Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Validate_DoesNotTouchLastUsed_WhenRecent()
    {
        var token = WithUser(PatScope.ReadWrite, "Admin");
        token.LastUsedAt = _time.GetUtcNow().UtcDateTime.AddSeconds(-5); // within throttle window
        _repo.FindActiveByHashWithUserAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(token);

        await _sut.ValidateAsync(PatConstants.TokenPrefix + "abc", ct: TestContext.Current.CancellationToken);

        await _repo.DidNotReceive().TouchLastUsedAsync(Arg.Any<int>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revoke_OwnedActive_SetsRevokedAt_ReturnsTrue()
    {
        var token = new PersonalAccessToken { Id = 3, UserId = 7, ExpiresAt = _time.GetUtcNow().UtcDateTime.AddDays(1) };
        _repo.FindByIdForUserAsync(3, 7, Arg.Any<CancellationToken>()).Returns(token);

        var ok = await _sut.RevokeAsync(3, 7, ct: TestContext.Current.CancellationToken);

        Assert.True(ok);
        Assert.NotNull(token.RevokedAt);
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("Revoked", "PersonalAccessToken", 3, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revoke_NotFound_ReturnsFalse()
    {
        _repo.FindByIdForUserAsync(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns((PersonalAccessToken?)null);

        Assert.False(await _sut.RevokeAsync(99, 7, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revoke_AlreadyRevoked_ReturnsFalse()
    {
        _repo.FindByIdForUserAsync(3, 7, Arg.Any<CancellationToken>())
            .Returns(new PersonalAccessToken { Id = 3, UserId = 7, RevokedAt = _time.GetUtcNow().UtcDateTime.AddDays(-1) });

        Assert.False(await _sut.RevokeAsync(3, 7, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
