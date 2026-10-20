// SPDX-License-Identifier: EUPL-1.2
using System.Security.Claims;
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Components.Users;
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace Aetheus.Back.Tests.Users;

/// <summary>Verifies that deactivating or deleting a pipeline owner returns a conflict unless the
/// request transfers ownership to an active user or explicitly forces the operation.</summary>
public sealed class UserPipelineOwnershipGuardTests
{
    private readonly IUserRepository _repo = Substitute.For<IUserRepository>();
    private readonly IHttpContextAccessor _http = Substitute.For<IHttpContextAccessor>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly IDomainEventDispatcher _events = Substitute.For<IDomainEventDispatcher>();
    private readonly UserService _sut;
    private readonly User _owner = new()
    {
        Id = 1,
        Username = "admin",
        IsActive = true,
        UserRoles = [new UserRole { Role = new Role { Name = "Contributor" } }],
    };

    public UserPipelineOwnershipGuardTests()
    {
        _sut = NewService(_repo, _http, _audit, _events);
        _repo.GetUserDetailAsync(1, Arg.Any<CancellationToken>()).Returns(_owner);
        _repo.FindUserAsync(1, Arg.Any<CancellationToken>()).Returns(_owner);
        _repo.GetOwnedPipelineNamesAsync("admin", Arg.Any<CancellationToken>())
            .Returns(["aetheus-candidate", "aetheus-deploy-prod"]);
        _repo.FindByUsernameAsync("ops", Arg.Any<CancellationToken>())
            .Returns(new User { Id = 2, Username = "ops", IsActive = true });
        _repo.FindByUsernameAsync("gone", Arg.Any<CancellationToken>())
            .Returns(new User { Id = 3, Username = "gone", IsActive = false });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UserService NewService(
        IUserRepository repo, IHttpContextAccessor http, IAuditService audit, IDomainEventDispatcher events) =>
        new(repo, http, audit, Substitute.For<IResourceAuthorizationService>(), new MemoryCache(new MemoryCacheOptions()),
            TimeProvider.System, Substitute.For<IAdminChangeNotifier>(), Substitute.For<IUserChangeNotifier>(), events);

    private static UpdateUserRequest Deactivate(string? transferTo = null, bool force = false) => new()
    {
        Username = "admin",
        IsActive = false,
        Roles = ["Contributor"],
        TransferPipelinesToUsername = transferTo,
        ForceWithOwnedPipelines = force,
    };

    [Fact]
    public async Task Deactivate_OwnerWithoutTargetOrForce_Is409ListingThePipelines_AndChangesNothing()
    {
        var error = await Assert.ThrowsAsync<ConflictException>(() => _sut.UpdateUserAsync(1, Deactivate(), Ct));

        Assert.Contains("owns 2 pipeline(s): aetheus-candidate, aetheus-deploy-prod", error.Message);
        Assert.True(_owner.IsActive);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _events.DidNotReceiveWithAnyArgs().DispatchStrictAsync<UserPipelinesTransferRequestedEvent>(default!, Ct);
    }

    [Fact]
    public async Task Deactivate_WithAnActiveTarget_TransfersFirstThenDeactivates()
    {
        _repo.GetOwnedPipelineNamesAsync("admin", Arg.Any<CancellationToken>())
            .Returns(new[] { "aetheus-candidate", "aetheus-deploy-prod" }, Array.Empty<string>());

        var updated = await _sut.UpdateUserAsync(1, Deactivate(transferTo: "OPS"), Ct);

        Assert.NotNull(updated);
        Assert.False(updated.IsActive);
        await _events.Received(1).DispatchStrictAsync(
            new UserPipelinesTransferRequestedEvent("admin", "ops"), Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deactivate_WithAnInactiveTarget_IsRefused()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateUserAsync(1, Deactivate(transferTo: "gone"), Ct));

        Assert.True(_owner.IsActive);
        await _events.DidNotReceiveWithAnyArgs().DispatchStrictAsync<UserPipelinesTransferRequestedEvent>(default!, Ct);
    }

    [Fact]
    public async Task Deactivate_TransferToTheSameUser_IsRefused()
    {
        _repo.FindByUsernameAsync("admin", Arg.Any<CancellationToken>()).Returns(_owner);

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.UpdateUserAsync(1, Deactivate(transferTo: "admin"), Ct));
        Assert.True(_owner.IsActive);
    }

