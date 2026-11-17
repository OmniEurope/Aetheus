// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Analysis;
using Aetheus.Back.Components.Analysis.DependencyUpdates;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Analysis;

/// <summary>
/// R-6 (decision of 2026-11-09, option A): the lock refresh of an automatic update runs under the identity of the cve-lock-refresh
/// pipeline's OWNER and nobody else. The accounts and pipelines are real EF rows (in memory); the rights and the run service are
/// doubles, so nothing here proves a real run on a runner: it proves who is resolved, every refusal, and what identity the launch
/// carries to the run service.
/// </summary>
public sealed class DependencyUpdateLockRefreshTests : IDisposable
{
    private const int PipelineId = 40;
    private const string Owner = "owner";
    private const string Branch = "aetheus/deps/lodash-4.17.21";
    private const string Command = "( npm install --package-lock-only --ignore-scripts )";

    private readonly AppDbContext _db;
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IPipelineRunService _runs = Substitute.For<IPipelineRunService>();
    private readonly ServiceProvider _provider;
    private readonly DependencyUpdateLockRefresh _refresh;
    private IAuditActor? _actorOfTheLaunchScope;

    public DependencyUpdateLockRefreshTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(DateTimeOffset.UnixEpoch));
        _db.Pipelines.Add(new Pipeline { Id = PipelineId, ProjectId = 3, Name = CveRemediationLockRun.PipelineName, CreatedByUsername = Owner });
        _db.Users.Add(new User { Id = 1, Username = Owner, IsActive = true });
        _db.SaveChanges();

        _authz.HasPermissionAsync(Owner, PermissionKeys.PipelineRun, PipelineId, Arg.Any<CancellationToken>()).Returns(true);
        _authz.HasPermissionAsync(Owner, ResourceType.Server, Arg.Any<int?>(), Permission.Admin, Arg.Any<CancellationToken>()).Returns(true);
        _runs.PrepareRunAsync(PipelineId, "main", Arg.Any<CancellationToken>()).Returns(Preparation(Owner, [5]));

        var services = new ServiceCollection();
        services.AddScoped<IAuditActor, AuditActor>();
        services.AddSingleton(_authz);
        services.AddScoped(provider =>
        {
            _actorOfTheLaunchScope = provider.GetRequiredService<IAuditActor>();
            return _runs;
        });
        _provider = services.BuildServiceProvider();
        // The artifact reading of the remediation is not exercised here (CveRemediationLockRun is covered on its own).
        _refresh = new DependencyUpdateLockRefresh(new DependencyUpdateRepository(_db), _authz, _runs, null!,
            _provider.GetRequiredService<IServiceScopeFactory>());
    }

    public void Dispose()
    {
        _provider.Dispose();
        _db.Dispose();
    }

    private static PipelineRunPreparation Preparation(string? owner, IReadOnlyCollection<int> servers) => new()
    {
        PipelineId = PipelineId,
        YamlSnapshot = "stages: []",
        TargetServerIds = servers,
        Pipeline = new Pipeline { Id = PipelineId, Name = CveRemediationLockRun.PipelineName, CreatedByUsername = owner }
    };

    private static DependencyUpdateLockLaunch Launch() => new(PipelineId, Owner, Branch, "abc123", Command, 9);

    private Task<DependencyUpdateLockReadiness> CheckAsync() => _refresh.CheckAsync(3, "main", TestContext.Current.CancellationToken);

    private async Task SetOwnerAsync(string? owner)
    {
        var pipeline = await _db.Pipelines.SingleAsync(TestContext.Current.CancellationToken);
        pipeline.CreatedByUsername = owner;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private void AuthorizedLaunch(int runId = 77)
    {
        _runs.PrepareAutomatedRunAsync(PipelineId, DependencyUpdateLockRefresh.TriggerSource, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Preparation(Owner, [5]));
        _runs.ResolveRunParametersAsync(Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<PipelineRunPreparation>());
        _runs.TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(new PipelineRunDto { Id = runId });
    }

    [Fact]
    public async Task Check_OwnerActiveAndAllowed_ResolvesThePipelineAndItsOwner()
    {
        var readiness = await CheckAsync();

        Assert.Equal((PipelineId, Owner, (string?)null), (readiness.PipelineId, readiness.Owner, readiness.Refusal));
    }

    [Fact]
    public async Task Check_NoRefreshPipeline_IsRefused()
    {
        _db.Pipelines.RemoveRange(_db.Pipelines);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var readiness = await CheckAsync();

        Assert.Null(readiness.PipelineId);
        Assert.Contains("no pipeline named 'cve-lock-refresh'", readiness.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_PipelineWithoutOwner_IsRefusedWithNoOtherIdentityTried()
    {
        await SetOwnerAsync(null);

        var readiness = await CheckAsync();

        Assert.Null(readiness.Owner);
        Assert.Contains("has no owner", readiness.Refusal, StringComparison.Ordinal);
        await _authz.DidNotReceive().HasPermissionAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Check_OwnerDeleted_IsRefused()
    {
        _db.Users.RemoveRange(_db.Users);
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var readiness = await CheckAsync();

        Assert.Contains("'owner' of the pipeline 'cve-lock-refresh' no longer exists", readiness.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_OwnerDisabled_IsRefused()
    {
        (await _db.Users.SingleAsync(TestContext.Current.CancellationToken)).IsActive = false;
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var readiness = await CheckAsync();

        Assert.Contains("is disabled", readiness.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_OwnerWithoutTheRunPermission_IsRefused()
    {
        _authz.HasPermissionAsync(Owner, PermissionKeys.PipelineRun, PipelineId, Arg.Any<CancellationToken>()).Returns(false);

        var readiness = await CheckAsync();

        Assert.Null(readiness.PipelineId);
        Assert.Contains("may not run it (permission pipeline.run)", readiness.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_OwnerNotAdminOfATargetServer_IsRefused()
    {
        _authz.HasPermissionAsync(Owner, ResourceType.Server, 5, Permission.Admin, Arg.Any<CancellationToken>()).Returns(false);

        var readiness = await CheckAsync();

        Assert.Contains("is not Admin of every server it targets", readiness.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Check_DefinitionThatCannotBePrepared_IsRefused()
    {
        _runs.PrepareRunAsync(PipelineId, "main", Arg.Any<CancellationToken>()).ThrowsAsync(new BadRequestException("invalid YAML"));

        var readiness = await CheckAsync();

        Assert.Contains("cannot be prepared: invalid YAML", readiness.Refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recheck_OwnerChangedSinceTheLaunch_IsRefused()
    {
        await SetOwnerAsync("someone-else");
        _db.Users.Add(new User { Id = 2, Username = "someone-else", IsActive = true });
        await _db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var refusal = await _refresh.RecheckAsync(PipelineId, Owner, TestContext.Current.CancellationToken);

        Assert.Contains("changed since the run was launched as 'owner'", refusal, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Recheck_OwnerStillAllowed_Passes() =>
        Assert.Null(await _refresh.RecheckAsync(PipelineId, Owner, TestContext.Current.CancellationToken));

    [Fact]
    public async Task Launch_CarriesTheOwnerIdentityToTheRunAndItsAudit()
    {
        AuthorizedLaunch();
        string? actorAtTrigger = null;
        PipelineRunPreparation? launched = null;
        Dictionary<string, string>? parameters = null;
        Dictionary<string, string>? variables = null;
        _runs.TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(), Arg.Any<string>())
            .Returns(call =>
            {
                actorAtTrigger = _actorOfTheLaunchScope?.Username;
                launched = call.ArgAt<PipelineRunPreparation>(0);
                variables = call.ArgAt<Dictionary<string, string>>(1);
                parameters = call.ArgAt<Dictionary<string, string>>(2);
                return new PipelineRunDto { Id = 77 };
            });

        var launch = await _refresh.LaunchAsync(Launch(), TestContext.Current.CancellationToken);

        Assert.Equal((77, (string?)null), (launch.RunId, launch.Refusal));
        // The launch's audit ("Triggered", the run's lineage) names the owner, and the preparation carries the owner as the initiator.
        Assert.Equal(Owner, actorAtTrigger);
        Assert.Equal(Owner, launched!.InitiatedBy);
        Assert.Equal(Command, parameters![CveRemediationLockRun.CommandParameter]);
        Assert.Equal((Branch, "abc123", "9"), (variables![PipelineRunService.SourceBranchVariable], variables[PipelineRunService.SourceCommitVariable],
            variables[DependencyUpdatePipelines.UpdateVariable]));
        // Authorized through the automated path, which authorizes the pipeline's owner (F-EXEC-1b), never a caller or a system identity.
        await _runs.Received(1).PrepareAutomatedRunAsync(PipelineId, DependencyUpdateLockRefresh.TriggerSource,
            Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Launch_AutomatedAuthorizationRefused_ReturnsTheRefusalAndLaunchesNothing()
    {
        _runs.PrepareAutomatedRunAsync(PipelineId, DependencyUpdateLockRefresh.TriggerSource, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns((PipelineRunPreparation?)null);

        var launch = await _refresh.LaunchAsync(Launch(), TestContext.Current.CancellationToken);

        Assert.Null(launch.RunId);
        Assert.Contains("not authorized for the pipeline's owner", launch.Refusal, StringComparison.Ordinal);
        await _runs.DidNotReceiveWithAnyArgs().TriggerPreparedRunAsync(default!, default, default, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task Launch_OwnerChangedBeforeTheLaunch_IsRefusedAndRecordedAsARefusedRun()
    {
        AuthorizedLaunch();
        _runs.PrepareAutomatedRunAsync(PipelineId, DependencyUpdateLockRefresh.TriggerSource, Arg.Any<Dictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Preparation("admin", [5]));

        var launch = await _refresh.LaunchAsync(Launch(), TestContext.Current.CancellationToken);

        Assert.Null(launch.RunId);
        Assert.Contains("owner of the pipeline 'cve-lock-refresh' changed", launch.Refusal, StringComparison.Ordinal);
        await _runs.Received(1).RecordRefusedAutomatedLaunchAsync(PipelineId, DependencyUpdateLockRefresh.TriggerSource, launch.Refusal!,
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>());
        await _runs.DidNotReceiveWithAnyArgs().TriggerPreparedRunAsync(default!, default, default, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task Launch_CommandMovesTheRunToAServerTheOwnerDoesNotAdminister_IsRefused()
    {
        AuthorizedLaunch();
        _runs.ResolveRunParametersAsync(Arg.Any<PipelineRunPreparation>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(Preparation(Owner, [5, 6]));
        _authz.HasPermissionAsync(Owner, ResourceType.Server, 6, Permission.Admin, Arg.Any<CancellationToken>()).Returns(false);

        var launch = await _refresh.LaunchAsync(Launch(), TestContext.Current.CancellationToken);

        Assert.Null(launch.RunId);
        Assert.Contains("not Admin of every server the run targets", launch.Refusal, StringComparison.Ordinal);
        await _runs.DidNotReceiveWithAnyArgs().TriggerPreparedRunAsync(default!, default, default, TestContext.Current.CancellationToken, default);
    }

    [Fact]
    public async Task Launch_RefusedByTheLauncher_ReturnsItsReason()
    {
        AuthorizedLaunch();
        _runs.TriggerPreparedRunAsync(Arg.Any<PipelineRunPreparation>(), Arg.Any<Dictionary<string, string>>(), Arg.Any<Dictionary<string, string>>(),
                Arg.Any<CancellationToken>(), Arg.Any<string>())
            .ThrowsAsync(new BadRequestException("the pipeline is disabled"));

        var launch = await _refresh.LaunchAsync(Launch(), TestContext.Current.CancellationToken);

        Assert.Equal((null, "the pipeline is disabled"), (launch.RunId, launch.Refusal));
    }
}
