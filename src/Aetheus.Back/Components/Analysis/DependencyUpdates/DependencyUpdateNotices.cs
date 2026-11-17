// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Notifications;
using Aetheus.Back.Data.Entities;
using Aetheus.Shared.Components.Notifications;

namespace Aetheus.Back.Components.Analysis.DependencyUpdates;

/// <summary>What the step after a green CI and QA did, as the notification words it (the <c>Outcome</c> row of the mail).</summary>
internal enum DependencyUpdateReadyOutcome
{
    PullRequestOpened,
    PullRequestToOpenOnOrigin
}

/// <summary>What retiring the branch of a resolved update did, as the notification words it (the <c>Outcome</c> row of the mail).</summary>
internal enum DependencyUpdateClosedOutcome
{
    BranchDeleted,
    BranchAlreadyGone,
    BranchKeptMirror,
    BranchKeptProtected
}

/// <summary>
/// R-30: the result of an update as a notification event of its project, which the notification service sends to the platform rules and
/// to the e-mail of the subscribers of the project (the same mechanism, templates and localization as every other event). The recipients
/// are never chosen here.
/// </summary>
internal sealed class DependencyUpdateNotices(INotificationService notifications, DependencyUpdateRepository repository)
{
    public async Task ReadyAsync(DependencyUpdate update, DependencyUpdateReadyOutcome outcome, CancellationToken ct) =>
        await notifications.SendEventAsync(
            NotificationEventTypes.DependencyUpdateReady, await PayloadAsync(update, outcome.ToString(), null, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

    public async Task ClosedAsync(DependencyUpdate update, DependencyUpdateClosedOutcome outcome, string? detail, CancellationToken ct) =>
        await notifications.SendEventAsync(
            NotificationEventTypes.DependencyUpdateClosed, await PayloadAsync(update, outcome.ToString(), detail, ct).ConfigureAwait(false), ct)
            .ConfigureAwait(false);

    private async Task<Dictionary<string, object?>> PayloadAsync(DependencyUpdate update, string outcome, string? detail, CancellationToken ct)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ProjectId"] = update.ProjectId,
            ["ProjectName"] = await repository.GetProjectNameAsync(update.ProjectId, ct).ConfigureAwait(false),
            ["PackageName"] = update.PackageName,
            ["FromVersion"] = update.FromVersion,
            ["TargetVersion"] = update.TargetVersion,
            ["Advisory"] = update.Advisory,
            ["Branch"] = update.Branch,
            ["Outcome"] = outcome
        };
        if (!string.IsNullOrWhiteSpace(detail)) payload["Detail"] = detail;
        return payload;
    }
}
