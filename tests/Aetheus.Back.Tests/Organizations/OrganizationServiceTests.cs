// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Aetheus.Shared.Components.Organizations;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class OrganizationServiceTests
{
    private readonly IOrganizationRepository _repo = Substitute.For<IOrganizationRepository>();
    private readonly IResourceAuthorizationService _authz = Substitute.For<IResourceAuthorizationService>();
    private readonly IUserChangeNotifier _userNotifier = Substitute.For<IUserChangeNotifier>();
    private readonly OrganizationService _sut;

    public OrganizationServiceTests()
    {
        _repo.GetMembersForNotificationAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _repo.GetProjectOrganizationIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _sut = new OrganizationService(_repo, _authz, TimeProvider.System, Substitute.For<IAdminChangeNotifier>(), _userNotifier, Substitute.For<IAuditService>());
    }

    [Fact]
    public async Task GetOrganizationsAsync_ReturnsPaginatedResult()
    {
        var org = new Organization { Id = 1, Name = "Acme", Slug = "acme", Members = [], Projects = [] };
        _repo.GetPagedAsync(null, 1, 10, null, false, Arg.Any<CancellationToken>())
            .Returns(([org], 1));

        var result = await _sut.GetOrganizationsAsync(null, new PaginationRequest { Page = 1, PageSize = 10 }, TestContext.Current.CancellationToken);

        Assert.Single(result.Items);
        Assert.Equal("Acme", result.Items[0].Name);
        Assert.Equal(1, result.TotalCount);
    }

    [Fact]
    public async Task GetOrganizationsAsync_ClampsPageSize()
    {
        _repo.GetPagedAsync(null, 1, 200, null, false, Arg.Any<CancellationToken>())
            .Returns((new List<Organization>(), 0));

        await _sut.GetOrganizationsAsync(null, new PaginationRequest { Page = 1, PageSize = 999 }, TestContext.Current.CancellationToken);

        await _repo.Received(1).GetPagedAsync(null, 1, 200, null, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrganizationsAsync_ClampsPageSizeMin()
    {
        _repo.GetPagedAsync(null, 1, 1, null, false, Arg.Any<CancellationToken>())
            .Returns((new List<Organization>(), 0));

        await _sut.GetOrganizationsAsync(null, new PaginationRequest { Page = 1, PageSize = -5 }, TestContext.Current.CancellationToken);

        await _repo.Received(1).GetPagedAsync(null, 1, 1, null, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrganizationsAsync_NegativePage_UsesOne()
    {
        _repo.GetPagedAsync("x", 1, 10, null, false, Arg.Any<CancellationToken>())
            .Returns((new List<Organization>(), 0));

        await _sut.GetOrganizationsAsync("x", new PaginationRequest { Page = -1, PageSize = 10 }, TestContext.Current.CancellationToken);

        await _repo.Received(1).GetPagedAsync("x", 1, 10, null, false, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetOrganizationAsync_Found_ReturnsDetail()
    {
        var org = new Organization
        {
            Id = 1,
            Name = "Acme",
            Slug = "acme",
            Description = "Desc",
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            Members =
            [
                new OrganizationMember
                {
                    Id = 10, UserId = 5, Role = OrganizationRole.Owner,
                    User = new User { Id = 5, Username = "alice", Email = "a@b.c" }
                }
            ],
            Projects = [new Project { Id = 20, Name = "P1", Status = ProjectStatus.Active }]
        };
        _repo.GetWithMembersAndProjectsAsync(1, Arg.Any<CancellationToken>())
            .Returns(org);

        var result = await _sut.GetOrganizationAsync(1, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Acme", result.Name);
        Assert.Single(result.Members);
        Assert.Equal("alice", result.Members[0].Username);
        Assert.Single(result.Projects);
        Assert.Equal("P1", result.Projects[0].Name);
    }

    [Fact]
    public async Task GetOrganizationAsync_NotFound_ReturnsNull()
    {
        _repo.GetWithMembersAndProjectsAsync(99, Arg.Any<CancellationToken>())
            .Returns((Organization?)null);

        var result = await _sut.GetOrganizationAsync(99, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateOrganizationAsync_Success_ReturnsDto()
    {
        _repo.NameExistsAsync("New Org", null, Arg.Any<CancellationToken>()).Returns(false);
        _repo.SlugExistsAsync("new-org", null, Arg.Any<CancellationToken>()).Returns(false);
        _repo.AddAsync(Arg.Any<Organization>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.CreateOrganizationAsync(
            new CreateOrganizationRequest { Name = "New Org", Slug = "new-org", Description = "d" }, TestContext.Current.CancellationToken);

        Assert.Equal("New Org", result.Name);
        Assert.Equal("new-org", result.Slug);
        await _repo.Received(1).AddAsync(Arg.Any<Organization>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CreateOrganizationAsync_DuplicateName_ThrowsConflict()
    {
        _repo.NameExistsAsync("Dup", null, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.CreateOrganizationAsync(new CreateOrganizationRequest { Name = "Dup", Slug = "dup" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateOrganizationAsync_DuplicateSlug_ThrowsConflict()
    {
        _repo.NameExistsAsync("X", null, Arg.Any<CancellationToken>()).Returns(false);
        _repo.SlugExistsAsync("dup", null, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.CreateOrganizationAsync(new CreateOrganizationRequest { Name = "X", Slug = "dup" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CreateOrganizationAsync_TrimsInput()
    {
        _repo.NameExistsAsync("Trimmed", null, Arg.Any<CancellationToken>()).Returns(false);
        _repo.SlugExistsAsync("trimmed", null, Arg.Any<CancellationToken>()).Returns(false);
        _repo.AddAsync(Arg.Any<Organization>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        Organization? captured = null;
        _repo.AddAsync(Arg.Any<Organization>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask)
            .AndDoes(ci => { captured = ci.Arg<Organization>(); });

        await _sut.CreateOrganizationAsync(
            new CreateOrganizationRequest { Name = "  Trimmed  ", Slug = "  TRIMMED  ", Description = "  desc  " }, TestContext.Current.CancellationToken);

        Assert.NotNull(captured);
        Assert.Equal("Trimmed", captured.Name);
        Assert.Equal("trimmed", captured.Slug);
        Assert.Equal("desc", captured.Description);
    }

    [Fact]
    public async Task UpdateOrganizationAsync_Found_ReturnsUpdated()
    {
        var org = new Organization { Id = 1, Name = "Old", Slug = "old", Members = [], Projects = [] };
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(org);
        _repo.NameExistsAsync("New", 1, Arg.Any<CancellationToken>()).Returns(false);
        _repo.SlugExistsAsync("new", 1, Arg.Any<CancellationToken>()).Returns(false);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.UpdateOrganizationAsync(1,
            new UpdateOrganizationRequest { Name = "New", Slug = "new" }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("New", result.Name);
    }

    [Fact]
    public async Task UpdateOrganizationAsync_NotFound_ReturnsNull()
    {
        _repo.GetByIdAsync(99, Arg.Any<CancellationToken>()).Returns((Organization?)null);

        var result = await _sut.UpdateOrganizationAsync(99,
            new UpdateOrganizationRequest { Name = "X", Slug = "x" }, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateOrganizationAsync_DuplicateName_ThrowsConflict()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1, Members = [], Projects = [] });
        _repo.NameExistsAsync("Dup", 1, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateOrganizationAsync(1, new UpdateOrganizationRequest { Name = "Dup", Slug = "x" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateOrganizationAsync_DuplicateSlug_ThrowsConflict()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1, Members = [], Projects = [] });
        _repo.NameExistsAsync("X", 1, Arg.Any<CancellationToken>()).Returns(false);
        _repo.SlugExistsAsync("dup", 1, Arg.Any<CancellationToken>()).Returns(true);

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.UpdateOrganizationAsync(1, new UpdateOrganizationRequest { Name = "X", Slug = "dup" }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DeleteOrganizationAsync_DelegatesToRepo()
    {
        _repo.DeleteAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteOrganizationAsync(1, TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repo.Received(1).DeleteAsync(1, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_Success_ReturnsMemberDto()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1, Name = "Acme" });
        _repo.GetUserByIdAsync(5, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 5, Username = "bob", Email = "b@c.d" });
        _repo.GetMemberAsync(1, 5, Arg.Any<CancellationToken>())
            .Returns((OrganizationMember?)null);
        _repo.AddMemberAsync(Arg.Any<OrganizationMember>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.AddMemberAsync(1,
            new AddOrganizationMemberRequest { UserId = 5, Role = OrganizationRole.Member }, TestContext.Current.CancellationToken);

        Assert.Equal("bob", result.Username);
        Assert.Equal(OrganizationRole.Member, result.Role);
        _authz.Received(1).InvalidateRoleCache("bob");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(5, PermissionChangeReasons.OrganizationMembership, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AddMemberAsync_OrgNotFound_ThrowsNotFound()
    {
        _repo.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((Organization?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.AddMemberAsync(99, new AddOrganizationMemberRequest { UserId = 1 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddMemberAsync_UserNotFound_ThrowsNotFound()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1, Name = "Acme" });
        _repo.GetUserByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((User?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.AddMemberAsync(1, new AddOrganizationMemberRequest { UserId = 99 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AddMemberAsync_AlreadyMember_ThrowsConflict()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1, Name = "Acme" });
        _repo.GetUserByIdAsync(5, Arg.Any<CancellationToken>())
            .Returns(new User { Id = 5, Username = "bob" });
        _repo.GetMemberAsync(1, 5, Arg.Any<CancellationToken>())
            .Returns(new OrganizationMember { Id = 10 });

        await Assert.ThrowsAsync<ConflictException>(() =>
            _sut.AddMemberAsync(1, new AddOrganizationMemberRequest { UserId = 5 }, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task UpdateMemberAsync_Found_ReturnsUpdated()
    {
        var member = new OrganizationMember
        {
            Id = 10,
            UserId = 5,
            Role = OrganizationRole.Member,
            User = new User { Id = 5, Username = "alice", Email = "a@b.c" }
        };
        _repo.GetMemberWithUserByIdAsync(10, 1, Arg.Any<CancellationToken>())
            .Returns(member);
        _repo.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);

        var result = await _sut.UpdateMemberAsync(1, 10,
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(OrganizationRole.Owner, result.Role);
        _authz.Received(1).InvalidateRoleCache("alice");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(5, PermissionChangeReasons.OrganizationMembership, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateMemberAsync_NotFound_ReturnsNull()
    {
        _repo.GetMemberWithUserByIdAsync(99, 1, Arg.Any<CancellationToken>())
            .Returns((OrganizationMember?)null);

        var result = await _sut.UpdateMemberAsync(1, 99,
            new UpdateOrganizationMemberRequest { Role = OrganizationRole.Owner }, TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveMemberAsync_Found_ReturnsTrueAndInvalidatesCache()
    {
        var member = new OrganizationMember
        {
            Id = 10,
            UserId = 5,
            User = new User { Id = 5, Username = "bob" }
        };
        _repo.GetMemberWithUserByIdAsync(10, 1, Arg.Any<CancellationToken>())
            .Returns(member);
        _repo.RemoveMemberAsync(1, 10, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.RemoveMemberAsync(1, 10, TestContext.Current.CancellationToken);

        Assert.True(result);
        _authz.Received(1).InvalidateRoleCache("bob");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(5, PermissionChangeReasons.OrganizationMembership, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RemoveMemberAsync_NotFound_ReturnsFalse()
    {
        _repo.GetMemberWithUserByIdAsync(99, 1, Arg.Any<CancellationToken>())
            .Returns((OrganizationMember?)null);
        _repo.RemoveMemberAsync(1, 99, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _sut.RemoveMemberAsync(1, 99, TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task AssignProjectsAsync_Success_CallsRepo()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1 });
        _repo.CountExistingProjectsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(2);
        _repo.AssignProjectsAsync(1, Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AssignProjectsAsync(1, [10, 20], TestContext.Current.CancellationToken);

        await _repo.Received(1).AssignProjectsAsync(1, Arg.Any<List<int>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignProjectsAsync_EvictsTheSourceOrganizationMembersAsWellAsTheTarget()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Organization { Id = 1 });
        _repo.CountExistingProjectsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>()).Returns(1);
        // The source is read before the move: afterwards every project already belongs to org 1.
        _repo.GetProjectOrganizationIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>()).Returns([2]);
        _repo.GetMembersForNotificationAsync(1, Arg.Any<CancellationToken>()).Returns([(11, "target-member")]);
        _repo.GetMembersForNotificationAsync(2, Arg.Any<CancellationToken>()).Returns([(22, "source-member"), (11, "target-member")]);

        await _sut.AssignProjectsAsync(1, [10], TestContext.Current.CancellationToken);

        Received.InOrder(() =>
        {
            _repo.GetProjectOrganizationIdsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>());
            _repo.AssignProjectsAsync(1, Arg.Any<List<int>>(), Arg.Any<CancellationToken>());
        });
        _authz.Received(1).InvalidateRoleCache("source-member");
        _authz.Received(1).InvalidateRoleCache("target-member");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(22, Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(11, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AssignProjectsAsync_OrgNotFound_ThrowsNotFound()
    {
        _repo.GetByIdAsync(99, Arg.Any<CancellationToken>())
            .Returns((Organization?)null);

        await Assert.ThrowsAsync<NotFoundException>(() =>
            _sut.AssignProjectsAsync(99, [1], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignProjectsAsync_InvalidProjectIds_ThrowsBadRequest()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1 });
        _repo.CountExistingProjectsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.AssignProjectsAsync(1, [10, 20], TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AssignProjectsAsync_DeduplicatesProjectIds()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>())
            .Returns(new Organization { Id = 1 });
        _repo.CountExistingProjectsAsync(Arg.Is<List<int>>(l => l.Count == 1), Arg.Any<CancellationToken>())
            .Returns(1);
        _repo.AssignProjectsAsync(1, Arg.Any<List<int>>(), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await _sut.AssignProjectsAsync(1, [10, 10, 10], TestContext.Current.CancellationToken);

        await _repo.Received(1).AssignProjectsAsync(1, Arg.Is<List<int>>(l => l.Count == 1), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMyOrganizationsAsync_ReturnsMappedList()
    {
        var org = new Organization { Id = 1, Name = "Acme", Slug = "acme" };
        _repo.GetOrganizationsForUsernameAsync("alice", Arg.Any<CancellationToken>())
            .Returns([(org, OrganizationRole.Owner)]);

        var result = await _sut.GetMyOrganizationsAsync("alice", TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Acme", result[0].Name);
        Assert.Equal(OrganizationRole.Owner, result[0].Role);
    }

    [Fact]
    public async Task GetOrganizationsForUserAsync_ReturnsMappedListWithMemberId()
    {
        var org = new Organization { Id = 1, Name = "Acme", Slug = "acme" };
        _repo.GetOrganizationsForUserIdAsync(5, Arg.Any<CancellationToken>())
            .Returns([(org, OrganizationRole.Member, 42)]);

        var result = await _sut.GetOrganizationsForUserAsync(5, TestContext.Current.CancellationToken);

        Assert.Single(result);
        Assert.Equal("Acme", result[0].Name);
        Assert.Equal(42, result[0].MemberId);
        Assert.Equal(OrganizationRole.Member, result[0].Role);
    }

    [Fact]
    public async Task AssignProjectsAsync_InvalidatesAndNotifiesMembers()
    {
        _repo.GetByIdAsync(1, Arg.Any<CancellationToken>()).Returns(new Organization { Id = 1 });
        _repo.CountExistingProjectsAsync(Arg.Any<List<int>>(), Arg.Any<CancellationToken>()).Returns(1);
        _repo.AssignProjectsAsync(1, Arg.Any<List<int>>(), Arg.Any<CancellationToken>()).Returns(Task.CompletedTask);
        _repo.GetMembersForNotificationAsync(1, Arg.Any<CancellationToken>())
            .Returns([(5, "alice")]);

        await _sut.AssignProjectsAsync(1, [10], TestContext.Current.CancellationToken);

        _authz.Received(1).InvalidateRoleCache("alice");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(5, PermissionChangeReasons.OrganizationMembership, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteOrganizationAsync_NotifiesMembersCapturedBeforeDelete()
    {
        _repo.GetMembersForNotificationAsync(1, Arg.Any<CancellationToken>())
            .Returns([(5, "alice")]);
        _repo.DeleteAsync(1, Arg.Any<CancellationToken>()).Returns(true);

        var result = await _sut.DeleteOrganizationAsync(1, TestContext.Current.CancellationToken);

        Assert.True(result);
        _authz.Received(1).InvalidateRoleCache("alice");
        await _userNotifier.Received(1).NotifyPermissionsChangedAsync(5, PermissionChangeReasons.OrganizationMembership, Arg.Any<CancellationToken>());
    }
}
