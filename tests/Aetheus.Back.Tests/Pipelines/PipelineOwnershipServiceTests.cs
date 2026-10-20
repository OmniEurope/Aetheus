// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Ownership tests exercise the repository over EF InMemory with substituted preparation
/// and authorization.</summary>
public sealed class PipelineOwnershipServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly IPipelineRunPreparationService _preparations = Substitute.For<IPipelineRunPreparationService>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly PipelineOwnershipService _sut;

    private static readonly ClaimsPrincipal Admin = Principal("root", "Admin");
    private static readonly ClaimsPrincipal Contributor = Principal("dev", "Contributor");

    public PipelineOwnershipServiceTests()
    {
        _db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero)));
        _sut = new PipelineOwnershipService(
            new PipelineOwnershipRepository(_db), _preparations, _authz, _audit,
            NullLogger<PipelineOwnershipService>.Instance);

        _db.Users.AddRange(
            new User { Id = 1, Username = "admin", PasswordHash = "x", IsActive = false },
            new User { Id = 2, Username = "ops", PasswordHash = "x", IsActive = true },
            new User { Id = 3, Username = "gone", PasswordHash = "x", IsActive = false });
        _db.Pipelines.AddRange(
            new Pipeline { Id = 10, Name = "aetheus-candidate", ProjectId = 5, CreatedByUsername = "admin" },
            new Pipeline { Id = 11, Name = "aetheus-deploy-prod", ProjectId = 5, CreatedByUsername = "admin" },
            new Pipeline { Id = 12, Name = "other-project-ci", ProjectId = 6, CreatedByUsername = "admin" },
            new Pipeline { Id = 13, Name = "ops-nightly", ProjectId = 6, CreatedByUsername = "ops" });
        _db.SaveChanges();

        _preparations.PrepareRunAsync(Arg.Any<int>(), null, null, null, Arg.Any<CancellationToken>())
            .Returns(call => new PipelineRunPreparation
            {
                PipelineId = call.ArgAt<int>(0),
                YamlSnapshot = "name: x",
                TargetServerIds = [42],
            });
        _authz.HasPermissionAsync("ops", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>())
            .Returns(true);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ClaimsPrincipal Principal(string name, string role) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.Name, name), new Claim(ClaimTypes.Role, role)], "test"));

    private string? OwnerOf(int pipelineId) =>
        _db.Pipelines.AsNoTracking().Single(pipeline => pipeline.Id == pipelineId).CreatedByUsername;

    [Fact]
    public async Task Transfer_OnePipeline_ChangesItsOwnerAndAuditsOldNewAndActor()
    {
        var result = await _sut.TransferAsync(Admin, new PipelineOwnerTransferRequest
        {
            PipelineId = 10,
            NewOwnerUsername = "OPS",
        }, Ct);

        Assert.NotNull(result);
        Assert.False(result.Preview);
        Assert.Equal("ops", result.NewOwnerUsername);
        var item = Assert.Single(result.Items);
        Assert.Equal("admin", item.PreviousOwnerUsername);
        Assert.True(item.NewOwnerHoldsServerAdmin);
        Assert.Equal("ops", OwnerOf(10));
        Assert.Equal("admin", OwnerOf(11));
        await _audit.Received(1).LogAsync(
            PipelineOwnershipService.TransferredAuditAction, "Pipeline", 10,
            "aetheus-candidate: owner 'admin' -> 'ops' by 'root'", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Transfer_ToAnInactiveUser_IsRefusedAndChangesNothing()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TransferAsync(Admin, new PipelineOwnerTransferRequest
        {
            PipelineId = 10,
            NewOwnerUsername = "gone",
        }, Ct));

        Assert.Equal("admin", OwnerOf(10));
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, Ct);
    }

    [Fact]
    public async Task Transfer_ToAnUnknownUser_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TransferAsync(Admin, new PipelineOwnerTransferRequest
        {
            PipelineId = 10,
            NewOwnerUsername = "nobody",
        }, Ct));
        Assert.Equal("admin", OwnerOf(10));
    }

    [Fact]
    public async Task Transfer_ByANonAdministrator_IsRefusedAndChangesNothing()
    {
        var result = await _sut.TransferAsync(Contributor, new PipelineOwnerTransferRequest
        {
            PipelineId = 10,
            NewOwnerUsername = "ops",
        }, Ct);

        Assert.Null(result);
        Assert.Equal("admin", OwnerOf(10));
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, Ct);
    }

    [Fact]
    public async Task Transfer_AllPipelinesOfAUser_MovesOnlyThatOwnersPipelines_OneAuditEntryEach()
    {
        var result = await _sut.TransferAsync(Admin, new PipelineOwnerTransferRequest
        {
            CurrentOwnerUsername = "admin",
            NewOwnerUsername = "ops",
        }, Ct);

        Assert.NotNull(result);
        Assert.Equal([10, 11, 12], result.Items.Select(item => item.PipelineId).Order());
        Assert.All(new[] { 10, 11, 12, 13 }, id => Assert.Equal("ops", OwnerOf(id)));
        await _audit.Received(3).LogAsync(
            PipelineOwnershipService.TransferredAuditAction, "Pipeline", Arg.Any<int?>(),
            Arg.Is<string?>(details => details!.Contains("'admin' -> 'ops' by 'root'")), Arg.Any<CancellationToken>());
    }

    // Recette R1-51: the deactivation path changes and audits exactly like Transfer, without preparing
    // any pipeline again for a report nobody reads.
    [Fact]
    public async Task R1_51_TransferWithoutReport_MovesAndAuditsLikeTransfer_WithoutPreparingAnyPipeline()
    {
        var transferred = await _sut.TransferWithoutReportAsync(Admin, new PipelineOwnerTransferRequest
        {
            CurrentOwnerUsername = "admin",
            NewOwnerUsername = "OPS",
        }, Ct);

        Assert.True(transferred);
        Assert.All(new[] { 10, 11, 12, 13 }, id => Assert.Equal("ops", OwnerOf(id)));
        await _audit.Received(3).LogAsync(
            PipelineOwnershipService.TransferredAuditAction, "Pipeline", Arg.Any<int?>(),
            Arg.Is<string?>(details => details!.Contains("'admin' -> 'ops' by 'root'")), Arg.Any<CancellationToken>());
        await _preparations.DidNotReceiveWithAnyArgs().PrepareRunAsync(default, default, default, default, Ct);
        await _authz.DidNotReceiveWithAnyArgs().HasPermissionAsync(default(string)!, default, default, default, Ct);
    }

    [Fact]
    public async Task R1_51_TransferWithoutReport_KeepsTheChecks()
    {
        Assert.False(await _sut.TransferWithoutReportAsync(Contributor,
            new PipelineOwnerTransferRequest { CurrentOwnerUsername = "admin", NewOwnerUsername = "ops" }, Ct));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TransferWithoutReportAsync(Admin,
            new PipelineOwnerTransferRequest { CurrentOwnerUsername = "admin", NewOwnerUsername = "gone" }, Ct));
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.TransferWithoutReportAsync(Admin,
            new PipelineOwnerTransferRequest { PipelineId = 99, NewOwnerUsername = "ops" }, Ct));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TransferWithoutReportAsync(Admin,
            new PipelineOwnerTransferRequest { PipelineId = 10, ProjectId = 5, NewOwnerUsername = "ops" }, Ct));

        Assert.Equal("admin", OwnerOf(10));
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, Ct);
    }

    [Fact]
    public async Task Transfer_AllPipelinesOfAProject_LeavesOtherProjectsAlone()
    {
        await _sut.TransferAsync(Admin, new PipelineOwnerTransferRequest { ProjectId = 5, NewOwnerUsername = "ops" }, Ct);

        Assert.Equal("ops", OwnerOf(10));
        Assert.Equal("ops", OwnerOf(11));
        Assert.Equal("admin", OwnerOf(12));
    }

    [Fact]
    public async Task Transfer_AlreadyOwnedPipeline_WritesNoAuditEntry()
    {
        await _sut.TransferAsync(Admin, new PipelineOwnerTransferRequest { PipelineId = 13, NewOwnerUsername = "ops" }, Ct);

        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, Ct);
    }

    [Fact]
    public async Task Preview_WritesNothing_AndWarnsWhenTheNewOwnerLacksServerAdmin()
    {
        _db.Users.Add(new User { Id = 4, Username = "reader", PasswordHash = "x", IsActive = true });
        _db.Servers.Add(new Server { Id = 42, Name = "vps-prod", Hostname = "vps-prod.example", OrganizationId = 1 });
        await _db.SaveChangesAsync(Ct);

        var result = await _sut.PreviewAsync(Admin, new PipelineOwnerTransferRequest
        {
            PipelineId = 10,
            NewOwnerUsername = "reader",
        }, Ct);

        Assert.NotNull(result);
        Assert.True(result.Preview);
        var item = Assert.Single(result.Items);
        Assert.False(item.NewOwnerHoldsServerAdmin);
        Assert.Equal(["vps-prod"], item.ServersMissingAdmin);
        Assert.Equal("admin", OwnerOf(10));
        // The same authorization call as the F-EXEC-1b gate: owner name, Server, target id, Admin.
        await _authz.Received().HasPermissionAsync("reader", ResourceType.Server, 42, Permission.Admin, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Preview_UnreadableDefinition_ReportsWhyTheTargetsAreUnknown()
    {
        _preparations.PrepareRunAsync(10, null, null, null, Arg.Any<CancellationToken>())
            .ThrowsAsync(new BadRequestException("definition refused"));

        var result = await _sut.PreviewAsync(Admin, new PipelineOwnerTransferRequest { PipelineId = 10, NewOwnerUsername = "ops" }, Ct);

        var item = Assert.Single(result!.Items);
        Assert.Null(item.NewOwnerHoldsServerAdmin);
        Assert.Equal("definition refused", item.TargetsUnresolvedReason);
    }

    [Fact]
    public async Task Transfer_WithoutExactlyOneScope_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TransferAsync(Admin,
            new PipelineOwnerTransferRequest { PipelineId = 10, ProjectId = 5, NewOwnerUsername = "ops" }, Ct));
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.TransferAsync(Admin,
            new PipelineOwnerTransferRequest { NewOwnerUsername = "ops" }, Ct));
    }

    [Fact]
    public async Task Transfer_UnknownPipeline_IsNotFound()
    {
        await Assert.ThrowsAsync<NotFoundException>(() => _sut.TransferAsync(Admin,
            new PipelineOwnerTransferRequest { PipelineId = 999, NewOwnerUsername = "ops" }, Ct));
    }

    [Fact]
    public async Task OwnedPipelines_FlagAnInactiveOwner()
    {
        var owned = await _sut.GetOwnedPipelinesAsync("admin", Ct);

        Assert.Equal(["aetheus-candidate", "aetheus-deploy-prod", "other-project-ci"], owned.Select(p => p.PipelineName));
        Assert.All(owned, pipeline => Assert.True(pipeline.OwnerInactive));
        Assert.All(await _sut.GetOwnedPipelinesAsync("ops", Ct), pipeline => Assert.False(pipeline.OwnerInactive));
    }

    [Fact]
    public async Task Candidates_AreTheActiveUsersOnly()
    {
        var candidates = await _sut.GetCandidatesAsync(Ct);

        Assert.Equal(["ops"], candidates.Select(candidate => candidate.Username));
    }

    [Fact]
    public async Task UserTransferHandler_MovesTheUsersPipelinesAsTheRequestAdministrator()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = Admin });
        var handler = new UserPipelinesTransferHandler(_sut, accessor);

        await handler.HandleAsync(new UserPipelinesTransferRequestedEvent("admin", "ops"), Ct);

        Assert.Equal("ops", OwnerOf(10));
        Assert.Equal("ops", OwnerOf(12));
    }

    [Fact]
    public async Task UserTransferHandler_RefusesANonAdministratorRequest()
    {
        var accessor = Substitute.For<IHttpContextAccessor>();
        accessor.HttpContext.Returns(new DefaultHttpContext { User = Contributor });
        var handler = new UserPipelinesTransferHandler(_sut, accessor);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            handler.HandleAsync(new UserPipelinesTransferRequestedEvent("admin", "ops"), Ct));
        Assert.Equal("admin", OwnerOf(10));
    }
}
