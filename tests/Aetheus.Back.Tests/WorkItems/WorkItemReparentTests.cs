// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.WorkItems;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using NSubstitute;

namespace Aetheus.Back.Tests.WorkItems;

/// <summary>Re-parenting a work item on update: the new parent must exist in the same project, must not be
/// the item itself nor one of its descendants; a refused move saves nothing, an accepted one walks the
/// parent's ancestry to its root and stores the new parent.</summary>
public sealed class WorkItemReparentTests
{
    private readonly IWorkItemRepository _repo = Substitute.For<IWorkItemRepository>();
    private readonly WorkItemService _sut;
    private readonly WorkItem _item = new() { Id = 1, ProjectId = 10, Title = "item", Tags = "[]" };

    public WorkItemReparentTests()
    {
        _sut = new WorkItemService(_repo, Substitute.For<IAuditService>(), TimeProvider.System);
        _repo.FindWorkItemAsync(1, Arg.Any<CancellationToken>()).Returns(_item);
    }

    private void Stored(int id, int projectId, int? parentId) =>
        _repo.FindWorkItemAsync(id, Arg.Any<CancellationToken>())
            .Returns(new WorkItem { Id = id, ProjectId = projectId, ParentId = parentId, Title = $"w{id}", Tags = "[]" });

    private Task<WorkItemDto?> MoveUnderAsync(int? parentId) =>
        _sut.UpdateWorkItemAsync(1, new UpdateWorkItemRequest { Title = "item", ParentId = parentId },
            TestContext.Current.CancellationToken);

    private async Task AssertRefusedAsync(int? parentId, string message)
    {
        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => MoveUnderAsync(parentId));

        Assert.Equal(message, refusal.Message);
        await _repo.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task TheItemItself_IsRefused() =>
        AssertRefusedAsync(1, "A work item cannot be its own parent.");

    [Fact]
    public Task AMissingParent_IsRefused() =>
        AssertRefusedAsync(5, "Parent work item must belong to the same project.");

    [Fact]
    public Task AParentOfAnotherProject_IsRefused()
    {
        Stored(5, projectId: 11, parentId: null);
        return AssertRefusedAsync(5, "Parent work item must belong to the same project.");
    }

    [Fact]
    public Task ADescendant_IsRefusedAsACycle()
    {
        Stored(3, projectId: 10, parentId: 2);
        Stored(2, projectId: 10, parentId: 1);
        return AssertRefusedAsync(3, "Parent work item cannot be a descendant (would create a cycle).");
    }

    [Fact]
    public async Task AParentWhoseAncestryReachesTheRoot_IsStored()
    {
        Stored(3, projectId: 10, parentId: 2);
        Stored(2, projectId: 10, parentId: null);

        var result = await MoveUnderAsync(3);

        Assert.Equal(3, result!.ParentId);
        Assert.Equal(3, _item.ParentId);
        await _repo.Received(1).FindWorkItemAsync(2, Arg.Any<CancellationToken>());
        await _repo.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TheSameParentOrNoParent_NeedsNoLookup()
    {
        _item.ParentId = 4;

        await MoveUnderAsync(4);
        await MoveUnderAsync(null);

        Assert.Null(_item.ParentId);
        await _repo.DidNotReceive().FindWorkItemAsync(4, Arg.Any<CancellationToken>());
        await _repo.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
