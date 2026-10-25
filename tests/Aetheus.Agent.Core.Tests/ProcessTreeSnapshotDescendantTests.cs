// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Agent.Core.Executors;

namespace Aetheus.Agent.Core.Tests;

/// <summary>Which processes count as below a root when the parent ids alone can lie: Windows keeps the parent
/// id of an orphan, so a reused id makes an older, unrelated process look like a child.</summary>
public sealed class ProcessTreeSnapshotDescendantTests
{
    private const int Root = 100;

    [Fact]
    public void ChildrenAndGrandchildren_StartedAfterTheirParent_AreDescendants()
    {
        var parents = new Dictionary<int, int> { [200] = Root, [300] = 200, [999] = 1 };
        var starts = new Dictionary<int, long?> { [Root] = 10, [200] = 20, [300] = 30, [999] = 5, [1] = 1 };

        var descendants = ProcessTreeSnapshot.SelectDescendantIds(Root, parents, id => starts.GetValueOrDefault(id));

        Assert.Equal([200, 300], descendants.Order());
    }

    [Fact]
    public void AnOrphanOlderThanTheRoot_WhoseDeadParentHadTheRootsId_IsNotADescendant()
    {
        // 400 was started by an earlier process 100 that has since exited; the root now reuses id 100.
        var parents = new Dictionary<int, int> { [200] = Root, [400] = Root, [401] = 400 };
        var starts = new Dictionary<int, long?> { [Root] = 50, [200] = 60, [400] = 10, [401] = 12 };

        var descendants = ProcessTreeSnapshot.SelectDescendantIds(Root, parents, id => starts.GetValueOrDefault(id));

        Assert.Equal([200], descendants);
    }

    [Fact]
    public void AChildWhoseStartTimeCannotBeRead_IsNotCountedAsOurs()
    {
        var parents = new Dictionary<int, int> { [200] = Root, [500] = Root };
        var starts = new Dictionary<int, long?> { [Root] = 50, [200] = 60, [500] = null };

        var descendants = ProcessTreeSnapshot.SelectDescendantIds(Root, parents, id => starts.GetValueOrDefault(id));

        Assert.Equal([200], descendants);
    }
}
