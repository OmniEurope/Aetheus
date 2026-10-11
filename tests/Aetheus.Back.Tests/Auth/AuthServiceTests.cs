// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Shared;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class AuthServiceTests
{
    private readonly IAuthRepository _repo = Substitute.For<IAuthRepository>();
    private readonly IConfiguration _config;
    private readonly AuthService _sut;
    private readonly ServerEnrollmentService _enrollment;
    private readonly IAuditService _audit = Substitute.For<IAuditService>();

    public AuthServiceTests()
    {
        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Auth:AdminUser"] = "admin",
                ["Auth:AdminPassword"] = "secret",
                ["Auth:JwtKey"] = "aetheus-dev-key-minimum-32-bytes!!",
                ["Auth:ExternalLogin:Enabled"] = "true",
                ["Auth:ExternalLogin:AllowedProviders:0"] = "github",
                ["Auth:ExternalLogin:AllowedProviders:1"] = "google",
                ["Auth:ExternalLogin:AutoProvision"] = "true"
            })
            .Build();

        var hubClients = Substitute.For<IHubClients>();
        var clientProxy = Substitute.For<IClientProxy>();
        hubClients.Group(Arg.Any<string>()).Returns(clientProxy);
        hubClients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(clientProxy);
        var hubContext = Substitute.For<IHubContext<ServerHub>>();
        hubContext.Clients.Returns(hubClients);

        _repo.TryPersistServerEnrollmentAsync(
                Arg.Any<int>(),
                Arg.Any<Server>(),
                Arg.Any<ServerToken>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var server = call.Arg<Server>();
                if (server.Id == 0)
                    server.Id = 1;
                return true;
            });

        var orgRepo = Substitute.For<IOrganizationService>();
        orgRepo.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>())
            .Returns(1);

        var jwtOptions = new JwtOptions { SigningKey = "aetheus-dev-key-minimum-32-bytes!!" };

        _sut = new AuthService(
            _repo,
            _config,
            _audit,
            jwtOptions,
                Substitute.For<IMemoryCache>(),
                orgRepo,
                Substitute.For<ITotpService>(),
                TimeProvider.System,
                Substitute.For<IHttpContextAccessor>(),
                Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(),
                Substitute.For<Aetheus.Back.Services.DomainEvents.IDomainEventDispatcher>(), Substitute.For<ILogger<AuthService>>());

        _enrollment = new ServerEnrollmentService(_repo, _config, _audit, hubContext, Substitute.For<Aetheus.Back.Services.IAdminChangeNotifier>(), jwtOptions, TimeProvider.System);
    }

    // --- LoginAsync ---

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsToken()
    {
        var result = await _sut.LoginAsync(new LoginRequest { Username = "admin", Password = "secret" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.True(result.ExpiresAt > DateTime.UtcNow);
    }

    [Fact]
    public async Task LoginAsync_RunScopedBootstrapIdentity_ReturnsTokenWithoutDatabaseMutation()
    {
        const string username = "deploy-smoke-0123456789abcdef";
        var password = new string('p', 64);
        var expiresAt = TimeProvider.System.GetUtcNow().AddMinutes(5);
        _config["Auth:DeploymentBootstrapUser"] = username;
        _config["Auth:DeploymentBootstrapPassword"] = password;
        _config["Auth:DeploymentBootstrapExpiresAtUtc"] = expiresAt.ToString("O");
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.LoginAsync(
            new LoginRequest { Username = username, Password = password },
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.True(result.ExpiresAt <= expiresAt.UtcDateTime);
        await _repo.Received(1).FindUserWithRolesAsync(username, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The deployment identity has its own keys precisely so it cannot become the persisted
    /// administrator's password. DbInitializer hashes Auth:AdminPassword into the seeded `admin` on a
    /// first run, so while the run-scoped secret arrived under that name, deploying to a new
    /// environment left it with a permanent account whose password only that one run ever knew - and
    /// the run masks it out of its own log by design. Nobody could log in afterwards.
    /// </summary>
    [Fact]
    public async Task RunScopedBootstrapIdentity_DoesNotBecomeTheAdministratorPassword()
    {
        var deployPassword = new string('d', 48);
        _config["Auth:DeploymentBootstrapUser"] = "deploy-smoke-separate";
        _config["Auth:DeploymentBootstrapPassword"] = deployPassword;
        _config["Auth:DeploymentBootstrapExpiresAtUtc"] =
            TimeProvider.System.GetUtcNow().AddMinutes(5).ToString("O");
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(false);

        // The persistent pair is untouched by the deployment identity, and still works while the
        // database has no user - which is exactly when DbInitializer is about to seed it.
        var admin = await _sut.LoginAsync(
            new LoginRequest { Username = "admin", Password = "secret" },
            ct: TestContext.Current.CancellationToken);
        Assert.NotNull(admin);

        // And the administrator's name cannot be logged into with the deployment secret.
        var crossed = await _sut.LoginAsync(
            new LoginRequest { Username = "admin", Password = deployPassword },
            ct: TestContext.Current.CancellationToken);
        Assert.Null(crossed);
    }

    /// <summary>
    /// The other direction: once the database holds a user, the persistent bootstrap pair closes as
    /// it always did. Separating the two identities must not have reopened it.
    /// </summary>
    [Fact]
    public async Task PersistentBootstrapPair_StillClosesOnceTheDatabaseHasAUser()
    {
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.LoginAsync(
            new LoginRequest { Username = "admin", Password = "secret" },
            ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task LoginAsync_ExpiredRunScopedBootstrapIdentity_IsRejectedWhenDatabaseHasUsers()
    {
        const string username = "deploy-smoke-expired";
        _config["Auth:DeploymentBootstrapUser"] = username;
        _config["Auth:DeploymentBootstrapPassword"] = "0123456789abcdef0123456789abcdef";
        _config["Auth:DeploymentBootstrapExpiresAtUtc"] = TimeProvider.System.GetUtcNow().AddMinutes(-1).ToString("O");
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.LoginAsync(
            new LoginRequest { Username = username, Password = _config["Auth:DeploymentBootstrapPassword"]! },
            ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task LoginAsync_OverlongRunScopedBootstrapIdentity_IsRejectedWhenDatabaseHasUsers()
    {
        const string username = "deploy-smoke-overlong";
        _config["Auth:DeploymentBootstrapUser"] = username;
        _config["Auth:DeploymentBootstrapPassword"] = "0123456789abcdef0123456789abcdef";
        _config["Auth:DeploymentBootstrapExpiresAtUtc"] = TimeProvider.System.GetUtcNow().AddMinutes(16).ToString("O");
        _repo.AnyUsersExistAsync(Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.LoginAsync(
            new LoginRequest { Username = username, Password = _config["Auth:DeploymentBootstrapPassword"]! },
            ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task LoginAsync_InvalidUser_ReturnsNull()
    {
        var result = await _sut.LoginAsync(new LoginRequest { Username = "wrong", Password = "secret" }, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
        await _audit.Received(1).LogAsync(
            "LoginFailed.InvalidCredentials", "Authentication", null,
            Arg.Is<string>(details => details.Contains("wrong", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_InactiveUser_IsRejectedAndAuditedAgainstUser()
    {
        _repo.FindUserWithRolesAsync("disabled", Arg.Any<CancellationToken>()).Returns(new User
        {
            Id = 42,
            Username = "disabled",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("secret"),
            IsActive = false
        });

        var result = await _sut.LoginAsync(
            new LoginRequest { Username = "disabled", Password = "secret" },
            ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _audit.Received(1).LogAsync(
            "LoginFailed.Inactive", "User", 42,
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_InvalidPassword_ReturnsNull()
    {
        var result = await _sut.LoginAsync(new LoginRequest { Username = "admin", Password = "wrong" }, ct: TestContext.Current.CancellationToken);
        Assert.Null(result);
    }

    // --- CreateRegistrationTokenAsync ---

    [Fact]
    public async Task CreateRegistrationTokenAsync_ReturnsDto()
    {
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.CreateRegistrationTokenAsync(new CreateRegistrationTokenRequest { ExpirationHours = 48 }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
        Assert.False(result.IsUsed);
        _repo.Received(1).AddRegistrationToken(Arg.Any<RegistrationToken>());
    }

    [Fact]
    public async Task CreateRegistrationTokenAsync_DefaultExpiration_24Hours()
    {
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.CreateRegistrationTokenAsync(new CreateRegistrationTokenRequest { ExpirationHours = 0 }, ct: TestContext.Current.CancellationToken);

        Assert.True(result.ExpiresAt > DateTime.UtcNow.AddHours(23));
        Assert.True(result.ExpiresAt < DateTime.UtcNow.AddHours(25));
    }

    // --- GetRegistrationTokensAsync ---

    [Fact]
    public async Task GetRegistrationTokensAsync_MapsEntities()
    {
        _repo.GetRegistrationTokensAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new RegistrationToken { Id = 1, Token = "tok1", IsUsed = false, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) },
            new RegistrationToken { Id = 2, Token = "tok2", IsUsed = true, UsedByServerId = 5, CreatedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(1) }
        ]);

        var result = await _sut.GetRegistrationTokensAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.False(result[0].IsUsed);
        Assert.True(result[1].IsUsed);
        Assert.Equal(5, result[1].UsedByServerId);
    }

    // --- ValidateServerTokenAsync ---

    [Fact]
    public async Task ValidateServerTokenAsync_ValidToken_ReturnsServerId()
    {
        _repo.ValidateServerTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(42);

        var result = await _sut.ValidateServerTokenAsync("some-token", ct: TestContext.Current.CancellationToken);

        Assert.Equal(42, result);
    }

    [Fact]
    public async Task ValidateServerTokenAsync_InvalidToken_ReturnsNull()
    {
        _repo.ValidateServerTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((int?)null);

        var result = await _sut.ValidateServerTokenAsync("bad-token", ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    // --- RegisterServerAsync ---

    [Fact]
    public async Task RegisterServerAsync_InvalidToken_ReturnsNull()
    {
        _repo.FindValidRegistrationTokenAsync("bad", Arg.Any<CancellationToken>())
            .Returns((RegistrationToken?)null);

        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "bad",
            Hostname = "host",
            OsDescription = "Linux",
            AgentVersion = "1.0",
            IpAddress = "10.0.0.1"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RegisterServerAsync_NewAgent_PersistsCompatibilityContractBeforeFirstHeartbeat()
    {
        var regToken = new RegistrationToken { Id = 1, Token = "valid-token" };
        Server? captured = null;
        _repo.FindValidRegistrationTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(regToken);
        _repo.FindServerByHostnameAsync("contract-host", Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        _repo.TryPersistServerEnrollmentAsync(
                regToken.Id,
                Arg.Any<Server>(),
                Arg.Any<ServerToken>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<Server>();
                captured.Id = 1;
                return true;
            });

        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "valid-token",
            Hostname = "contract-host",
            OsDescription = "Linux",
            AgentVersion = "1.0.1294",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilities =
            [
                AgentCapabilities.PipelineBuild,
                AgentCapabilities.SelfUpdate,
                AgentCapabilities.PipelineBuild
            ],
            PipelineRunnerAvailable = true,
            IpAddress = "10.0.0.1"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(captured);
        Assert.Equal(AgentProtocol.CurrentVersion, captured.AgentProtocolVersion);
        Assert.Equal(
            "[\"agent.self-update\",\"pipeline.build\"]",
            captured.AgentCapabilitiesJson);
        Assert.True(captured.PipelineRunnerEnabled);
    }

    [Fact]
    public async Task RegisterServerAsync_NewServer_CreatesAndReturns()
    {
        var regToken = new RegistrationToken { Id = 1, Token = "valid-token" };
        _repo.FindValidRegistrationTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(regToken);
        _repo.FindServerByHostnameAsync("new-host", Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "valid-token",
            Hostname = "new-host",
            OsDescription = "Linux",
            AgentVersion = "1.0",
            IpAddress = "10.0.0.1"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.BearerToken));
        await _repo.Received(1).TryPersistServerEnrollmentAsync(
            regToken.Id,
            Arg.Is<Server>(server => server.Hostname == "new-host"),
            Arg.Is<ServerToken>(token => token.Server.Hostname == "new-host"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterServerAsync_AtomicPersistenceLosesTokenRace_ReturnsNullWithoutNotifications()
    {
        var regToken = new RegistrationToken { Id = 7, Token = "valid-token", OrganizationId = 1 };
        _repo.FindValidRegistrationTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(regToken);
        _repo.FindServerByHostnameAsync("race-loser", Arg.Any<CancellationToken>())
            .Returns((Server?)null);
        _repo.TryPersistServerEnrollmentAsync(
                regToken.Id,
                Arg.Any<Server>(),
                Arg.Any<ServerToken>(),
                Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "valid-token",
            Hostname = "race-loser",
            OsDescription = "Linux",
            AgentVersion = "1.0",
            IpAddress = "10.0.0.7"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RegisterServerAsync_ExistingServer_UpdatesAndReturns()
    {
        var regToken = new RegistrationToken { Id = 1, Token = "valid-token" };
        var existingServer = new Server { Id = 5, Name = "existing", Hostname = "existing-host" };
        _repo.FindValidRegistrationTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(regToken);
        _repo.FindServerByHostnameAsync("existing-host", Arg.Any<CancellationToken>())
            .Returns(existingServer);
        _repo.ServerHasActiveTokensAsync(existingServer.Id, Arg.Any<CancellationToken>())
            .Returns(false);
        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "valid-token",
            Hostname = "existing-host",
            OsDescription = "Ubuntu 24.04",
            AgentVersion = "2.0",
            IpAddress = "10.0.0.2"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(5, result.ServerId);
        Assert.Equal("Ubuntu 24.04", existingServer.OsDescription);
        await _repo.Received(1).TryPersistServerEnrollmentAsync(
            regToken.Id,
            existingServer,
            Arg.Any<ServerToken>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterServerAsync_ExistingServerOwnedByAnotherOrganization_IsRejected()
    {
        var regToken = new RegistrationToken
        {
            Id = 1,
            Token = "valid-token",
            OrganizationId = 2
        };
        var existingServer = new Server
        {
            Id = 5,
            Name = "existing",
            Hostname = "existing-host",
            OrganizationId = 1
        };
        _repo.FindValidRegistrationTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(regToken);
        _repo.FindServerByHostnameAsync("existing-host", Arg.Any<CancellationToken>())
            .Returns(existingServer);

        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "valid-token",
            Hostname = "existing-host",
            OsDescription = "Ubuntu 24.04",
            AgentVersion = "2.0",
            IpAddress = "10.0.0.2"
        }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
        await _repo.DidNotReceive().TryPersistServerEnrollmentAsync(
            Arg.Any<int>(),
            Arg.Any<Server>(),
            Arg.Any<ServerToken>(),
            Arg.Any<CancellationToken>());
    }

    // --- Enrollment: PipelineRunnerEnabled default (secure-by-default gate) ---

    [Fact]
    public async Task RegisterServerAsync_PipelineRunnerAvailableFalse_DisablesRunner()
    {
        var regToken = new RegistrationToken { Id = 1, Token = "valid-token" };
        Server? captured = null;
        _repo.FindValidRegistrationTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(regToken);
        _repo.FindServerByHostnameAsync("new-host", Arg.Any<CancellationToken>()).Returns((Server?)null);
        _repo.TryPersistServerEnrollmentAsync(
                regToken.Id,
                Arg.Any<Server>(),
                Arg.Any<ServerToken>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<Server>();
                captured.Id = 1;
                return true;
            });

        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "valid-token",
            Hostname = "new-host",
            OsDescription = "Linux",
            AgentVersion = "1.0",
            IpAddress = "10.0.0.1",
            PipelineRunnerAvailable = false
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(captured);
        Assert.False(captured.PipelineRunnerEnabled);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true)]
    public async Task RegisterServerAsync_MissingCurrentContract_DisablesRunnerByDefault(
        bool? pipelineRunnerAvailable)
    {
        var regToken = new RegistrationToken { Id = 1, Token = "valid-token" };
        Server? captured = null;
        _repo.FindValidRegistrationTokenAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(regToken);
        _repo.FindServerByHostnameAsync("new-host", Arg.Any<CancellationToken>()).Returns((Server?)null);
        _repo.TryPersistServerEnrollmentAsync(
                regToken.Id,
                Arg.Any<Server>(),
                Arg.Any<ServerToken>(),
                Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<Server>();
                captured.Id = 1;
                return true;
            });

        var result = await _enrollment.RegisterServerAsync(new ServerRegistrationRequest
        {
            RegistrationToken = "valid-token",
            Hostname = "new-host",
            OsDescription = "Linux",
            AgentVersion = "1.0",
            IpAddress = "10.0.0.1",
            PipelineRunnerAvailable = pipelineRunnerAvailable
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.NotNull(captured);
        Assert.False(captured.PipelineRunnerEnabled);
    }

    // --- RotateAgentTokenAsync ---

    [Fact]
    public async Task RotateAgentTokenAsync_ServerNotFound_ReturnsNull()
    {
        _repo.ServerExistsAsync(99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.RotateAgentTokenAsync(99, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RotateAgentTokenAsync_ServerExists_RevokesAndReturnsNewToken()
    {
        _repo.ServerExistsAsync(1, Arg.Any<CancellationToken>()).Returns(true);
        _repo.RevokeServerTokensAsync(1, Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.RotateAgentTokenAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(1, result.ServerId);
        Assert.False(string.IsNullOrEmpty(result.BearerToken));
        await _repo.Received(1).RevokeServerTokensAsync(1, Arg.Any<CancellationToken>());
    }

    // --- HandleExternalLoginAsync ---

    [Fact]
    public async Task HandleExternalLoginAsync_ExistingLogin_ReturnsToken()
    {
        var user = new User { Id = 1, Username = "ext-user", IsActive = true, UserRoles = [new UserRole { Role = new Role { Name = "User" } }] };
        _repo.FindExternalLoginAsync("github", "123", Arg.Any<CancellationToken>())
            .Returns(new ExternalLogin { UserId = 1, Provider = "github", ProviderSubjectId = "123", User = user });

        var result = await _sut.HandleExternalLoginAsync("github", "123", "ext-user", "ext@test.com", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.Token));
    }

    [Fact]
    public async Task HandleExternalLoginAsync_InactiveUser_ReturnsNull()
    {
        var user = new User { Id = 2, Username = "inactive", IsActive = false, UserRoles = [] };
        _repo.FindExternalLoginAsync("github", "456", Arg.Any<CancellationToken>())
            .Returns(new ExternalLogin { UserId = 2, Provider = "github", ProviderSubjectId = "456", User = user });

        var result = await _sut.HandleExternalLoginAsync("github", "456", "inactive", null, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task HandleExternalLoginAsync_NewUser_CreatesAndReturnsToken()
    {
        // Canonical username for newly provisioned external identities is "{provider}_{subjectId}".
        // displayName is intentionally NOT used to look up an existing local account (prevents takeover).
        const string canonical = "google_789";

        _repo.FindExternalLoginAsync("google", "789", Arg.Any<CancellationToken>())
            .Returns((ExternalLogin?)null);
        _repo.AddUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.AddExternalLoginAsync(Arg.Any<ExternalLogin>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        // First call (collision check) returns null; second call (refetch with roles) returns the new user.
        _repo.FindUserWithRolesAsync(canonical, Arg.Any<CancellationToken>())
            .Returns((User?)null, new User { Id = 10, Username = canonical, IsActive = true, UserRoles = [], SecurityStamp = "stamp" });

        var result = await _sut.HandleExternalLoginAsync("google", "789", "NewUser", "new@test.com", ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        await _repo.Received(1).AddUserAsync(Arg.Is<User>(u => u.Username == canonical), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddExternalLoginAsync(Arg.Any<ExternalLogin>(), Arg.Any<CancellationToken>());
    }
}
