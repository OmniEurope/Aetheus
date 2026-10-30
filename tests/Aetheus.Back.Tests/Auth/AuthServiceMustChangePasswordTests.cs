// SPDX-License-Identifier: EUPL-1.2
using System.IdentityModel.Tokens.Jwt;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests.Auth;

// S-FEAT-UE4K / mdp first-login cycle: LoginAsync surfaces MustChangePassword in the response and the
// minted JWT carries the aetheus:mcp claim only while the user must change their password.
public class AuthServiceMustChangePasswordTests
{
    private const string Password = "Correct-Password-1!";
    private static readonly string PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password);

    private readonly IAuthRepository _repo = Substitute.For<IAuthRepository>();
    private readonly AuthService _sut;

    public AuthServiceMustChangePasswordTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:AdminPassword"] = "admin",
                ["Auth:JwtKey"] = "aetheus-dev-key-minimum-32-bytes!!"
            })
            .Build();

        var orgRepo = Substitute.For<IOrganizationService>();
        orgRepo.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(1);

        // DB users exist so the config-fallback admin path is skipped.
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(true);

        _sut = new AuthService(
            _repo,
            config,
            Substitute.For<IAuditService>(),
            new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" },
            Substitute.For<IMemoryCache>(),
            orgRepo,
            Substitute.For<ITotpService>(),
            TimeProvider.System,
            Substitute.For<IHttpContextAccessor>(),
            Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), Substitute.For<ILogger<AuthService>>());
    }

    private User MakeUser(bool mustChangePassword) => new()
    {
        Id = 1,
        Username = "testuser",
        Email = "testuser@example.com",
        PasswordHash = PasswordHash,
        IsActive = true,
        MustChangePassword = mustChangePassword,
        SecurityStamp = "stamp",
        UserRoles = [new UserRole { Role = new Role { Name = "User" } }]
    };

    private static bool TokenHasMustChangePasswordClaim(string token)
    {
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        return jwt.Claims.Any(c =>
            c.Type == AetheusClaimTypes.MustChangePassword && c.Value == "true");
    }

    [Fact]
    public async Task R471_TheToken_CarriesAnOpaqueAudienceIdentifier_ThatIsNotTheAccount()
    {
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser(mustChangePassword: false));

        var response = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = Password }, TestContext.Current.CancellationToken);

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(response!.Token);
        var visitor = jwt.Claims.Single(claim => claim.Type == AetheusClaimTypes.AnalyticsVisitor).Value;
        Assert.Equal(AuthTokenHelper.AnalyticsVisitorId("aetheus-dev-key-minimum-32-bytes!!", "1"), visitor);
        Assert.Matches("^[A-Za-z0-9_-]{22}$", visitor);
        Assert.DoesNotContain("testuser", visitor, StringComparison.OrdinalIgnoreCase);
        // One identifier per account, and another key gives other identifiers.
        Assert.NotEqual(visitor, AuthTokenHelper.AnalyticsVisitorId("aetheus-dev-key-minimum-32-bytes!!", "2"));
        Assert.NotEqual(visitor, AuthTokenHelper.AnalyticsVisitorId("another-key-of-at-least-32-bytes!!!", "1"));
    }

    [Fact]
    public async Task LoginAsync_UserMustChangePassword_ResponseFlagTrueAndClaimPresent()
    {
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser(mustChangePassword: true));

        var result = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = Password }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.MustChangePassword);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.True(TokenHasMustChangePasswordClaim(result.Token));
    }

    [Fact]
    public async Task LoginAsync_UserDoesNotNeedChange_ResponseFlagFalseAndClaimAbsent()
    {
        _repo.FindUserByEmailWithRolesAsync("testuser@example.com", Arg.Any<CancellationToken>())
            .Returns(MakeUser(mustChangePassword: false));

        var result = await _sut.LoginAsync(new LoginRequest { Email = "testuser@example.com", Password = Password }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.MustChangePassword);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.False(TokenHasMustChangePasswordClaim(result.Token));
    }

    [Fact]
    public async Task RenewTokenAsync_UserStillMustChange_RenewedTokenKeepsClaim()
    {
        // The claim must survive token renewal so the Front gate persists across a reload.
        _repo.FindUserByIdWithRolesAsync(1, Arg.Any<CancellationToken>())
            .Returns(MakeUser(mustChangePassword: true));

        var result = await _sut.RenewTokenAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(TokenHasMustChangePasswordClaim(result.Token));
    }

    [Fact]
    public async Task RenewTokenAsync_UserNoLongerMustChange_RenewedTokenDropsClaim()
    {
        // A token minted after the password is changed no longer carries the claim.
        _repo.FindUserByIdWithRolesAsync(1, Arg.Any<CancellationToken>())
            .Returns(MakeUser(mustChangePassword: false));

        var result = await _sut.RenewTokenAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(TokenHasMustChangePasswordClaim(result.Token));
    }
}
