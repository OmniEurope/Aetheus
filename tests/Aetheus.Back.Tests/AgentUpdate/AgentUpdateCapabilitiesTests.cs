// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.AgentUpdate;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.AgentUpdate;

/// <summary>
/// R3-58 (ADR-051): an agent update may also grant the whitelisted capabilities, and nothing else. The
/// service refuses everything outside the whitelist before any state changes; the repository persists
/// what was asked, hands it to the agent through the task environment and expects the grant to show in
/// the confirming heartbeat.
/// </summary>
public sealed class AgentUpdateCapabilitiesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private static readonly string[] PatchManage = [AgentEnableableCapabilities.PatchManage];

    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IAgentUpdateRepository _repo = Substitute.For<IAgentUpdateRepository>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _tasks =
        Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly AgentUpdateService _sut;

    public AgentUpdateCapabilitiesTests()
    {
        var hub = Substitute.For<IHubContext<ServerHub>>();
        var clients = Substitute.For<IHubClients>();
        clients.Group(Arg.Any<string>()).Returns(Substitute.For<IClientProxy>());
        hub.Clients.Returns(clients);
        var releases = Substitute.For<IAgentReleaseCatalog>();
        releases.Current.Returns(new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 });
        _repo.TryQueueAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => (new AgentUpdateRequest { Id = call.ArgAt<int>(0), ServerId = 1, TargetVersion = "1.0.0" },
                (ServerTask?)null, 0));
        _sut = new AgentUpdateService(
            hub, _tasks, _audit, _repo, releases, Substitute.For<IAgentCompatibilityPolicy>(),
            NullLogger<AgentUpdateService>.Instance,
            new AgentUpdateNotificationPublisher(
                Substitute.For<Aetheus.Back.Components.Notifications.IUserNotificationService>(),
                NullLogger<AgentUpdateNotificationPublisher>.Instance));
    }

    private static Server LinuxServer(string? capabilitiesJson = null) => new()
    {
        Id = 1,
        Name = "srv-1",
        OsType = OsType.Linux,
        AgentCapabilitiesJson = capabilitiesJson
            ?? $"[\"{AgentCapabilities.SelfUpdate}\",\"{AgentCapabilities.EnableCapabilitiesOnUpdate}\"]"
    };

    private void Reserves(AgentUpdateRequest request, bool created = true) =>
        _repo.ReserveAsync(
                Arg.Any<Server>(), Arg.Any<AgentReleaseManifestDto>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<IReadOnlyList<string>?>())
            .Returns((request, created));

    [Theory]
    [InlineData("docker")]
    [InlineData("service-control")]
    [InlineData("deployment")]
    [InlineData("enable-all")]
    [InlineData("--enable-all")]
    [InlineData("FIREWALL-MANAGE")]
    [InlineData("firewall-manage,docker")]
    [InlineData("PATCH-MANAGE")]
    [InlineData(" patch-manage")]
    [InlineData("patch-manage,docker")]
    [InlineData("")]
    public async Task ACapabilityOutsideTheWhitelist_IsRefusedBeforeAnythingIsReadOrQueued(string capability)
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.QueueUpdateWithCapabilitiesAsync(
            1, "admin", [capability], TestContext.Current.CancellationToken));

        await _repo.DidNotReceiveWithAnyArgs().FindServerAsync(default, TestContext.Current.CancellationToken);
        await _repo.DidNotReceiveWithAnyArgs().ReserveAsync(
            default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnEmptyRequest_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.QueueUpdateWithCapabilitiesAsync(
            1, "admin", [], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnknownServer_IsNotFound()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns((Server?)null);

        await Assert.ThrowsAsync<NotFoundException>(() => _sut.QueueUpdateWithCapabilitiesAsync(
            1, "admin", PatchManage, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AWindowsServer_IsRefused()
    {
        var server = LinuxServer();
        server.OsType = OsType.Windows;
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(server);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.QueueUpdateWithCapabilitiesAsync(
            1, "admin", PatchManage, TestContext.Current.CancellationToken));
        await _repo.DidNotReceiveWithAnyArgs().ReserveAsync(
            default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AnAgentThatDoesNotDeclareTheField_IsRefusedInsteadOfUpdatedWithoutTheGrant()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>())
            .Returns(LinuxServer($"[\"{AgentCapabilities.SelfUpdate}\"]"));

        var failure = await Assert.ThrowsAsync<ConflictException>(() => _sut.QueueUpdateWithCapabilitiesAsync(
            1, "admin", PatchManage, TestContext.Current.CancellationToken));

        Assert.Contains("update it first", failure.Message, StringComparison.Ordinal);
        await _repo.DidNotReceiveWithAnyArgs().ReserveAsync(
            default!, default!, default!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task AWhitelistedRequest_IsReservedWithTheNormalizedListAndAudited()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(LinuxServer());
        Reserves(new AgentUpdateRequest
        {
            Id = 7,
            ServerId = 1,
            TargetVersion = "1.0.0",
            EnableCapabilities = AgentEnableableCapabilities.PatchManage
        });

        // Duplicates collapse: the request carries the capability once.
        await _sut.QueueUpdateWithCapabilitiesAsync(
            1, "admin", [AgentEnableableCapabilities.PatchManage, AgentEnableableCapabilities.PatchManage],
            TestContext.Current.CancellationToken);

        await _repo.Received(1).ReserveAsync(
            Arg.Any<Server>(), Arg.Any<AgentReleaseManifestDto>(), "admin",
            Arg.Any<CancellationToken>(),
            Arg.Is<IReadOnlyList<string>?>(list => list != null && list.SequenceEqual(PatchManage)));
        await _audit.Received(1).LogAsync(
            "AgentCapabilityEnableRequested", "Server", 1,
            Arg.Is<string>(details => details.Contains(AgentEnableableCapabilities.PatchManage, StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ARunningUpdateThatDoesNotCarryTheGrant_IsAConflictNotAFalseQueued()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(LinuxServer());
        // The reused request already produced its task, without the capability.
        Reserves(new AgentUpdateRequest { Id = 7, ServerId = 1, TargetVersion = "1.0.0", TaskId = 55 }, created: false);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.QueueUpdateWithCapabilitiesAsync(
            1, "admin", PatchManage, TestContext.Current.CancellationToken));
        await _repo.DidNotReceiveWithAnyArgs().TryQueueAsync(default, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ThePlainUpdate_NeverAsksForCapabilities()
    {
        _repo.FindServerAsync(1, Arg.Any<CancellationToken>()).Returns(LinuxServer());
        Reserves(new AgentUpdateRequest { Id = 7, ServerId = 1, TargetVersion = "1.0.0" });

        await _sut.QueueUpdateAsync(1, "admin", TestContext.Current.CancellationToken);

        await _repo.Received(1).ReserveAsync(
            Arg.Any<Server>(), Arg.Any<AgentReleaseManifestDto>(), "admin",
            Arg.Any<CancellationToken>(), null);
        await _audit.DidNotReceive().LogAsync(
            "AgentCapabilityEnableRequested", Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheRepositoryPersistsTheGrant_PutsItInTheTaskEnvironmentAndExpectsItInTheHeartbeat()
    {
        await using var db = CreateDb();
        db.Servers.Add(new Server { Id = 1, Name = "srv-1", Hostname = "srv-1", OsType = OsType.Linux });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentUpdateRepository(db, new FakeTimeProvider(Now));
        var release = new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 };

        var (request, created) = await repository.ReserveAsync(
            (await db.Servers.FindAsync([1], TestContext.Current.CancellationToken))!, release, "admin",
            TestContext.Current.CancellationToken, PatchManage);
        var (_, task, _) = await repository.TryQueueAsync(request.Id, TestContext.Current.CancellationToken);

        Assert.True(created);
        Assert.Equal(AgentEnableableCapabilities.PatchManage, request.EnableCapabilities);
        Assert.Contains(
            AgentCapabilities.PatchManagement,
            JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson)!);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(task!.EnvironmentVariables!)!;
        Assert.Equal("1.0.0", environment["AETHEUS_AGENT_TARGET_VERSION"]);
        Assert.Equal(AgentEnableableCapabilities.PatchManage, environment[AgentEnableableCapabilities.EnvironmentVariable]);
    }

    // ADR-055: every accepted token reaches the task environment as written and is expected back in the
    // heartbeat under its own capability (rkhunter reports none, so nothing is expected for it).
    [Theory]
    [InlineData("package-manage", "package.manage")]
    [InlineData("service-enable", "service.manage")]
    [InlineData("cron-manage", "cron.manage")]
    [InlineData("rkhunter-manage", null)]
    [InlineData("apache-manage", "apache.manage")]
    [InlineData("certbot-manage", "certbot.manage")]
    [InlineData("portsentry-manage", "portsentry.manage")]
    [InlineData("firewall-manage", "firewall.manage")]
    [InlineData("mail-setup", "mail.manage")]
    [InlineData("teamspeak-setup", "teamspeak.manage")]
    public async Task EachAcceptedComponent_IsCarriedToTheAgentAndExpectedInTheHeartbeat(string token, string? capability)
    {
        await using var db = CreateDb();
        db.Servers.Add(new Server { Id = 1, Name = "srv-1", Hostname = "srv-1", OsType = OsType.Linux });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentUpdateRepository(db, new FakeTimeProvider(Now));
        var release = new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 };

        var (request, _) = await repository.ReserveAsync(
            (await db.Servers.FindAsync([1], TestContext.Current.CancellationToken))!, release, "admin",
            TestContext.Current.CancellationToken, [token]);
        var (_, task, _) = await repository.TryQueueAsync(request.Id, TestContext.Current.CancellationToken);

        Assert.Equal(token, request.EnableCapabilities);
        var expected = JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson)!;
        if (capability is null)
            Assert.DoesNotContain(expected, entry => entry.EndsWith(".manage", StringComparison.Ordinal));
        else
            Assert.Contains(capability, expected);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(task!.EnvironmentVariables!)!;
        Assert.Equal(token, environment[AgentEnableableCapabilities.EnvironmentVariable]);
    }

    [Fact]
    public async Task SeveralComponents_AreAllExpectedInTheHeartbeat_AndNothingElse()
    {
        await using var db = CreateDb();
        db.Servers.Add(new Server { Id = 1, Name = "srv-1", Hostname = "srv-1", OsType = OsType.Linux });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentUpdateRepository(db, new FakeTimeProvider(Now));

        var (request, _) = await repository.ReserveAsync(
            (await db.Servers.FindAsync([1], TestContext.Current.CancellationToken))!,
            new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 }, "admin",
            TestContext.Current.CancellationToken,
            [AgentEnableableCapabilities.MailSetup, AgentEnableableCapabilities.PatchManage, AgentEnableableCapabilities.CronManage]);

        Assert.Equal("patch-manage,cron-manage,mail-setup", request.EnableCapabilities);
        var expected = JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson)!;
        Assert.Contains(AgentCapabilities.PatchManagement, expected);
        Assert.Contains(AgentCapabilities.CronManagement, expected);
        Assert.Contains(AgentCapabilities.MailManagement, expected);
        Assert.DoesNotContain(AgentCapabilities.FirewallManagement, expected);
    }

    [Fact]
    public async Task APlainUpdate_LeavesTheTaskEnvironmentAndTheExpectationsAsTheyWere()
    {
        await using var db = CreateDb();
        var server = new Server { Id = 1, Name = "srv-1", Hostname = "srv-1" };
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentUpdateRepository(db, new FakeTimeProvider(Now));

        var (request, _) = await repository.ReserveAsync(
            server, new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 }, "admin",
            TestContext.Current.CancellationToken);
        var (_, task, _) = await repository.TryQueueAsync(request.Id, TestContext.Current.CancellationToken);

        Assert.Null(request.EnableCapabilities);
        Assert.DoesNotContain(AgentCapabilities.PatchManagement,
            JsonSerializer.Deserialize<List<string>>(request.ExpectedCapabilitiesJson)!);
        Assert.False(JsonSerializer.Deserialize<Dictionary<string, string>>(task!.EnvironmentVariables!)!
            .ContainsKey(AgentEnableableCapabilities.EnvironmentVariable));
    }

    [Fact]
    public async Task AWaitingRequest_TakesTheGrantAskedForLater_ButAQueuedOneDoesNot()
    {
        await using var db = CreateDb();
        var server = new Server { Id = 1, Name = "srv-1", Hostname = "srv-1" };
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentUpdateRepository(db, new FakeTimeProvider(Now));
        var release = new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 };
        var (waiting, _) = await repository.ReserveAsync(
            server, release, "admin", TestContext.Current.CancellationToken);

        var (reused, created) = await repository.ReserveAsync(
            server, release, "admin", TestContext.Current.CancellationToken, PatchManage);

        Assert.False(created);
        Assert.Equal(waiting.Id, reused.Id);
        Assert.Equal(AgentEnableableCapabilities.PatchManage, reused.EnableCapabilities);

        // Once the task exists its environment is fixed: the request is reused as it is.
        reused.EnableCapabilities = null;
        reused.TaskId = 99;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var (fixedRequest, _) = await repository.ReserveAsync(
            server, release, "admin", TestContext.Current.CancellationToken, PatchManage);
        Assert.Null(fixedRequest.EnableCapabilities);
    }

    [Fact]
    public async Task ARequestedCapabilityOutsideTheWhitelist_StopsTheReservationInsteadOfGrantingNothing()
    {
        await using var db = CreateDb();
        var server = new Server { Id = 1, Name = "srv-1", Hostname = "srv-1" };
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentUpdateRepository(db, new FakeTimeProvider(Now));

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReserveAsync(
            server, new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 }, "admin",
            TestContext.Current.CancellationToken, ["docker"]));
    }

    [Fact]
    public async Task AStoredGrantOutsideTheWhitelist_IsNotMergedAndNotRewritten()
    {
        await using var db = CreateDb();
        var server = new Server { Id = 1, Name = "srv-1", Hostname = "srv-1" };
        db.Servers.Add(server);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AgentUpdateRepository(db, new FakeTimeProvider(Now));
        var release = new AgentReleaseManifestDto { SoftwareVersion = "1.0.0", ProtocolVersion = 2 };
        var (waiting, _) = await repository.ReserveAsync(
            server, release, "admin", TestContext.Current.CancellationToken);
        waiting.EnableCapabilities = "docker";
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() => repository.ReserveAsync(
            server, release, "admin", TestContext.Current.CancellationToken, PatchManage));
        Assert.Equal("docker", waiting.EnableCapabilities);
    }

    private static AppDbContext CreateDb() => new(
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
        new FakeTimeProvider(Now));
}