    [Fact]
    public async Task Deactivate_WhenTheTransferLeftPipelinesBehind_IsRefused_NeverAFakeSuccess()
    {
        // No handler moved anything: the owned list is the same after the dispatch.
        var error = await Assert.ThrowsAsync<ConflictException>(() => _sut.UpdateUserAsync(1, Deactivate(transferTo: "ops"), Ct));

        Assert.Contains("did not complete", error.Message);
        Assert.True(_owner.IsActive);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deactivate_Forced_ProceedsAndAuditsTheOrphanedPipelines()
    {
        var updated = await _sut.UpdateUserAsync(1, Deactivate(force: true), Ct);

        Assert.False(updated!.IsActive);
        await _audit.Received(1).LogAsync("DeactivatedWithOwnedPipelines", "User", 1,
            Arg.Is<string?>(details => details!.Contains("aetheus-candidate")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Deactivate_UserOwningNoPipeline_NeedsNothing()
    {
        _repo.GetOwnedPipelineNamesAsync("admin", Arg.Any<CancellationToken>()).Returns([]);

        var updated = await _sut.UpdateUserAsync(1, Deactivate(), Ct);

        Assert.False(updated!.IsActive);
    }

    [Fact]
    public async Task Delete_OwnerWithoutTargetOrForce_Is409_AndTheUserStays()
    {
        var error = await Assert.ThrowsAsync<ConflictException>(() => _sut.DeleteUserAsync(1, Ct));

        Assert.Contains("aetheus-deploy-prod", error.Message);
        await _repo.DidNotReceive().RemoveUserAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Delete_Forced_RemovesTheUser()
    {
        var deleted = await _sut.DeleteUserAsync(1, Ct, forceWithOwnedPipelines: true);

        Assert.True(deleted);
        await _repo.Received(1).RemoveUserAsync(_owner, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync("DeletedWithOwnedPipelines", "User", 1, Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// End to end over EF InMemory: real repositories, the real Pipelines transfer handler wired to the
    /// strict dispatch, as the administrator of the request. The deactivated owner's pipelines end up
    /// owned by the target, one audit entry each, and only then is the owner deactivated.
    /// </summary>
    [Fact]
    public async Task TransferThenDeactivate_OnRealData_MovesEveryPipelineThenDeactivates()
    {
        await using var db = new AppDbContext(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options,
            new FakeTimeProvider(new DateTimeOffset(2026, 10, 11, 8, 0, 0, TimeSpan.Zero)));
        var contributor = new Role { Id = 1, Name = "Contributor" };
        db.Roles.Add(contributor);
        db.Users.AddRange(
            new User { Id = 1, Username = "admin", PasswordHash = "x", IsActive = true, UserRoles = [new UserRole { UserId = 1, RoleId = 1 }] },
            new User { Id = 2, Username = "root", PasswordHash = "x", IsActive = true });
        db.Pipelines.AddRange(
            new Pipeline { Id = 10, Name = "aetheus-candidate", CreatedByUsername = "admin" },
            new Pipeline { Id = 11, Name = "aetheus-deploy-prod", CreatedByUsername = "admin" });
        await db.SaveChangesAsync(Ct);

        var http = Substitute.For<IHttpContextAccessor>();
        http.HttpContext.Returns(new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.Name, "root"), new Claim(ClaimTypes.Role, "Admin")], "test")),
        });
        var preparations = Substitute.For<IPipelineRunPreparationService>();
        var pipelineAudit = Substitute.For<IAuditService>();
        var ownership = new PipelineOwnershipService(
            new PipelineOwnershipRepository(db), preparations, Substitute.For<IResourceAuthorizationService>(),
            pipelineAudit, NullLogger<PipelineOwnershipService>.Instance);
        var handler = new UserPipelinesTransferHandler(ownership, http);
        var events = Substitute.For<IDomainEventDispatcher>();
        events.DispatchStrictAsync(Arg.Any<UserPipelinesTransferRequestedEvent>(), Arg.Any<CancellationToken>())
            .Returns(call => handler.HandleAsync(call.ArgAt<UserPipelinesTransferRequestedEvent>(0), Ct));
        var service = NewService(new UserRepository(db), http, Substitute.For<IAuditService>(), events);

        var refused = await Assert.ThrowsAsync<ConflictException>(() =>
            service.UpdateUserAsync(1, Deactivate(), Ct));
        Assert.Contains("aetheus-candidate", refused.Message);

        var updated = await service.UpdateUserAsync(1, Deactivate(transferTo: "root"), Ct);

        Assert.False(updated!.IsActive);
        Assert.All(db.Pipelines.AsNoTracking().ToList(), pipeline => Assert.Equal("root", pipeline.CreatedByUsername));
        Assert.False(db.Users.AsNoTracking().Single(user => user.Id == 1).IsActive);
        await pipelineAudit.Received(2).LogAsync(
            PipelineOwnershipService.TransferredAuditAction, "Pipeline", Arg.Any<int?>(),
            Arg.Is<string?>(details => details!.Contains("'admin' -> 'root' by 'root'")), Arg.Any<CancellationToken>());
    }
}
