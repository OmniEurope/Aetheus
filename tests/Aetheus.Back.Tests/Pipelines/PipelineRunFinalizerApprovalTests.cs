// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Hubs;
using Aetheus.Back.Services;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>Finalization closes the ended run's pending approvals.</summary>
public sealed class PipelineRunFinalizerApprovalTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly PipelineRunFinalizer _sut;

    public PipelineRunFinalizerApprovalTests()
    {
        var hub = Substitute.For<IHubContext<PipelineHub>>();
        var clients = Substitute.For<IHubClients>();
        clients.Groups(Arg.Any<IReadOnlyList<string>>()).Returns(Substitute.For<IClientProxy>());
        hub.Clients.Returns(clients);
        _sut = new PipelineRunFinalizer(
            _repo, hub, Substitute.For<IDomainEventDispatcher>(), Substitute.For<ISecretMaskingService>(), TimeProvider.System);
    }

    [Theory]
    [InlineData(PipelineStatus.Failed)]
    [InlineData(PipelineStatus.Cancelled)]
    [InlineData(PipelineStatus.Partial)]
    [InlineData(PipelineStatus.Success)]
    public async Task EndingARun_ClosesItsPendingApprovals_WithTheEndedRunReason(PipelineStatus status)
    {
        await _sut.CompleteRunAsync(2323, status, TestContext.Current.CancellationToken);

        await _repo.Received(1).CloseApprovalsOfEndedRunsAsync(
            2323, Arg.Any<DateTime>(), EndedRunApprovals.Reason, Arg.Any<CancellationToken>());
    }
}
