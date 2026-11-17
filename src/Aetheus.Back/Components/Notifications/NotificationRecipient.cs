// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Notifications;

/// <summary>A user an event may be notified to, before the read permission on its project is
/// checked; <see cref="Email"/> is the account address, null when it has none.</summary>
public sealed record NotificationRecipient(int UserId, string Username, string? Email = null);

/// <summary>A pipeline as the failure-mail choice sees it: its name, and whether a schedule starts it (its mails
/// are off by default then).</summary>
public sealed record PipelineMailInfo(int Id, string Name, bool IsScheduled);

/// <summary>A pipeline for which a user saved a failure-mail choice.</summary>
public sealed record PipelineChoiceRow(int PipelineId, string PipelineName, bool IsEnabled, bool IsScheduled);
