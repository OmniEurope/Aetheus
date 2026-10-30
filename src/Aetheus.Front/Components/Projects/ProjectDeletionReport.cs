// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Projects;

/// <summary>One line of the end of a deletion: what it is, where it was, and the detail (a count, an environment,
/// or why it was not removed).</summary>
internal sealed record ProjectDeletionReportRow(string Item, string Place, string Detail);

/// <summary> Recette R-50 (ADR-058): the end of a project deletion, said in the dialog that ran it. What was
/// deleted and what was not, each with where it was and, for what stays, why. Built only from what the server
/// answered (the preview, the agents' tasks and the outcome of the deletion), never assumed. </summary>
internal sealed record ProjectDeletionReport(
    ProjectDeletionOutcome Outcome,
    IReadOnlyList<ProjectDeletionReportRow> Deleted,
    IReadOnlyList<ProjectDeletionReportRow> NotDeleted)
{
    public static ProjectDeletionReport Build(
        ProjectDeletionOutcome outcome,
        ProjectDeletionPreviewDto preview,
        IReadOnlyList<ProjectServerResourceTaskDto> discoveries,
        IReadOnlyList<ProjectServerResourceTaskDto> removals,
        IStringLocalizer<AppStrings> localizer,
        bool cleanupRefused = false)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(localizer);
        var deleted = new List<ProjectDeletionReportRow>();
        var kept = new List<ProjectDeletionReportRow>();
        var aetheus = localizer["ProjectDeletionPlaceAetheus"].Value;
        if (outcome == ProjectDeletionOutcome.Deleted)
            deleted.AddRange(preview.Items.Select(item =>
                new ProjectDeletionReportRow(localizer.Localize(item.Kind), aetheus, item.Count.ToString(CultureInfo.CurrentCulture))));
        else
            kept.Add(new ProjectDeletionReportRow(localizer["ProjectDeletionAetheusData"], aetheus, localizer["ProjectDeletionAetheusDataKept"]));

        foreach (var server in preview.Servers)
            AddServer(server, discoveries, removals, localizer, cleanupRefused, deleted, kept);
        // An environment without a server has nothing Aetheus could remove: nothing to say (recette R-49).
        kept.AddRange(preview.RefusedValues.Where(item => item.Reason != ProjectDeletionRefusal.NoServer).Select(item =>
            new ProjectDeletionReportRow($"{item.Key} = {item.Value}", item.Environment, localizer.Localize(item.Reason))));
        return new ProjectDeletionReport(outcome, deleted, kept);
    }

    private static void AddServer(ProjectDeletionServerDto server,
        IReadOnlyList<ProjectServerResourceTaskDto> discoveries, IReadOnlyList<ProjectServerResourceTaskDto> removals,
        IStringLocalizer<AppStrings> localizer, bool cleanupRefused, List<ProjectDeletionReportRow> deleted, List<ProjectDeletionReportRow> kept)
    {
        var place = server.ServerName;
        var everything = localizer["ProjectDeletionServerEverything"].Value;
        if (server.State != ProjectDeletionServerState.Ready)
        {
            kept.Add(new ProjectDeletionReportRow(everything, place, StateText(server.State, localizer)));
            return;
        }
        var discovery = discoveries.LastOrDefault(task => task.ServerId == server.ServerId);
        if (discovery is null)
        {
            kept.Add(new ProjectDeletionReportRow(everything, place, localizer["ProjectDeletionServerNotAsked"]));
            return;
        }
        AddUnreadable(discovery, place, localizer, kept);
        if (discovery.Failure is { Length: > 0 } readFailure)
            kept.Add(new ProjectDeletionReportRow(everything, place, readFailure));

        var removal = removals.LastOrDefault(task => task.ServerId == server.ServerId);
        var answered = new HashSet<(ProjectServerResourceKind, string)>();
        if (removal is not null)
        {
            foreach (var resource in removal.Resources)
            {
                answered.Add((resource.Kind, resource.Name));
                if (resource.Outcome is ProjectServerResourceOutcome.Removed or ProjectServerResourceOutcome.Absent)
                    deleted.Add(Row(resource, place, localizer, resource.Environment ?? localizer["ProjectDeletionProjectWide"]));
                else
                    kept.Add(Row(resource, place, localizer, resource.Reason is { Length: > 0 } reason ? reason : localizer.Localize(resource.Outcome)));
            }
            AddUnreadable(removal, place, localizer, kept);
            if (removal.Failure is { Length: > 0 } removalFailure)
                kept.Add(new ProjectDeletionReportRow(everything, place, removalFailure));
        }
        // Found and never answered for: the cleanup was refused, not asked (the agent lacks a capability) or never removed.
        var why = cleanupRefused && removal is null ? localizer["ProjectDeletionServerRequestFailed"].Value
            : server.MissingCapabilities.Count > 0
            ? localizer["ProjectDeletionServerMissingCapabilities", string.Join(", ", server.MissingCapabilities)].Value
            : localizer["ProjectDeletionNotRemoved"].Value;
        kept.AddRange(discovery.Resources
            .Where(resource => resource.Outcome == ProjectServerResourceOutcome.Found && !answered.Contains((resource.Kind, resource.Name)))
            .Select(resource => Row(resource, place, localizer, why)));
    }

    private static void AddUnreadable(ProjectServerResourceTaskDto task, string place, IStringLocalizer<AppStrings> localizer,
        List<ProjectDeletionReportRow> kept) =>
        kept.AddRange(task.Unreadable.Select(item =>
            new ProjectDeletionReportRow($"{localizer.Localize(item.Kind)} ({item.Scope})", place, item.Reason)));

    private static ProjectDeletionReportRow Row(ProjectServerResourceDto resource, string place, IStringLocalizer<AppStrings> localizer, string detail) =>
        new($"{localizer.Localize(resource.Kind)} {resource.Name}", place, detail);

    internal static string StateText(ProjectDeletionServerState state, IStringLocalizer<AppStrings> localizer) => state switch
    {
        ProjectDeletionServerState.Offline => localizer["ProjectDeletionServerOffline"],
        ProjectDeletionServerState.AgentTooOld => localizer["ProjectDeletionServerAgentTooOld"],
        ProjectDeletionServerState.NoWriteAccess => localizer["ProjectDeletionServerNoWriteAccess"],
        _ => string.Empty
    };
}
