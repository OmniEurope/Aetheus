// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Data.Entities;

/// <summary>
/// A user's saved choice about the e-mail of one notification event type for one pipeline: today whether a failure
/// of that pipeline (<c>pipeline.failed</c>) is e-mailed to him. It narrows <see cref="UserNotificationPreference"/>,
/// the choice for the whole event type, and never replaces it: the in-app notification does not depend on it.
///
/// A table of its own rather than a pipeline column on <see cref="UserNotificationPreference"/>: that table is
/// unique on (user, event type), and widening the key is a destructive index change the expand-only migration guard
/// refuses. A pipeline without a row takes the default of the event type for that pipeline (off for a pipeline a
/// schedule starts, on otherwise). Deleting the user or the pipeline deletes its rows.
/// </summary>
public class UserPipelineNotificationPreference
{
    public int UserId { get; set; }
    public int PipelineId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public bool IsEnabled { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public User User { get; set; } = default!;
    public Pipeline Pipeline { get; set; } = default!;
}
