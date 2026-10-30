// SPDX-License-Identifier: EUPL-1.2
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

/// <summary>PLAN-003 P3-2: people sign in by e-mail. The e-mail of a request is looked up as an e-mail
/// only (a username is no sign-in); an external identity is provisioned only with
/// an e-mail no other account holds.</summary>
public sealed class AuthServiceEmailLoginTests
{
    private const string Password = "Email-Login-Pwd-2026!";
    private readonly IAuthRepository _repo = Substitute.For<IAuthRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly AuthService _sut;

    public AuthServiceEmailLoginTests()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:JwtKey"] = "aetheus-dev-key-minimum-32-bytes!!",
                ["Auth:ExternalLogin:Enabled"] = "true",
                ["Auth:ExternalLogin:AllowedProviders:0"] = "github",
                ["Auth:ExternalLogin:AutoProvision"] = "true"
            })
            .Build();
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(true);
        _sut = new AuthService(
            _repo, config, _audit, new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" },
            Substitute.For<IMemoryCache>(), Substitute.For<IOrganizationService>(), Substitute.For<ITotpService>(),
            TimeProvider.System, Substitute.For<IHttpContextAccessor>(),
            Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(),
            Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(),
            Substitute.For<ILogger<AuthService>>());
    }

    private static User Account(int id = 7, string username = "jane") => new()
    {
        Id = id,
        Username = username,
        Email = "jane@example.com",
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(Password),
        IsActive = true
    };

    [Fact]
    public async Task Email_IsLookedUpAsAnEmail_AndSignsIn_WithTheUsernameAsTheTokenName()
    {
        _repo.FindUserByEmailWithRolesAsync("Jane@Example.com", Arg.Any<CancellationToken>()).Returns(Account());

        var result = await _sut.LoginAsync(new LoginRequest { Email = "Jane@Example.com", Password = Password },
            TestContext.Current.CancellationToken);

        Assert.False(string.IsNullOrEmpty(result?.Token));
        var name = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(result!.Token)
            .Claims.First(claim => claim.Type is "unique_name" or System.Security.Claims.ClaimTypes.Name).Value;
        Assert.Equal("jane", name);
        await _repo.DidNotReceive().FindUserWithRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Email_ThatMatchesNoAccount_NeverFallsBackToTheUsername()
    {
        _repo.FindUserWithRolesAsync("jane", Arg.Any<CancellationToken>()).Returns(Account());

        var result = await _sut.LoginAsync(new LoginRequest { Email = "jane", Password = Password },
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.DidNotReceive().FindUserWithRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("LoginFailed.InvalidCredentials", "Authentication", null,
            Arg.Is<string>(details => details.Contains("jane", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Email_WithAWrongPassword_CountsTowardsTheLockout()
    {
        _repo.FindUserByEmailWithRolesAsync("jane@example.com", Arg.Any<CancellationToken>()).Returns(Account());

        var result = await _sut.LoginAsync(new LoginRequest { Email = "jane@example.com", Password = "wrong-password" },
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.Received(1).IncrementFailedLoginAsync(7, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Username_WithTheRightPassword_IsNoSignIn_AndIsAuditedAsAnyFailedSignIn()
    {
        // The account "jane" exists and the password is right: the username is not a credential.
        _repo.FindUserWithRolesAsync("jane", Arg.Any<CancellationToken>()).Returns(Account());

        var result = await _sut.LoginAsync(new LoginRequest { Email = " Jane ", Password = Password },
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.DidNotReceive().FindUserWithRolesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("LoginFailed.InvalidCredentials", "Authentication", null,
            Arg.Is<string>(details => details.Contains("Jane", StringComparison.Ordinal)), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("   ", false)]
    [InlineData("Taken@Example.com", true)]
    public async Task ExternalLogin_IsNotProvisioned_WithoutAFreeEmail(string? email, bool taken)
    {
        _repo.EmailInUseAsync("taken@example.com", null, Arg.Any<CancellationToken>()).Returns(taken);

        var result = await _sut.HandleExternalLoginAsync("github", "subject-1", "Jane", email,
            TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.DidNotReceive().AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ExternalLogin_IsProvisioned_WithTheEmailNormalized()
    {
        User? added = null;
        _repo.AddUserAsync(Arg.Do<User>(user => added = user), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.FindUserWithRolesAsync("github_subject-2", Arg.Any<CancellationToken>())
            .Returns(null, Account(8, "github_subject-2"));

        await _sut.HandleExternalLoginAsync("github", "subject-2", "Jane", " Jane@Example.COM ",
            TestContext.Current.CancellationToken);

        Assert.Equal("jane@example.com", added?.Email);
    }
}
