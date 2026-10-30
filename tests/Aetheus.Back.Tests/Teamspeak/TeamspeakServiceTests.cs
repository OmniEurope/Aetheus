// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Teamspeak;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class TeamspeakServiceTests
{
    private readonly ITeamspeakRepository _repo = Substitute.For<ITeamspeakRepository>();
    private readonly Aetheus.Back.Components.Servers.IServerRepository _serverRepo = Substitute.For<Aetheus.Back.Components.Servers.IServerRepository>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private readonly Aetheus.Back.Components.Tasks.ITaskService _taskService = Substitute.For<Aetheus.Back.Components.Tasks.ITaskService>();
    private readonly TeamspeakService _sut;

    public TeamspeakServiceTests()
    {
        // Encrypted payloads must not start with '{' (TaskEnvProtection treats '{'-prefixed values as
        // plaintext) - prefix a marker so a Protect→Unprotect round-trip in tests is unambiguous.
        _encryption.EncryptValue(Arg.Any<string>()).Returns(ci => "ENC:" + ci.Arg<string>());
        _sut = new TeamspeakService(_repo, _serverRepo, _audit, _encryption, _taskService);
    }

    // --- GetStateAsync ---

    [Fact]
    public async Task GetStateAsync_NoState_ReturnsEmptyDto()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((TeamspeakState?)null);

        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.False(result.IsInstalled);
        Assert.Empty(result.Channels);
        Assert.Empty(result.Clients);
        Assert.Empty(result.Bans);
    }

    [Fact]
    public async Task GetStateAsync_WithState_ReturnsPopulatedDto()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState
        {
            ServerId = 1,
            IsRunning = true,
            Version = "3.13.7",
            Platform = "Linux",
            ServerName = "My TS",
            VoicePort = 9987,
            QueryPort = 10011,
            MaxClients = 32,
            OnlineClients = 5,
            ChannelCount = 3,
            UptimeSeconds = 7200
        });
        var result = await _sut.GetStateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result.IsInstalled);
        Assert.True(result.IsRunning);
        Assert.Equal("3.13.7", result.Version);
        Assert.Equal("Linux", result.Platform);
        Assert.Equal("My TS", result.ServerName);
        Assert.Equal(9987, result.VoicePort);
        Assert.Equal(10011, result.QueryPort);
        Assert.Equal(32, result.MaxClients);
        Assert.Equal(5, result.OnlineClients);
        Assert.Equal(3, result.ChannelCount);
        Assert.Empty(result.Channels);
    }

    [Fact]
    public async Task GetChannelsAsync_ReturnsMappedPage()
    {
        _repo.GetChannelsPagedAsync(1, "room", 2, 10, "Name", true, TestContext.Current.CancellationToken)
            .Returns((new List<TeamspeakChannel>
            {
                new() { ChannelId = 7, Name = "Room", IsPermanent = true }
            }, 11));

        var result = await _sut.GetChannelsAsync(1, new PaginationRequest
        {
            Search = "room",
            Page = 2,
            PageSize = 10,
            SortBy = "Name",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(11, result.TotalCount);
        Assert.Equal(7, Assert.Single(result.Items).Id);
    }

    [Fact]
    public async Task GetClientsAsync_ReturnsMappedPage()
    {
        _repo.GetClientsPagedAsync(1, null, 1, 25, "Nickname", false, TestContext.Current.CancellationToken)
            .Returns((new List<TeamspeakClient>
            {
                new() { ClientId = 8, Nickname = "Alice", ChannelName = "Lobby" }
            }, 1));

        var result = await _sut.GetClientsAsync(1, new PaginationRequest
        {
            Page = 1,
            PageSize = 25,
            SortBy = "Nickname"
        }, ct: TestContext.Current.CancellationToken);

        var client = Assert.Single(result.Items);
        Assert.Equal("Alice", client.Nickname);
        Assert.Equal("Lobby", client.ChannelName);
    }

    // --- ExecuteActionAsync ---

    [Theory]
    [InlineData(TeamspeakAction.Start)]
    [InlineData(TeamspeakAction.Stop)]
    [InlineData(TeamspeakAction.Restart)]
    public async Task ExecuteActionAsync_CreatesTaskAndAudits(TeamspeakAction action)
    {
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.ExecuteActionAsync(1, new TeamspeakActionRequest { Action = action }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(1, captured.ServerId);
        Assert.Contains("TeamSpeak", captured.Name);
        Assert.Equal(ExecutorType.Operation, captured.Executor);
        await _audit.Received(1).LogAsync(Arg.Is<string>(s => s.Contains("Teamspeak")), "Teamspeak", 1, Arg.Any<string>(), TestContext.Current.CancellationToken);
    }

    // --- SetupAsync ---

    [Fact]
    public async Task SetupAsync_ValidPath_CreatesTypedOperationTaskAndAudits()
    {
        _serverRepo.FindServerAsync(1, TestContext.Current.CancellationToken).Returns(new Server { TeamspeakSetupAvailable = true });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.SetupAsync(1, new TeamspeakSetupRequest
        {
            InstallPath = "/opt/teamspeak",
            VoicePort = 9987,
            QueryPort = 10011
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        // The typed controlled-sudo operation, not the retired free-form shell pipeline.
        Assert.Equal(OperationKind.TeamspeakSetup, captured.Operation);
        Assert.Equal("/opt/teamspeak", captured.Command);
        Assert.Contains("9987", captured.EnvironmentVariables);
        Assert.Contains("10011", captured.EnvironmentVariables);
        await _audit.Received(1).LogAsync("TeamspeakSetup", "Teamspeak", 1, "/opt/teamspeak", TestContext.Current.CancellationToken);
        await _taskService.Received(1).NotifyTaskQueuedAsync(Arg.Any<ServerTask>(), ct: Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SetupAsync_CapabilityDisabled_ThrowsBadRequest()
    {
        // Server reports no teamspeak-setup sudoers grant - the install must be refused, not queued.
        _serverRepo.FindServerAsync(1, TestContext.Current.CancellationToken).Returns(new Server { TeamspeakSetupAvailable = false });
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SetupAsync(1, new TeamspeakSetupRequest { InstallPath = "/opt/teamspeak" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SetupAsync_InvalidPath_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SetupAsync(1, new TeamspeakSetupRequest { InstallPath = "invalid path!!" }, ct: TestContext.Current.CancellationToken));
    }

    // --- GetLogsAsync ---

    [Fact]
    public async Task GetLogsAsync_DispatchesTypedGetLogsOpWithInstallPath()
    {
        ServerTask? captured = null;
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { InstallPath = "/opt/teamspeak" });
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        // The old shell `tail | sort | tail` is gone; the line count is now bounded agent-side. Assert the
        // typed op carries the install path (the agent tails {installPath}/logs/ directly).
        await _sut.GetLogsAsync(1, new TeamspeakLogRequest { Lines = 9999 }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("logs", captured.Name);
        Assert.Equal(Aetheus.Shared.Components.Tasks.ExecutorType.Operation, captured.Executor);
        Assert.Equal(Aetheus.Shared.Components.Tasks.OperationKind.TeamspeakGetLogs, captured.Operation);
        Assert.Equal("/opt/teamspeak", captured.Command);
    }

    [Fact]
    public async Task GetLogsAsync_NoState_UsesEmptyInstallPath()
    {
        ServerTask? captured = null;
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((TeamspeakState?)null);
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.GetLogsAsync(1, new TeamspeakLogRequest { Lines = 50 }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(Aetheus.Shared.Components.Tasks.OperationKind.TeamspeakGetLogs, captured.Operation);
        Assert.Equal(string.Empty, captured.Command);
    }

    [Fact]
    public async Task GracefulRestartAsync_DispatchesTypedCompositeOpWithWarnParams()
    {
        ServerTask? captured = null;
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { InstallPath = "/opt/teamspeak", QueryPort = 10011 });
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.GracefulRestartAsync(1, new TeamspeakGracefulRestartRequest { WarningSeconds = 30, WarningMessage = "Restart in {0}s" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(Aetheus.Shared.Components.Tasks.ExecutorType.Operation, captured.Executor);
        Assert.Equal(Aetheus.Shared.Components.Tasks.OperationKind.TeamspeakGracefulRestart, captured.Operation);
        // Warning seconds/message + the live query port travel in env (no shell chain).
        Assert.Contains("10011", captured.EnvironmentVariables);
        Assert.Contains("30", captured.EnvironmentVariables);
        Assert.Equal(90, captured.TimeoutSeconds); // 30s warn + 60s slack
        await _audit.Received(1).LogAsync("TeamspeakGracefulRestart", "Teamspeak", 1, "warn=30s", TestContext.Current.CancellationToken);
    }

    // --- KickClientAsync ---

    [Fact]
    public async Task KickClientAsync_Installed_CreatesTaskAndAudits()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.KickClientAsync(1, new TeamspeakKickRequest { ClientId = 42, ReasonMessage = "AFK" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("kick", captured.Name);
        Assert.Contains("clientkick", captured.Command);
        Assert.Contains("clid=42", captured.Command);
        await _audit.Received(1).LogAsync("TeamspeakKick", "Teamspeak", 1, "Client 42", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task KickClientAsync_DispatchesTypedServerQueryOp_NotShell()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.KickClientAsync(1, new TeamspeakKickRequest { ClientId = 42, ReasonMessage = "AFK" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        // S-TECH-87: ServerQuery actions are typed operations (agent native TCP), not shell/nc tasks.
        Assert.Equal(ExecutorType.Operation, captured.Executor);
        Assert.Equal(OperationKind.TeamspeakServerQuery, captured.Operation);
        Assert.DoesNotContain("| nc", captured.Command);
        Assert.DoesNotContain("printf", captured.Command);
        Assert.DoesNotContain("login serveradmin", captured.Command);
        // The query port rides in the env var, not baked into a shell string.
        Assert.Contains("\"TEAMSPEAK_QUERY_PORT\":\"10011\"", captured.EnvironmentVariables);
    }

    // R-119: start/stop/restart were `systemctl ... teamspeak3` shell strings, run without root and carrying no kind,
    // so the agent never collected its TeamSpeak inventory again. They are typed operations with a fixed target now.
    [Theory]
    [InlineData(TeamspeakAction.Start, OperationKind.TeamspeakStart)]
    [InlineData(TeamspeakAction.Stop, OperationKind.TeamspeakStop)]
    [InlineData(TeamspeakAction.Restart, OperationKind.TeamspeakRestart)]
    public async Task ExecuteActionAsync_QueuesTheTypedOperation_NeverAShellCommand(TeamspeakAction action, OperationKind expected)
    {
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.ExecuteActionAsync(1, new TeamspeakActionRequest { Action = action }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal(ExecutorType.Operation, captured.Executor);
        Assert.Equal(expected, captured.Operation);
        Assert.Equal("-", captured.Command);
        Assert.True(OperationTargetValidator.IsValid(expected, captured.Command));
    }

    [Fact]
    public async Task ExecuteActionAsync_UnknownAction_ThrowsBadRequest_AndQueuesNothing()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ExecuteActionAsync(
            1, new TeamspeakActionRequest { Action = (TeamspeakAction)999 }, ct: TestContext.Current.CancellationToken));
        await _repo.DidNotReceive().AddTaskAsync(Arg.Any<ServerTask>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task KickClientAsync_NotInstalled_ThrowsBadRequest()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((TeamspeakState?)null);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.KickClientAsync(1, new TeamspeakKickRequest { ClientId = 1 }, ct: TestContext.Current.CancellationToken));
    }

    // --- BanClientAsync ---

    [Fact]
    public async Task BanClientAsync_Installed_CreatesTaskAndAudits()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.BanClientAsync(1, new TeamspeakBanRequest { ClientUniqueId = "uid123", DurationSeconds = 3600, Reason = "spam" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("ban", captured.Name);
        Assert.Contains("banadd", captured.Command);
        Assert.Contains("uid123", captured.Command);
        await _audit.Received(1).LogAsync("TeamspeakBan", "Teamspeak", 1, "uid123", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task BanClientAsync_NotInstalled_ThrowsBadRequest()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((TeamspeakState?)null);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.BanClientAsync(1, new TeamspeakBanRequest { ClientUniqueId = "uid" }, ct: TestContext.Current.CancellationToken));
    }

    // --- GetBansAsync ---

    [Fact]
    public async Task GetBansAsync_ReturnsPersistedPage()
    {
        _repo.GetBansPagedAsync(1, null, 1, 25, "Created", true, TestContext.Current.CancellationToken)
            .Returns((new List<TeamspeakBan>
            {
                new() { BanId = 4, Nickname = "Blocked", Reason = "spam" }
            }, 1));

        var result = await _sut.GetBansAsync(1, new PaginationRequest
        {
            Page = 1,
            PageSize = 25,
            SortBy = "Created",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        var ban = Assert.Single(result.Items);
        Assert.Equal(4, ban.BanId);
        Assert.Equal("spam", ban.Reason);
    }

    // --- UnbanAsync ---

    [Fact]
    public async Task UnbanAsync_Installed_CreatesTaskAndAudits()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.UnbanAsync(1, 77, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("bandel", captured.Command);
        Assert.Contains("banid=77", captured.Command);
        await _audit.Received(1).LogAsync("TeamspeakUnban", "Teamspeak", 1, "Ban 77", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnbanAsync_NotInstalled_ThrowsBadRequest()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((TeamspeakState?)null);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UnbanAsync(1, 1, ct: TestContext.Current.CancellationToken));
    }

    // --- CreateChannelAsync ---

    // S-TECH-W9K7: a channel created WITH a password is sensitive - the command (carrying the password)
    // must be redacted out of the plaintext-at-rest Command and moved into the encrypted env instead.
    [Fact]
    public async Task CreateChannelAsync_WithPassword_RedactsCommandAndEncryptsEnv()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.CreateChannelAsync(1, new TeamspeakCreateChannelRequest
        {
            Name = "Lobby",
            ParentId = null,
            Password = "secret",
            MaxClients = 10,
            IsPermanent = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.DoesNotContain("channelcreate", captured.Command);
        Assert.DoesNotContain("secret", captured.Command);
        Assert.StartsWith("ENC:", captured.EnvironmentVariables); // command routed through encrypted env
        Assert.Contains("Lobby", captured.Name);
        await _audit.Received(1).LogAsync("TeamspeakCreateChannel", "Teamspeak", 1, "Lobby", TestContext.Current.CancellationToken);
    }

    // A passwordless channel carries no secret - the command stays readable in Command (env plaintext).
    [Fact]
    public async Task CreateChannelAsync_NoPassword_KeepsReadableCommand()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.CreateChannelAsync(1, new TeamspeakCreateChannelRequest
        {
            Name = "Lobby",
            ParentId = null,
            Password = null,
            MaxClients = 10,
            IsPermanent = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("channelcreate", captured.Command);
        Assert.DoesNotContain("ENC:", captured.EnvironmentVariables);
        await _audit.Received(1).LogAsync("TeamspeakCreateChannel", "Teamspeak", 1, "Lobby", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task CreateChannelAsync_InvalidName_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateChannelAsync(1, new TeamspeakCreateChannelRequest { Name = "bad|name" }, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateChannelAsync_NotInstalled_ThrowsBadRequest()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((TeamspeakState?)null);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.CreateChannelAsync(1, new TeamspeakCreateChannelRequest { Name = "Test" }, ct: TestContext.Current.CancellationToken));
    }

    // --- EditChannelAsync ---

    [Fact]
    public async Task EditChannelAsync_Valid_CreatesTask()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.EditChannelAsync(1, new TeamspeakEditChannelRequest
        {
            ChannelId = 5,
            Name = "New Name",
            MaxClients = 20
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("channeledit", captured.Command);
        Assert.Contains("cid=5", captured.Command);
        await _audit.Received(1).LogAsync("TeamspeakEditChannel", "Teamspeak", 1, Arg.Any<string>(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EditChannelAsync_InvalidName_ThrowsBadRequest()
    {
        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.EditChannelAsync(1, new TeamspeakEditChannelRequest { ChannelId = 1, Name = "bad|name" }, ct: TestContext.Current.CancellationToken));
    }

    // --- DeleteChannelAsync ---

    [Fact]
    public async Task DeleteChannelAsync_Installed_CreatesTask()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.DeleteChannelAsync(1, 99, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("channeldelete", captured.Command);
        Assert.Contains("cid=99", captured.Command);
        await _audit.Received(1).LogAsync("TeamspeakDeleteChannel", "Teamspeak", 1, "Channel 99", TestContext.Current.CancellationToken);
    }

    // --- EditServerAsync ---

    [Fact]
    public async Task EditServerAsync_Installed_CreatesTaskAndAudits()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.EditServerAsync(1, new TeamspeakServerEditRequest
        {
            ServerName = "My Server",
            MaxClients = 64,
            WelcomeMessage = "Welcome!"
        }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("serveredit", captured.Command);
        await _audit.Received(1).LogAsync("TeamspeakEditServer", "Teamspeak", 1, "My Server", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task EditServerAsync_NullName_UsesSettingsAuditDetail()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken).Returns(Task.CompletedTask);

        await _sut.EditServerAsync(1, new TeamspeakServerEditRequest { MaxClients = 16 }, ct: TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync("TeamspeakEditServer", "Teamspeak", 1, "settings", TestContext.Current.CancellationToken);
    }

    // --- SendGlobalMessageAsync ---

    [Fact]
    public async Task SendGlobalMessageAsync_Installed_CreatesTask()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns(new TeamspeakState { QueryPort = 10011 });
        ServerTask? captured = null;
        _repo.AddTaskAsync(Arg.Any<ServerTask>(), TestContext.Current.CancellationToken)
            .Returns(ci => { captured = ci.Arg<ServerTask>(); return Task.CompletedTask; });

        await _sut.SendGlobalMessageAsync(1, new TeamspeakGlobalMessageRequest { Message = "Hello everyone" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Contains("sendtextmessage", captured.Command);
        await _audit.Received(1).LogAsync("TeamspeakMessage", "Teamspeak", 1, "Global message", TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SendGlobalMessageAsync_NotInstalled_ThrowsBadRequest()
    {
        _repo.GetStateAsync(1, TestContext.Current.CancellationToken).Returns((TeamspeakState?)null);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.SendGlobalMessageAsync(1, new TeamspeakGlobalMessageRequest { Message = "test" }, ct: TestContext.Current.CancellationToken));
    }
}
