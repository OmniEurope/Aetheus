// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-11 (user decision of 2026-11-10): a pipeline imported from an external repository mirror runs only on the servers
/// linked to its project, whatever runner the organization offers. Real ready-stage selector, real dispatch resolver
/// and real runner queries on an in-memory database.
/// </summary>
public sealed class PipelineExternalImportDispatchTests : IDisposable
{
    private const int OrganizationId = 4;
    private const int ProjectId = 7;
    private const int ImportedRunId = 50;
    private const int OwnRunId = 51;
    private const int Unlinked = 1;
    private const int Linked = 2;

    private readonly AppDbContext _db;
    private readonly PipelineServerResolver _targets;
    private readonly PipelineDispatchServerResolver _dispatch;
    private readonly PipelineReadyStageServerSelector _selector;

    public PipelineExternalImportDispatchTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
        _targets = new PipelineServerResolver(_db);
        _dispatch = new PipelineDispatchServerResolver(_targets);
        _selector = new PipelineReadyStageServerSelector(_targets, _dispatch, NullLogger.Instance);
        _db.Pipelines.AddRange(
            new Pipeline
            {
                Id = 5,
                Name = "oe1-ci",
                ProjectId = ProjectId,
                YamlDefinition = "name: oe1-ci",
                ExternalImportPath = ".pipeline/oe1-ci.yaml",
                ExternalImportCommit = "0123456789abcdef0123456789abcdef01234567"
            },
            new Pipeline { Id = 6, Name = "own-ci", ProjectId = ProjectId, YamlDefinition = "name: own-ci" });
        _db.PipelineRuns.AddRange(
            new PipelineRun { Id = ImportedRunId, PipelineId = 5 },
            new PipelineRun { Id = OwnRunId, PipelineId = 6 });
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static PipelineStageDefinition Stage => new() { Name = "Build" };

    private void AddServer(int id, ServerStatus status = ServerStatus.Online, bool linked = false)
    {
        _db.Servers.Add(new Server
        {
            Id = id,
            Name = $"runner-{id}",
            Hostname = $"runner-{id}",
            OrganizationId = OrganizationId,
            Status = status,
            OsType = OsType.Linux,
            PipelineRunnerEnabled = true,
            Tags = "[]",
            AgentProtocolVersion = AgentProtocol.CurrentVersion,
            AgentCapabilitiesJson = $"[\"{AgentCapabilities.PipelineBuild}\"]"
        });
        if (linked)
            _db.ProjectServers.Add(new ProjectServer { Id = 100 + id, ProjectId = ProjectId, ServerId = id, DisplayName = $"runner-{id}" });
    }

    private Task<ReadyStageServerResolution> ResolveAsync(int runId, int? affinity = null) =>
        _selector.ResolveAsync(runId, "Build", Stage, new Dictionary<string, string>(), OrganizationId, false, affinity, ProjectId, Ct);

    [Fact]
    public async Task ImportedPipeline_RunsOnAServerLinkedToItsProject_NeverOnAnotherRunnerOfTheOrganization()
    {
        AddServer(Unlinked);
        AddServer(Linked, linked: true);
        await _db.SaveChangesAsync(Ct);

        var resolution = await ResolveAsync(ImportedRunId);

        Assert.Equal(Linked, resolution.Server?.Id);
    }

    [Fact]
    public async Task ImportedPipeline_ProjectWithoutALinkedRunner_FailsTheStageInsteadOfUsingAnotherRunner()
    {
        AddServer(Unlinked);
        await _db.SaveChangesAsync(Ct);

        var resolution = await ResolveAsync(ImportedRunId);

        Assert.Null(resolution.Server);
        Assert.False(resolution.RunnerTemporarilyUnavailable);
        Assert.Contains("linked to its project", resolution.Reason, StringComparison.Ordinal);
        // The same organization runner is what a pipeline defined in Aetheus takes.
        Assert.Equal(Unlinked, (await ResolveAsync(OwnRunId)).Server?.Id);
    }

    [Fact]
    public async Task ImportedPipeline_AffinityOnAnUnlinkedServer_IsIgnored()
    {
        AddServer(Unlinked);
        AddServer(Linked, linked: true);
        await _db.SaveChangesAsync(Ct);

        var resolution = await ResolveAsync(ImportedRunId, affinity: Unlinked);

        Assert.Equal(Linked, resolution.Server?.Id);
    }

    [Fact]
    public async Task ImportedPipeline_LinkedRunnerOffline_Waits()
    {
        AddServer(Unlinked);
        AddServer(Linked, status: ServerStatus.Offline, linked: true);
        await _db.SaveChangesAsync(Ct);

        var resolution = await ResolveAsync(ImportedRunId);

        Assert.Null(resolution.Server);
        Assert.True(resolution.RunnerTemporarilyUnavailable);
    }

    [Fact]
    public async Task ProjectServerResolution_ExplicitSelectorOnAnUnlinkedServer_IsRefused()
    {
        AddServer(Unlinked);
        AddServer(Linked, linked: true);
        await _db.SaveChangesAsync(Ct);

        var unlinked = await _dispatch.ResolveProjectServerForTargetAsync(
            Stage with { Agent = $"runner-{Unlinked}" }, new Dictionary<string, string>(), OrganizationId, false, ProjectId, Ct);
        var linked = await _dispatch.ResolveProjectServerForTargetAsync(
            Stage with { Agent = $"runner-{Linked}" }, new Dictionary<string, string>(), OrganizationId, false, ProjectId, Ct);

        Assert.Null(unlinked);
        Assert.Equal(Linked, linked?.Id);
    }
}
