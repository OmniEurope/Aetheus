// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Services.DomainEvents;
using Microsoft.AspNetCore.Http;

namespace Aetheus.Back.Components.Pipelines;

/// <summary> Users (L1) asks, before deactivating or deleting a pipeline owner, that the pipelines
/// pass to another active user. The transfer runs as the administrator of the current request, so
/// it is authorized and audited exactly like the transfer action of the pipeline settings page.
/// Dispatched strictly: a refusal reaches the Users caller, which also re-reads the owned pipelines
/// afterwards. </summary>
public sealed class UserPipelinesTransferHandler(
    IPipelineOwnershipService ownership,
    IHttpContextAccessor httpContextAccessor) : IDomainEventHandler<UserPipelinesTransferRequestedEvent>
{
    public async Task HandleAsync(UserPipelinesTransferRequestedEvent domainEvent, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(domainEvent);
        var actor = httpContextAccessor.HttpContext?.User
            ?? throw new BadRequestException("A pipeline owner transfer needs an authenticated administrator.");
        var transferred = await ownership.TransferWithoutReportAsync(actor, new PipelineOwnerTransferRequest
        {
            CurrentOwnerUsername = domainEvent.FromUsername,
            NewOwnerUsername = domainEvent.ToUsername
        }, ct).ConfigureAwait(false);
        if (!transferred)
            throw new BadRequestException("Only a platform administrator can transfer the pipelines of a user.");
    }
}
