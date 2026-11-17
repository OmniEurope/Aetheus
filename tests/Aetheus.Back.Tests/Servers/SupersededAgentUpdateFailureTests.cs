// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Servers;

namespace Aetheus.Back.Tests.Servers;

/// <summary>A failed update the running agent already left behind is not shown as the server's state any more (seen on
/// vpssim: "Échec 1.2.0 → 1.5.0" while the agent ran 1.6.0); every other request is kept.</summary>
public sealed class SupersededAgentUpdateFailureTests
{
    private static AgentUpdateRequestSummaryDto Request(AgentUpdateRequestStatus status, string target = "1.5.0") =>
        new() { RequestId = 4, ObservedVersion = "1.2.0", TargetVersion = target, Status = status };

    [Theory]
    [InlineData("1.6.0")]
    [InlineData("1.5.0")]
    [InlineData("1.6.0+7bdea98b7")]
    public void AFailureAtOrBelowTheRunningVersion_IsDropped(string running) =>
        Assert.Null(SupersededAgentUpdateFailure.Drop(Request(AgentUpdateRequestStatus.Failed), running));

    [Theory]
    [InlineData("1.2.0")]
    [InlineData("1.4.9")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("unknown")]
    public void AFailureAboveTheRunningVersion_OrAnUnreadableVersion_IsKept(string? running)
    {
        var request = Request(AgentUpdateRequestStatus.Failed);
        Assert.Same(request, SupersededAgentUpdateFailure.Drop(request, running));
    }

    [Theory]
    [InlineData(AgentUpdateRequestStatus.Confirmed)]
    [InlineData(AgentUpdateRequestStatus.Downloading)]
    [InlineData(AgentUpdateRequestStatus.WaitingForIdle)]
    public void ARequestThatDidNotFail_IsKept(AgentUpdateRequestStatus status)
    {
        var request = Request(status);
        Assert.Same(request, SupersededAgentUpdateFailure.Drop(request, "1.6.0"));
    }

    [Fact]
    public void AnUnreadableTarget_IsKept()
    {
        var request = Request(AgentUpdateRequestStatus.Failed, "next");
        Assert.Same(request, SupersededAgentUpdateFailure.Drop(request, "1.6.0"));
    }

    [Fact]
    public void NoRequest_StaysNone() => Assert.Null(SupersededAgentUpdateFailure.Drop(null, "1.6.0"));
}
