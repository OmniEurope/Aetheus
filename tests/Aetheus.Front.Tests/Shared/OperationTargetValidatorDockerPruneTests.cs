// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Tests;

/// <summary>R-119: a Docker prune target lists what the prune covers, each at most once and in this order. The target
/// reaches a root-owned command, so anything else (a shell metacharacter, a space, a line break) is refused.</summary>
public sealed class OperationTargetValidatorDockerPruneTests
{
    [Theory]
    [InlineData("containers")]
    [InlineData("containers,images")]
    [InlineData("containers,volumes")]
    [InlineData("containers,images,volumes")]
    [InlineData("images")]
    [InlineData("images,volumes")]
    [InlineData("volumes")]
    public void WhatAPruneCovers_IsAccepted(string target) =>
        Assert.True(OperationTargetValidator.IsValid(OperationKind.DockerPrune, target));

    [Theory]
    [InlineData("containers,containers")]
    [InlineData("images,containers")]
    [InlineData("volumes,images")]
    [InlineData("containers,images,images")]
    [InlineData("system")]
    [InlineData("all")]
    [InlineData("Containers")]
    [InlineData("containers,")]
    [InlineData(",containers")]
    [InlineData("containers,,images")]
    [InlineData("container")]
    [InlineData("networks")]
    public void AnythingElseThanTheOrderedKnownTargets_IsRefused(string target) =>
        Assert.False(OperationTargetValidator.IsValid(OperationKind.DockerPrune, target));

    [Theory]
    [InlineData("containers;rm -rf /")]
    [InlineData("containers;")]
    [InlineData("containers&&id")]
    [InlineData("containers|id")]
    [InlineData("containers$(id)")]
    [InlineData("$(id)")]
    [InlineData("containers`id`")]
    [InlineData("`id`")]
    [InlineData("containers\nid")]
    [InlineData("containers\n")]
    [InlineData("containers\r\n")]
    [InlineData("\ncontainers")]
    [InlineData("containers images")]
    [InlineData("containers, images")]
    [InlineData(" containers")]
    [InlineData("containers ")]
    [InlineData("containers\t")]
    [InlineData("containers>out")]
    public void AnInjectionAttempt_IsRefused(string target) =>
        Assert.False(OperationTargetValidator.IsValid(OperationKind.DockerPrune, target));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoTarget_IsRefused(string? target) =>
        Assert.False(OperationTargetValidator.IsValid(OperationKind.DockerPrune, target));
}
