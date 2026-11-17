// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Servers.ServerDetailSections;

/// <summary>R-17, R-20: the Overview tab of a server's quality page (grade block, count tiles, facts, fixable and no-fix tables).</summary>
public partial class ServerQualityOverview : ComponentBase
{
    /// <summary>Index of the Findings tab, the tab the fixable tile leads to.</summary>
    internal const int FindingsTab = 1;

    /// <summary>Index of the tab of the findings with no fix, the tab the no-fix tile leads to.</summary>
    internal const int NoFixTab = 2;

    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public ServerQualityDto Server { get; set; } = default!;

    /// <summary>Asks the page to select a tab (the tiles are shortcuts to them).</summary>
    [Parameter] public EventCallback<int> OnSelectTab { get; set; }

    private bool HasCountTiles => Server.OpenCount > 0 || Server.Exploited > 0;

    /// <summary>What sets a server's grade, the first that applies in the order of the ladder.</summary>
    internal enum GradeCause
    {
        None,
        Critical,
        High,
        Other
    }

    /// <summary>The cause of the grade, read from the vulnerabilities that have a fix: those with none are shown but do not
    /// count in the grade.</summary>
    internal static GradeCause CauseOf(ServerQualityDto server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var fixable = ServerHostFixability.Fixable(server);
        if (fixable.Critical > 0) return GradeCause.Critical;
        if (fixable.High > 0) return GradeCause.High;
        if (fixable.Medium + fixable.Low + fixable.Info > 0) return GradeCause.Other;
        return GradeCause.None;
    }

    private string CauseText => CauseOf(Server) switch
    {
        GradeCause.Critical => L["ServerQualityCauseCritical"],
        GradeCause.High => L["ServerQualityCauseHigh"],
        GradeCause.Other => L["ServerQualityCauseOther"],
        _ => L["ServerQualityCauseNone"]
    };

    private string NotGradedText => Server.State == HostVulnerabilityWatchState.Watched
        ? L["ServerQualityNotGradedPending"]
        : string.Format(L["ServerQualityNotGradedUnsupported"], Server.OsName);

    private static string DateText(DateTime? date) => date?.ToLocalTime().ToString("g") ?? string.Empty;
}
