// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Releases;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.Releases;

/// <summary>
/// Recette R-13: the release production still serves, left Failed by an older rollback rule
/// (<c>c-424f31</c>), is put back to Deployed by hand; the project keeps a single deployed release, the
/// change is audited with its reason and broadcast. Real repository on the EF in-memory provider.
/// </summary>
public sealed class ReleaseStatusRestoreServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IReleaseService _releases = Substitute.For<IReleaseService>();
    private readonly IHubContext<ReleaseHub> _hub = Substitute.For<IHubContext<ReleaseHub>>();
    private readonly IClientProxy _clients = Substitute.For<IClientProxy>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly ReleaseStatusRestoreService _service;
    private readonly int _projectId;
    private readonly int _otherProjectId;

    public ReleaseStatusRestoreServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var project = new Project { Name = "aetheus" };
        var other = new Project { Name = "other" };
        _db.Projects.AddRange(project, other);
        _db.SaveChanges();
        _projectId = project.Id;
        _otherProjectId = other.Id;
        _hub.Clients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(_clients);
        _releases.GetReleaseAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(call => _db.Releases.AsNoTracking().Where(r => r.Id == call.Arg<int>())
                .Select(r => new ReleaseDto { Id = r.Id, ProjectId = r.ProjectId, Version = r.Version, Status = r.Status })
                .FirstOrDefault());
        _service = new ReleaseStatusRestoreService(new ReleaseRepository(_db), _releases, _hub, _audit);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private int AddRelease(string version, ReleaseStatus status, int? projectId = null)
    {
        var release = new Release { ProjectId = projectId ?? _projectId, Version = version, BranchName = "main", Status = status };
        _db.Releases.Add(release);
        _db.SaveChanges();
        _db.ChangeTracker.Clear();
        return release.Id;
    }

    private ReleaseStatus StatusOf(int id) => _db.Releases.AsNoTracking().Single(r => r.Id == id).Status;

    private static RestoreReleaseStatusRequest To(ReleaseStatus status, string reason = "production still serves it") =>
        new() { Status = status, Reason = reason };

    [Fact]
    public async Task R_13_AFailedReleaseProductionServes_IsDeployedAgain_AndTheOtherDeployedReleaseOfItsProjectIsSuperseded()
    {
        var stuck = AddRelease("c-424f31", ReleaseStatus.Failed);
        var wronglyDeployed = AddRelease("c-500000", ReleaseStatus.Deployed);
        var otherProject = AddRelease("c-999999", ReleaseStatus.Deployed, _otherProjectId);

        var dto = await _service.RestoreAsync(stuck, To(ReleaseStatus.Deployed), Ct);

        Assert.Equal(ReleaseStatus.Deployed, dto.Status);
        Assert.Equal(ReleaseStatus.Deployed, StatusOf(stuck));
        Assert.Equal(ReleaseStatus.Superseded, StatusOf(wronglyDeployed));
        Assert.Equal(ReleaseStatus.Deployed, StatusOf(otherProject));
    }

    [Fact]
    public async Task R_13_ARestore_IsAuditedWithItsReason_AndBroadcastForEveryChangedRelease()
    {
        var stuck = AddRelease("c-424f31", ReleaseStatus.Failed);
        var wronglyDeployed = AddRelease("c-500000", ReleaseStatus.Deployed);

        await _service.RestoreAsync(stuck, To(ReleaseStatus.Deployed, "  production still serves it  "), Ct);

        await _audit.Received(1).LogAsync("ReleaseStatusRestored", "Release", stuck,
            $"version=c-424f31; from=Failed; to=Deployed; superseded={wronglyDeployed}; reason=production still serves it",
            Arg.Any<CancellationToken>());
        await _clients.Received(2).SendCoreAsync("ReleaseStatusChanged", Arg.Any<object?[]>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task R_13_RestoringAnotherStatus_LeavesTheDeployedReleaseAlone()
    {
        var failed = AddRelease("c-111111", ReleaseStatus.Failed);
        var deployed = AddRelease("c-222222", ReleaseStatus.Deployed);

        await _service.RestoreAsync(failed, To(ReleaseStatus.Superseded), Ct);

        Assert.Equal(ReleaseStatus.Superseded, StatusOf(failed));
        Assert.Equal(ReleaseStatus.Deployed, StatusOf(deployed));
    }

    [Theory]
    [InlineData(ReleaseStatus.Detected)]
    [InlineData(ReleaseStatus.Building)]
    [InlineData(ReleaseStatus.RolledBack)]
    [InlineData(ReleaseStatus.Promoted)]
    [InlineData(ReleaseStatus.Archived)]
    public async Task R_13_AStatusNoDeploymentHistoryEndsIn_IsRefused(ReleaseStatus target)
    {
        var failed = AddRelease("c-424f31", ReleaseStatus.Failed);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.RestoreAsync(failed, To(target), Ct));

        Assert.Equal(ReleaseStatus.Failed, StatusOf(failed));
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, Ct);
    }

    [Fact]
    public async Task R_13_AnArchivedRelease_IsRefused()
    {
        var archived = AddRelease("c-424f31", ReleaseStatus.Archived);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.RestoreAsync(archived, To(ReleaseStatus.Deployed), Ct));

        Assert.Equal(ReleaseStatus.Archived, StatusOf(archived));
    }

    [Fact]
    public async Task R_13_TheSameStatus_IsRefused()
    {
        var deployed = AddRelease("c-424f31", ReleaseStatus.Deployed);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.RestoreAsync(deployed, To(ReleaseStatus.Deployed), Ct));
    }

    [Fact]
    public async Task R_13_ABlankReason_IsRefused()
    {
        var failed = AddRelease("c-424f31", ReleaseStatus.Failed);

        await Assert.ThrowsAsync<BadRequestException>(() => _service.RestoreAsync(failed, To(ReleaseStatus.Deployed, "   "), Ct));

        Assert.Equal(ReleaseStatus.Failed, StatusOf(failed));
    }

    [Fact]
    public async Task R_13_AnUnknownRelease_IsNotFound() =>
        await Assert.ThrowsAsync<NotFoundException>(() => _service.RestoreAsync(4242, To(ReleaseStatus.Deployed), Ct));
}
