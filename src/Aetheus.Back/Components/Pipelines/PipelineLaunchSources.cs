// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>Trigger sources shared by the launch paths and the refused-launch recorder, kept apart from both so
/// neither depends on the other for a constant.</summary>
internal static class PipelineLaunchSources
{
    /// <summary>The trigger source of a launch someone asked for in the interface or the API: the person already
    /// read the refusal, so its run says "manual launch" rather than "automated launch".</summary>
    internal const string Manual = "Manual";
}
