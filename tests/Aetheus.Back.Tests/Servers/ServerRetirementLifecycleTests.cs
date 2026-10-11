// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Auth;
using Aetheus.Back.Components.Environments;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Servers;
using Aetheus.Back.Components.Tasks;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Aetheus.Back.Tests.Servers.RetiredServerScenario;

namespace Aetheus.Back.Tests.Servers;

/// <summary>
/// PLAN-004 R-11: deleting a server retires it. The row and every link survive, the agent can no
/// longer authenticate, and the server leaves lists, selectors and dispatch; only an explicit purge
/// of a retired server erases it, backup policies included.
/// </summary>
public sealed class ServerRetirementLifecycleTests
{
    private readonly RetiredServerScenario _scenario = new();

    [Fact]
    public async Task Retire_KeepsTheRowEveryLinkAndTheSettings_AndRevokesTheAgentTokens()
    {
        await _scenario.SeedAsync();

        await _scenario.RetireAsync();

        var server = await _scenario.LoadIncludingRetiredAsync(ServerId);
        Assert.Equal(_scenario.Now, server.DeletedAt);
        Assert.Equal(ServerStatus.Offline, server.Status);
        Assert.Equal("[\"prod\",\"web\"]", server.Tags);
        Assert.True(server.PipelineRunnerEnabled);
        Assert.True(server.RequireContainerIsolation);
        Assert.Equal(MachineHash, server.MachineIdHash);

        await using var db = _scenario.NewContext();
        var all = new[] { ServerQueryFilters.ExcludeRetired };
        Assert.Single(await db.EnvironmentServers.IgnoreQueryFilters(all).Where(link => link.ServerId == ServerId).ToListAsync(Ct));
        Assert.Single(await db.AgentPoolServers.IgnoreQueryFilters(all).Where(link => link.ServerId == ServerId).ToListAsync(Ct));
        Assert.Single(await db.ServerPortReservations.Where(reservation => reservation.ServerId == ServerId).ToListAsync(Ct));
        Assert.Single(await db.ServerPortRanges.Where(range => range.ServerId == ServerId).ToListAsync(Ct));
        Assert.Single(await db.ProjectServers.Where(projectServer => projectServer.ServerId == ServerId).ToListAsync(Ct));
        Assert.Single(await db.VariableLibraries.Where(library => library.ProjectServerId == ProjectServerId).ToListAsync(Ct));
        Assert.Single(await db.Vaults.Where(vault => vault.ProjectServerId == ProjectServerId).ToListAsync(Ct));
        Assert.All(await db.ServerTokens.Where(token => token.ServerId == ServerId).ToListAsync(Ct), token => Assert.True(token.IsRevoked));
        await _scenario.Audit.Received(1).LogAsync("Retired", "Server", ServerId, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RetiredServer_LeavesTheListsSelectorsAndEnvironmentViews()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();

        await using var db = _scenario.NewContext();
        var servers = new ServerRepository(db, _scenario.Clock);
        var (items, total) = await servers.GetServersPagedProjectedAsync(null, null, false, null, null, 1, 25, ct: Ct);
        Assert.Equal(OtherServerId, Assert.Single(items).Id);
        Assert.Equal(1, total);
        Assert.DoesNotContain(Hostname, await servers.GetServerNamesAsync(Ct));
        Assert.Null(await servers.FindServerAsync(ServerId, Ct));
        Assert.False(await servers.ServerExistsAsync(ServerId, Ct));
        var environment = await new EnvironmentRepository(db).GetEnvironmentWithServersAsync(EnvironmentId, Ct);
        Assert.NotNull(environment);
        Assert.Empty(environment.Servers);
    }

    [Fact]
    public async Task RetiredServer_IsNeverResolvedForDispatch_EvenIfItStillReadsOnline()
    {
        await _scenario.SeedAsync();
        // Retirement sets Offline, which alone would already block the Online-gated resolvers. Keep
        // the row Online here so the only thing excluding it is the retirement itself.
        await using (var seed = _scenario.NewContext())
        {
            var retired = await seed.Servers.SingleAsync(server => server.Id == ServerId, Ct);
            retired.DeletedAt = _scenario.Now;
            await seed.SaveChangesAsync(Ct);
        }

        await using var db = _scenario.NewContext();
        var resolver = new PipelineServerResolver(db);
        Assert.Null(await resolver.FindOnlineServerByAgentAsync(Hostname, ct: Ct));
        Assert.Null(await resolver.FindOnlineServerByAgentAsync("prod", ct: Ct));
        Assert.Null(await resolver.FindOnlineServerInPoolAsync(PoolName, ct: Ct));
        Assert.Null(await resolver.FindOnlineServerInEnvironmentAsync(EnvironmentName, projectId: null, ct: Ct));
        Assert.Null(await resolver.FindOnlineServerByIdAsync(ServerId, ct: Ct));
        Assert.Equal(OtherServerId, (await resolver.FindOnlineServerByAgentAsync("default", ct: Ct))?.Id);
    }

    [Fact]
    public async Task EnvironmentEdit_WhileTheServerIsRetired_KeepsItsLink()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();

        // What EnvironmentService.UpdateEnvironmentAsync does: load, replace the links, save.
        await using (var db = _scenario.NewContext())
        {
            var environment = await new EnvironmentRepository(db).FindEnvironmentAsync(EnvironmentId, Ct);
            Assert.NotNull(environment);
            environment.Servers.Clear();
            environment.Servers.Add(new EnvironmentServer { EnvironmentId = EnvironmentId, ServerId = OtherServerId });
            await db.SaveChangesAsync(Ct);
        }

        await using var verify = _scenario.NewContext();
        var linked = await verify.EnvironmentServers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired])
            .Where(link => link.EnvironmentId == EnvironmentId)
            .Select(link => link.ServerId)
            .OrderBy(id => id)
            .ToListAsync(Ct);
        Assert.Equal([ServerId, OtherServerId], linked);
    }

    [Fact]
    public async Task RetiredServerToken_NoLongerAuthenticates_EvenBeforeItIsRevoked()
    {
        await _scenario.SeedAsync();
        await using (var seed = _scenario.NewContext())
        {
            var retired = await seed.Servers.SingleAsync(server => server.Id == ServerId, Ct);
            retired.DeletedAt = _scenario.Now;
            await seed.SaveChangesAsync(Ct);
        }

        await using var db = _scenario.NewContext();
        Assert.Null(await new AuthRepository(db, _scenario.Clock).ValidateServerTokenHashAsync(OldTokenHash, Ct));
    }

    [Fact]
    public async Task PendingWorkQueuedForARetiredServer_StillAgesOut()
    {
        await _scenario.SeedAsync();
        await using (var seed = _scenario.NewContext())
        {
            seed.Tasks.Add(new ServerTask { ServerId = ServerId, Name = "build", Status = TaskExecutionStatus.Pending });
            await seed.SaveChangesAsync(Ct);
        }
        await _scenario.RetireAsync();
        _scenario.Clock.Advance(TimeSpan.FromMinutes(10));

        await using var db = _scenario.NewContext();
        var stale = await new TaskRepository(db, _scenario.Clock).GetStalePendingTasksAsync(TimeSpan.FromMinutes(5), Ct);

        Assert.Equal(ServerId, Assert.Single(stale).ServerId);
    }

    [Fact]
    public async Task RunHistory_StillNamesARetiredServer()
    {
        await _scenario.SeedAsync();
        int runId;
        await using (var seed = _scenario.NewContext())
        {
            var pipeline = new Pipeline { Name = "shop-deploy", YamlDefinition = "y", ProjectId = ProjectId };
            seed.Pipelines.Add(pipeline);
            await seed.SaveChangesAsync(Ct);
            var run = new PipelineRun { PipelineId = pipeline.Id, Status = PipelineStatus.Success, StartedAt = _scenario.Now };
            seed.PipelineRuns.Add(run);
            await seed.SaveChangesAsync(Ct);
            seed.PipelineStepRuns.Add(new PipelineStepRun { PipelineRunId = run.Id, StageName = "deploy", StepName = "deploy", Order = 1, ServerId = ServerId });
            await seed.SaveChangesAsync(Ct);
            runId = run.Id;
        }
        await _scenario.RetireAsync();

        await using var db = _scenario.NewContext();
        var repository = new PipelineRepository(db, _scenario.Clock, NullLogger<PipelineRepository>.Instance,
            new PipelineTaskLifecycleRepository(db, _scenario.Clock), new PipelineRunLineageRepository(db));
        var detail = await repository.GetRunDetailAsync(runId, Ct);

        Assert.NotNull(detail);
        Assert.Equal(Hostname, Assert.Single(detail.StepRuns).Server?.Name);
    }

    [Fact]
    public async Task RetiredServers_AreListedForPurge_WithinTheCallersScope()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();

        await using var db = _scenario.NewContext();
        var service = _scenario.Retirement(db);
        var visible = await service.GetRetiredServersAsync(new PaginationRequest(), null, Ct);
        var hidden = await service.GetRetiredServersAsync(new PaginationRequest(), [OtherServerId], Ct);

        var listed = Assert.Single(visible.Items);
        Assert.Equal(ServerId, listed.Id);
        Assert.Equal(_scenario.Now, listed.RetiredAt);
        Assert.True(listed.HasMachineIdentity);
        Assert.Empty(hidden.Items);
    }

    [Fact]
    public async Task RetiringTwice_ReportsNotFound()
    {
        await _scenario.SeedAsync();
        await _scenario.RetireAsync();

        await using var db = _scenario.NewContext();
        Assert.False(await _scenario.Retirement(db).RetireServerAsync(ServerId, "operator", Ct));
    }

    [Fact]
    public async Task Purge_OfARetiredServer_ErasesItWithItsBackupPolicies()
    {
        await _scenario.SeedAsync();
        await using (var seed = _scenario.NewContext())
        {
            var policy = new BackupPolicy { Name = "shop-db", ProjectId = ProjectId, ServerId = ServerId, ScheduleCron = "0 3 * * *" };
            seed.BackupPolicies.Add(policy);
            await seed.SaveChangesAsync(Ct);
            seed.BackupRuns.Add(new BackupRun { BackupPolicyId = policy.Id });
            await seed.SaveChangesAsync(Ct);
        }
        await _scenario.RetireAsync();

        await using (var db = _scenario.NewContext())
            Assert.True(await _scenario.Retirement(db).PurgeServerAsync(ServerId, "operator", Ct));

        await using var verify = _scenario.NewContext();
        Assert.False(await verify.Servers.IgnoreQueryFilters([ServerQueryFilters.ExcludeRetired]).AnyAsync(server => server.Id == ServerId, Ct));
        Assert.False(await verify.BackupPolicies.AnyAsync(policy => policy.ServerId == ServerId, Ct));
        Assert.False(await verify.BackupRuns.AnyAsync(Ct));
        await _scenario.Audit.Received(1).LogAsync("Purged", "Server", ServerId,
            Arg.Is<string?>(detail => detail != null && detail.Contains("1 backup policy", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_OfAnActiveServer_IsRefusedAndErasesNothing()
    {
        await _scenario.SeedAsync();

        await using (var db = _scenario.NewContext())
            await Assert.ThrowsAsync<ConflictException>(() => _scenario.Retirement(db).PurgeServerAsync(ServerId, "operator", Ct));

        var server = await _scenario.LoadIncludingRetiredAsync(ServerId);
        Assert.Null(server.DeletedAt);
        await _scenario.Audit.DidNotReceive().LogAsync("Purged", Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Purge_OfAnUnknownServer_ReportsNotFound()
    {
        await _scenario.SeedAsync();

        await using var db = _scenario.NewContext();
        Assert.False(await _scenario.Retirement(db).PurgeServerAsync(404, "operator", Ct));
    }
}
