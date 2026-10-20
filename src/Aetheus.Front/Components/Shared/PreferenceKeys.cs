// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The keys of the Aetheus preferences kept per user on the server (recette R1-66), beside OE's own
/// (<c>omni.appearance</c>, <c>omni.grid.*</c>). They follow the server's key rules
/// (<see cref="UserPreferenceRules"/>).
/// </summary>
internal static class PreferenceKeys
{
    public const string Appearance = "aetheus.appearance";
    public const string Language = "aetheus.language";
    public const string ActiveOrganization = "aetheus.organizations.active";
    public const string ProjectFavorites = "aetheus.projects.favorites";
    public const string ProjectViewMode = "aetheus.projects.view-mode";
    public const string ServerHiddenColumns = "aetheus.servers.hidden-columns";
    public const string PipelineListView = "aetheus.pipelines.view-tab";

    /// <summary>The child runs left open in run timelines: one map, run id to child run ids, kept short.</summary>
    public const string RunTimelineExpanded = "aetheus.runs.timeline-expanded";

    /// <summary>The unmet-requirement warning dismissed for one project (the signature of the list dismissed).</summary>
    public static string UnmetDismissed(int projectId) =>
        "aetheus.projects." + projectId.ToString(CultureInfo.InvariantCulture) + ".unmet-dismissed";

    /// <summary>Set once the finished "Prepare this project" checklist of one project has been shown its last time (R3-67).</summary>
    public static string GettingStartedClosed(int projectId) =>
        "aetheus.projects." + projectId.ToString(CultureInfo.InvariantCulture) + ".getting-started-closed";

    /// <summary>The node positions laid out by hand in one pipeline's Visual editor.</summary>
    public static string VisualLayout(int pipelineId) =>
        "aetheus.pipelines." + pipelineId.ToString(CultureInfo.InvariantCulture) + ".visual-layout";
}
