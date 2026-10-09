// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Who cancels a run and why (recette R2-103): written to the audit log and kept on the run, so the run
/// page can say who stopped it. <see cref="By"/> is a user name, or <see cref="SystemAuthor"/> when
/// Aetheus cancels on its own (latest-wins replacement, cancellation recovery).
/// </summary>
public sealed record PipelineRunCancellation(string By, string Reason)
{
    public const string SystemAuthor = "system";

    public static PipelineRunCancellation ByUser(string? username) =>
        new(string.IsNullOrWhiteSpace(username) ? "unknown user" : username, "Cancelled by a user");

    public static PipelineRunCancellation BySystem(string reason) => new(SystemAuthor, reason);

    /// <summary>The cancellation a parent's cancellation carries down to a run it triggered.</summary>
    public PipelineRunCancellation ForChildOf(int parentRunId) =>
        this with { Reason = $"Parent run #{parentRunId} was cancelled: {Reason}" };
}
