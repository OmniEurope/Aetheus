// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Users.Events;
using Aetheus.Back.Services.DomainEvents;

namespace Aetheus.Back.Components.Users;

/// <summary>Deactivating or deleting a pipeline owner requires an active replacement owner or an
/// explicit force request, because inactive owners cannot authorize chained runs.</summary>
internal sealed class UserPipelineOwnershipGuard(
    IUserRepository repo,
    IAuditService audit,
    IDomainEventDispatcher domainEvents)
{
    private const int MaxListedNames = 20;

    /// <summary>
    /// Lets the deactivation or deletion of <paramref name="username"/> proceed, after transferring the
    /// pipelines it owns to <paramref name="transferTo"/> when one is named. Throws
    /// <see cref="ConflictException"/> when the user owns pipelines and neither a target nor
    /// <paramref name="force"/> is given, and <see cref="BadRequestException"/> for an unusable target.
    /// Must run before the user entity is modified: the transfer saves its own changes.
    /// </summary>
    internal async Task EnsureOwnedPipelinesHandledAsync(
        int userId, string username, string? transferTo, bool force, string operation, CancellationToken ct)
    {
        var owned = await repo.GetOwnedPipelineNamesAsync(username, ct).ConfigureAwait(false);
        if (owned.Length == 0) return;

        if (!string.IsNullOrWhiteSpace(transferTo))
        {
            var targetName = transferTo.Trim().ToLowerInvariant();
            var target = await repo.FindByUsernameAsync(targetName, ct).ConfigureAwait(false);
            if (target is null || !target.IsActive || target.Id == userId)
                throw new BadRequestException(
                    $"The pipelines of '{username}' can only be transferred to another active user; '{targetName}' is not one.");

            await domainEvents.DispatchStrictAsync(
                new UserPipelinesTransferRequestedEvent(username, target.Username), ct).ConfigureAwait(false);
            var remaining = await repo.GetOwnedPipelineNamesAsync(username, ct).ConfigureAwait(false);
            if (remaining.Length > 0)
                throw new ConflictException(
                    $"The transfer of the pipelines of '{username}' to '{target.Username}' did not complete; still owned: {Describe(remaining)}.");
            return;
        }

        if (force)
        {
            await audit.LogAsync(operation == "Deleted" ? "DeletedWithOwnedPipelines" : "DeactivatedWithOwnedPipelines", "User", userId,
                $"{username} still owns {owned.Length} pipeline(s), chained runs will be refused: {Describe(owned)}",
                ct).ConfigureAwait(false);
            return;
        }

        throw new ConflictException(
            $"User '{username}' owns {owned.Length} pipeline(s): {Describe(owned)}. Once the owner is inactive, " +
            "every chained run of these pipelines is refused (F-EXEC-1b). Name an active user to transfer them to, or force the operation.");
    }

    private static string Describe(string[] names) =>
        names.Length <= MaxListedNames
            ? string.Join(", ", names)
            : $"{string.Join(", ", names.Take(MaxListedNames))} and {names.Length - MaxListedNames} more";
}
