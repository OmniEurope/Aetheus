// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

/// <summary> The refresh-token rotation through the real repository on a controlled clock. A second
/// use of the same token inside the five-minute grace window gets an access token only, and the
/// same use after the window is still a replay that revokes the whole chain. The truly parallel
/// race, which needs a relational row lock, is proved against PostgreSQL in
/// <c>RefreshTokenIntegrationTests</c>. </summary>
public sealed class AuthServiceRefreshRotationTests : IDisposable
{
    private const int UserId = 42;
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 9, 21, 10, 4, 9, TimeSpan.Zero));
    private readonly AppDbContext _db;
    private readonly AuthService _sut;

    public AuthServiceRefreshRotationTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            _clock);
        _db.Users.Add(new User { Id = UserId, Username = "carol", IsActive = true, SecurityStamp = "s", UserRoles = [] });
        _db.SaveChanges();

        const string key = "aetheus-dev-key-minimum-32-bytes!!";
        _sut = new AuthService(
            new AuthRepository(_db, _clock),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Auth:JwtKey"] = key }).Build(),
            Substitute.For<IAuditService>(), new JwtOptions { SigningKey = key },
            Substitute.For<IMemoryCache>(), Substitute.For<IOrganizationService>(),
            Substitute.For<ITotpService>(), _clock,
            Substitute.For<IHttpContextAccessor>(), Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), Substitute.For<ILogger<AuthService>>());
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> IssueRefreshTokenAsync() =>
        (await _sut.RenewTokenAsync(UserId, Ct))!.RefreshToken!;

    private Task<List<RefreshToken>> TokensAsync() => _db.RefreshTokens.AsNoTracking().OrderBy(t => t.Id).ToListAsync(Ct);

    [Fact]
    public async Task SecondUseWithinGrace_GetsAnAccessTokenOnly_AndLeavesOneActiveReplacement()
    {
        var original = await IssueRefreshTokenAsync();

        var first = await _sut.RefreshTokenWithReasonAsync(original, Ct);
        _clock.Advance(TimeSpan.FromSeconds(2));
        var second = await _sut.RefreshTokenWithReasonAsync(original, Ct);

        Assert.False(string.IsNullOrEmpty(first.Response!.RefreshToken));
        Assert.Null(second.RejectionCode);
        Assert.False(string.IsNullOrEmpty(second.Response!.Token));
        Assert.Null(second.Response.RefreshToken);
        // Exactly one live token: the replacement the first request received. No fork of the chain.
        var active = (await TokensAsync()).Where(t => t.RevokedAt is null).ToList();
        Assert.Single(active);
        Assert.Equal(active[0].Id, (await TokensAsync())[0].ReplacedById);

        // The replacement handed to the first request keeps working.
        var next = await _sut.RefreshTokenWithReasonAsync(first.Response.RefreshToken!, Ct);
        Assert.Null(next.RejectionCode);
        Assert.False(string.IsNullOrEmpty(next.Response!.RefreshToken));
    }

    [Fact]
    public async Task SecondUseAfterGrace_IsAReplay_AndRevokesTheWholeChain()
    {
        var original = await IssueRefreshTokenAsync();
        var first = await _sut.RefreshTokenWithReasonAsync(original, Ct);

        _clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));
        var replay = await _sut.RefreshTokenWithReasonAsync(original, Ct);

        Assert.Null(replay.Response);
        Assert.Equal(RefreshRejectionCodes.Replay, replay.RejectionCode);
        Assert.All(await TokensAsync(), t => Assert.NotNull(t.RevokedAt));
        Assert.Equal(RefreshRejectionCodes.Replay,
            (await _sut.RefreshTokenWithReasonAsync(first.Response!.RefreshToken!, Ct)).RejectionCode);
    }

    [Fact]
    public async Task UseWithinGrace_AfterTheChainWasEnded_GetsNoAccessToken()
    {
        // Audit AUD-01 (recette R1-50): a password change revokes every refresh token. The token rotated
        // seconds before must not use the grace window to mint an access token after that.
        var original = await IssueRefreshTokenAsync();
        await _sut.RefreshTokenWithReasonAsync(original, Ct);
        foreach (var token in _db.RefreshTokens.Where(t => t.RevokedAt == null))
            token.RevokedAt = _clock.GetUtcNow().UtcDateTime;
        await _db.SaveChangesAsync(Ct);
        _clock.Advance(TimeSpan.FromSeconds(2));

        var reuse = await _sut.RefreshTokenWithReasonAsync(original, Ct);

        Assert.Null(reuse.Response);
        Assert.Equal(RefreshRejectionCodes.Replay, reuse.RejectionCode);
    }

    [Fact]
    public async Task UseWithinGrace_WhenTheReplacementWasRotatedOnward_StillGetsAnAccessToken()
    {
        // Two tabs: the replacement was already rotated by the other tab. That is a live chain, not a theft.
        var original = await IssueRefreshTokenAsync();
        var first = await _sut.RefreshTokenWithReasonAsync(original, Ct);
        await _sut.RefreshTokenWithReasonAsync(first.Response!.RefreshToken!, Ct);
        _clock.Advance(TimeSpan.FromSeconds(2));

        var reuse = await _sut.RefreshTokenWithReasonAsync(original, Ct);

        Assert.Null(reuse.RejectionCode);
        Assert.False(string.IsNullOrEmpty(reuse.Response!.Token));
    }
}
