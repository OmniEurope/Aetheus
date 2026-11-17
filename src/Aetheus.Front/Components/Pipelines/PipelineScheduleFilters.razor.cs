// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The "Genre" and "Serveur" filters of the build planner (recette R-8). The page hands over the plan (for the
/// servers it can offer and the genres that exist) and the choice it read from the URL; a pick navigates, which is the only
/// place the choice is kept.</summary>
public partial class PipelineScheduleFilters
{
    private const string AllGenres = "all";

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    /// <summary>The plan the page read: the servers and genres the filters offer come from it.</summary>
    [Parameter, EditorRequired] public SchedulePlanDto Plan { get; set; } = default!;

    /// <summary>The genre chosen in the URL (<see cref="ScheduleGenres"/>), null for all.</summary>
    [Parameter] public string? Kind { get; set; }

    /// <summary>The server chosen in the URL, null for all.</summary>
    [Parameter] public int? Server { get; set; }

    private string KindValue => Kind ?? AllGenres;

    private IReadOnlyList<OmniOption<string>> GenreOptions =>
    [
        new(AllGenres, L["All"].Value),
        .. PipelineScheduleFilter.Genres(Plan, Kind).Select(genre => new OmniOption<string>(genre, L[GenreKey(genre)].Value))
    ];

    private IReadOnlyList<OmniOption<int?>> ServerOptions
    {
        get
        {
            var servers = PipelineScheduleFilter.Servers(Plan).Select(server => new OmniOption<int?>(server.Id, server.Name)).ToList();
            // A server named by the URL that the plan does not list is still shown as chosen, never as "all".
            if (Server is { } chosen && servers.All(option => option.Value != chosen))
                servers.Add(new OmniOption<int?>(chosen, string.Create(CultureInfo.CurrentCulture, $"{L["Server"].Value} {chosen}")));
            return servers;
        }
    }

    private static string GenreKey(string genre) => genre switch
    {
        ScheduleGenres.Pipeline => "ScheduleGenrePipeline",
        ScheduleGenres.Scan => "ScheduleGenreScan",
        ScheduleGenres.AiTask => "ScheduleGenreAiTask",
        ScheduleGenres.Backup => "ScheduleGenreBackup",
        ScheduleGenres.CveWatch => "ScheduleGenreCveWatch",
        ScheduleGenres.Reboot => "ScheduleGenreReboot",
        ScheduleGenres.MachineJob => "ScheduleGenreMachineJob",
        _ => throw new ArgumentOutOfRangeException(nameof(genre), genre, "Every genre has its own label.")
    };

    private static OmniIconName GenreIcon(string genre) => genre switch
    {
        ScheduleGenres.Pipeline => OmniIconName.Graph,
        ScheduleGenres.Scan => OmniIconName.HardDrive,
        ScheduleGenres.AiTask => OmniIconName.Robot,
        ScheduleGenres.Backup => OmniIconName.Archive,
        ScheduleGenres.CveWatch => OmniIconName.ShieldWarning,
        ScheduleGenres.Reboot => OmniIconName.Sync,
        ScheduleGenres.MachineJob => OmniIconName.TerminalWindow,
        _ => OmniIconName.Layers
    };

    private void OnKindChanged(string genre) => GoTo(genre == AllGenres ? null : PipelineScheduleFilter.ParseKind(genre), Server);

    private void OnServerChanged(int? server) => GoTo(Kind, server);

    private void GoTo(string? kind, int? server) =>
        Nav.NavigateTo(Nav.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            [PipelineScheduleFilter.KindKey] = kind,
            [PipelineScheduleFilter.ServerKey] = server is { } id ? id.ToString(CultureInfo.InvariantCulture) : null
        }));
}
