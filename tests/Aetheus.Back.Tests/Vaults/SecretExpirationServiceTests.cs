// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Alerts;
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Components.Vaults;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Vaults;

public class SecretExpirationServiceTests
{
    private static (SecretExpirationService Sut, IVaultRepository VaultRepo, INotificationService Notifier) BuildSut(
        IConfiguration config, FakeTimeProvider clock)
    {
        var vaultRepo = Substitute.For<IVaultRepository>();
        var notifier = Substitute.For<INotificationService>();
        var sp = new ServiceCollection()
            .AddScoped(_ => vaultRepo)
            .AddScoped(_ => notifier)
            .BuildServiceProvider();
        var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
        var logger = Substitute.For<ILogger<SecretExpirationService>>();
        return (new SecretExpirationService(scopeFactory, config, logger, clock), vaultRepo, notifier);
    }

    [Fact]
    public async Task ProcessExpiringSecretsAsync_QueriesWithAlertThresholdAndNotifies()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["SecretExpiration:AlertDays"] = "14" })
            .Build();
        var (sut, vaultRepo, notifier) = BuildSut(config, clock);

        var secret = new VaultSecret
        {
            Id = 11,
            Key = "API_TOKEN",
            ExpiresAt = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc),
            Vault = new Vault { Name = "prod" }
        };
        vaultRepo.GetExpiringSecretsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([secret]);

        await sut.ProcessExpiringSecretsAsync(TestContext.Current.CancellationToken);

        // Threshold = now + AlertDays (14) = 2026-06-30
        await vaultRepo.Received(1).GetExpiringSecretsAsync(
            new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Utc), Arg.Any<CancellationToken>());
        await notifier.Received(1).SendEventAsync("SecretExpiring", Arg.Any<object>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// BACK-003 follow-up: an AI task starts only for an event naming its owner, so the event carries
    /// the project owning the vault, whichever way the vault is owned. The real repository query is
    /// used, since it is what loads the environment or project server the owner is read from.
    /// </summary>
    [Theory]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("project-server")]
    public async Task ProcessExpiringSecretsAsync_StartsTheAiTasksOfTheVaultProjectOnly(string owner)
    {
        var ct = TestContext.Current.CancellationToken;
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);
        db.Projects.Add(new Project { Id = 1, Name = "own-project", OrganizationId = 7 });
        db.Environments.Add(new Aetheus.Back.Data.Entities.Environment { Id = 5, Name = "prod", ProjectId = 1 });
        db.ProjectServers.Add(new ProjectServer { Id = 6, ProjectId = 1, DisplayName = "vps" });
        var vault = new Vault
        {
            Id = 3,
            Name = "prod",
            ProjectId = owner == "project" ? 1 : null,
            EnvironmentId = owner == "environment" ? 5 : null,
            ProjectServerId = owner == "project-server" ? 6 : null
        };
        db.Vaults.Add(vault);
        db.VaultSecrets.Add(new VaultSecret
        {
            Id = 11,
            VaultId = vault.Id,
            Key = "API_TOKEN",
            ExpiresAt = new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc)
        });
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        var notifier = Substitute.For<INotificationService>();
        object? payload = null;
        await notifier.SendEventAsync("SecretExpiring", Arg.Do<object>(value => payload = value), Arg.Any<CancellationToken>());
        var sp = new ServiceCollection()
            .AddScoped<IVaultRepository>(_ => new VaultRepository(db, clock))
            .AddScoped(_ => notifier)
            .BuildServiceProvider();
        var sut = new SecretExpirationService(
            sp.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationBuilder().Build(),
            Substitute.For<ILogger<SecretExpirationService>>(),
            clock);

        await sut.ProcessExpiringSecretsAsync(ct);

        Assert.NotNull(payload);
        Assert.Equal(
            ["own"],
            await Aetheus.Back.Tests.AiTasks.AiEventDispatchProbe.StartedDefinitionsAsync(
                "SecretExpiring", payload, ownProjectId: 1, foreignProjectId: 2, ct));
    }

    [Fact]
    public async Task ProcessExpiringSecretsAsync_NoExpiringSecrets_DoesNotNotify()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        var (sut, vaultRepo, notifier) = BuildSut(new ConfigurationBuilder().Build(), clock);
        vaultRepo.GetExpiringSecretsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns([]);

        await sut.ProcessExpiringSecretsAsync(TestContext.Current.CancellationToken);

        await notifier.DidNotReceive().SendEventAsync(Arg.Any<string>(), Arg.Any<object>(), Arg.Any<CancellationToken>());
    }
}
