// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The kinds of source the planner's "Genre" filter names, as they are written in the URL (<c>?kind=</c>) and in the
/// bar. The restore-check of a backup policy goes with the backups.</summary>
internal static class ScheduleGenres
{
    internal const string Pipeline = "pipeline";
    internal const string Scan = "scan";
    internal const string AiTask = "ai";
    internal const string Backup = "backup";
    internal const string CveWatch = "cve";
    internal const string Reboot = "reboot";
    internal const string MachineJob = "machine";

    /// <summary>The genres of the bar, in order (the "all" entry comes first and is not a genre).</summary>
    internal static readonly string[] All = [Pipeline, Scan, AiTask, Backup, CveWatch, Reboot, MachineJob];

    /// <summary>The genre a server action belongs to.</summary>
    internal static string Of(ServerActionKind kind) => kind switch
    {
        ServerActionKind.RkhunterScan => Scan,
        ServerActionKind.AiTask => AiTask,
        ServerActionKind.AppBackup or ServerActionKind.RestoreCheck => Backup,
        ServerActionKind.CveWatch => CveWatch,
        ServerActionKind.ServerReboot or ServerActionKind.MachineReboot => Reboot,
        ServerActionKind.MachineJob => MachineJob,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Every kind of action belongs to a genre.")
    };
}

/// <summary>
/// The planner's "Genre" and "Serveur" filters (recette R-8, mockup <c>planificateur-actions-serveur-maquette.html</c>), kept in
/// the URL like the view: <c>?kind=pipeline|scan|ai|backup|cve&amp;server=&lt;id&gt;</c>. A missing or unreadable value means "not
/// chosen" (everything). A filter hides sources from the calendar and the cards only: the overlap minutes the server filled
/// and the tiles stay those of the whole plan, since a pipeline and an action overlap whatever the server. A pipeline
/// belongs to no server, so choosing a server hides every pipeline, as the mockup does; so does it for an action with no
/// server (the CVE watch, an AI task owned by a project).
/// </summary>
internal static class PipelineScheduleFilter
{
    internal const string KindKey = "kind";
    internal const string ServerKey = "server";

    /// <summary>The genre named by the URL, or null when absent or not one of the five.</summary>
    internal static string? ParseKind(string? value) => ScheduleGenres.All.Contains(value, StringComparer.Ordinal) ? value : null;

    /// <summary>The server id named by the URL, or null when absent or not a positive number.</summary>
    internal static int? ParseServer(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : null;

    /// <summary>The plan reduced to the chosen genre and server; the same plan, unchanged, when nothing is chosen.</summary>
    internal static SchedulePlanDto Apply(SchedulePlanDto plan, string? kind, int? serverId)
    {
        if (kind is null && serverId is null) return plan;
        return plan with
        {
            Tasks = (kind is null || kind == ScheduleGenres.Pipeline) && serverId is null ? plan.Tasks : [],
            ServerActions =
            [
                .. plan.ServerActions.Where(action =>
                    (kind is null || ScheduleGenres.Of(action.Kind) == kind) && (serverId is null || action.ServerId == serverId))
            ]
        };
    }

    /// <summary>The servers the plan's actions run on, by name: what the "Serveur" list offers.</summary>
    internal static IReadOnlyList<(int Id, string Name)> Servers(SchedulePlanDto plan) =>
    [
        .. plan.ServerActions
            .Where(action => action.ServerId > 0)
            .GroupBy(action => action.ServerId)
            .Select(group => (Id: group.Key, Name: group.First().ServerName))
            .OrderBy(server => server.Name, StringComparer.CurrentCultureIgnoreCase)
    ];

    /// <summary>The genres the bar offers for a plan: the CVE watch and the reboots only when the plan holds them (the CVE
    /// watch for site administrators, a reboot once a server Admin schedules one) or when the URL already names them, so the
    /// bar never shows the selection of an entry it lacks.</summary>
    internal static IReadOnlyList<string> Genres(SchedulePlanDto plan, string? chosen) =>
    [
        .. ScheduleGenres.All.Where(genre => !IsOnDemand(genre) || chosen == genre
            || plan.ServerActions.Any(action => ScheduleGenres.Of(action.Kind) == genre))
    ];

    // The reboots genre holds both the reboots Aetheus orders and the ones the machine decides itself.
    private static bool IsOnDemand(string genre) => genre is ScheduleGenres.CveWatch or ScheduleGenres.Reboot or ScheduleGenres.MachineJob;
}
