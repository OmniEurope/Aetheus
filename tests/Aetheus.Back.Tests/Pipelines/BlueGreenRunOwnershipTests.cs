// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// What the control plane tells the agent about who owns a blue-green transaction. A pipeline that declares
/// no environment never holds the lock, so the list of runs in flight is what lets the agent recognise a
/// dead run's transaction as an orphan (runs 1422/1423 on the simulator stayed blocked without it).
/// </summary>
public sealed class BlueGreenRunOwnershipTests
{
    [Fact]
    public async Task Stamp_ListsTheRunsInFlight_ForAStepThatMayUndoAnotherRun()
    {
        var variables = new Dictionary<string, string>();
        var locks = Substitute.For<IPipelineResourceLockRepository>();

        await BlueGreenRunOwnership.StampAsync(
            variables, 1423, locks, _ => Task.FromResult(new List<int> { 1424, 1423 }), TestContext.Current.CancellationToken);

        Assert.Equal("1423", variables[BlueGreenRunOwnership.OwnerRunVariable]);
        Assert.Equal("1423,1424", variables[BlueGreenRunOwnership.ActiveRunsVariable]);
        Assert.False(variables.ContainsKey(BlueGreenRunOwnership.ExclusiveVariable));
    }

    // An empty list is a real answer ("nothing else in flight"), distinct from an absent one.
    [Fact]
    public async Task Stamp_WritesAnEmptyList_WhenNoRunIsInFlight()
    {
        var variables = new Dictionary<string, string>();

        await BlueGreenRunOwnership.StampAsync(
            variables, 7, locks: null, _ => Task.FromResult(new List<int>()), TestContext.Current.CancellationToken);

        Assert.Equal(string.Empty, variables[BlueGreenRunOwnership.ActiveRunsVariable]);
    }

    [Fact]
    public async Task Stamp_WithoutAList_LeavesTheVariableAbsent_AndKeepsTheLock()
    {
        var variables = new Dictionary<string, string>();
        var locks = Substitute.For<IPipelineResourceLockRepository>();
        locks.HoldsAnyAsync(9, Arg.Any<CancellationToken>()).Returns(true);

        await BlueGreenRunOwnership.StampAsync(variables, 9, locks, activeRuns: null, TestContext.Current.CancellationToken);

        Assert.False(variables.ContainsKey(BlueGreenRunOwnership.ActiveRunsVariable));
        Assert.Equal("true", variables[BlueGreenRunOwnership.ExclusiveVariable]);
    }

    [Theory]
    [InlineData(OperationKind.BlueGreenUp, true)]
    [InlineData(OperationKind.BlueGreenRetire, true)]
    [InlineData(OperationKind.BlueGreenRollback, false)]
    [InlineData(OperationKind.BlueGreenSwitch, false)]
    [InlineData(OperationKind.BlueGreenCommit, false)]
    public void OnlyUpAndRetire_ReceiveTheList(OperationKind operation, bool expected) =>
        Assert.Equal(expected, BlueGreenRunOwnership.MayUndoAnotherRun(operation));
}
