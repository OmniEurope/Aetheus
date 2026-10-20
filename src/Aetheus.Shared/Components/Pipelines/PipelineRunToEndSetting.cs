// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The platform setting that allows the "run to end" launch mode. Off unless an administrator sets
/// it to <c>true</c>: on a production instance the mode would let a deployment step run after failed
/// tests, so it is meant for a local mirror that has to test everything in one run.
/// </summary>
public static class PipelineRunToEndSetting
{
    public const string Key = "Pipelines:AllowRunToEnd";

    /// <summary>Only the literal <c>true</c> (any case) allows the mode; anything else, a missing row
    /// included, keeps it off.</summary>
    public static bool IsAllowed(string? value) =>
        bool.TryParse(value, out var allowed) && allowed;
}
