// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using Aetheus.Shared.Components.VulnerabilityWatch;

namespace Aetheus.Front.Components.Analysis;

/// <summary>R-14: the recommendation of the CVE watch for the match an analysis finding was written for, and the way to have the
/// upgrade prepared. The block reads the match of the finding and its latest attempt itself; a finding with no match shows nothing.</summary>
public partial class CveWatchRemediationBlock : ComponentBase
{
    private static readonly CveRecommendationKind[] PreparableKinds =
        [CveRecommendationKind.BumpDependency, CveRecommendationKind.MajorUpgrade, CveRecommendationKind.BumpBaseImage, CveRecommendationKind.BumpRuntime];

    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;

    [Parameter, EditorRequired] public int FindingId { get; set; }

    /// <summary>Whether the caller may write in the project: only then is the upgrade offered (the back checks it again).</summary>
    [Parameter] public bool CanWrite { get; set; }

    private CveWatchMatchDto? _match;
    private CveRemediationDto? _remediation;
    private CveRemediationFallbackDto? _fallback;
    private int? _loadedFindingId;
    private bool _busy;
    private bool _loadFailed;

    private CveRemediationFallbackDto? Fallback => _fallback ?? _remediation?.Fallback;

    /// <summary>An attempt that is not over (asked for, running, a branch or a pull request waiting, merged and not yet closed by
    /// the watch) is the one to follow, not to repeat.</summary>
    private static bool IsActive(VulnerabilityRemediationStatus? status) =>
        status is VulnerabilityRemediationStatus.Requested or VulnerabilityRemediationStatus.Running
            or VulnerabilityRemediationStatus.BranchPushed or VulnerabilityRemediationStatus.PullRequestOpen
            or VulnerabilityRemediationStatus.Merged;

    /// <summary>R-48: the platform only prepares a change to a file of the project; a runtime that no file names (it
    /// comes with the SDK or the base image) is refused every time, so the action is not offered for it.</summary>
    private bool CanPrepare => CanWrite
        && _match is { ClosedAt: null, Status: VulnerabilityMatchStatus.Affected, Recommendation: { Files.Count: > 0 } recommendation }
        && PreparableKinds.Contains(recommendation.Kind)
        && !IsActive(_remediation?.Status);

    private static OmniTone AttemptTone(VulnerabilityRemediationStatus status) => status switch
    {
        VulnerabilityRemediationStatus.Merged => OmniTone.Success,
        VulnerabilityRemediationStatus.Failed => OmniTone.Danger,
        VulnerabilityRemediationStatus.Abandoned => OmniTone.Neutral,
        _ => OmniTone.Info
    };

    /// <summary>R-39: the version the watch matched and the inventory it was read from (origin, then the branch or the shortened commit).</summary>
    private string FoundInText
    {
        get
        {
            if (_match is not { } match) return string.Empty;
            if (match.InventoryOrigin is not { } origin) return match.Version;
            var label = L.Localize(origin);
            var source = string.IsNullOrWhiteSpace(match.InventoryRef) ? label : $"{label} - {ShortId.Shorten(match.InventoryRef)}";
            return $"{match.Version} ({source})";
        }
    }

    /// <summary>R-39: the version to move to, from the recommendation first, else the one the advisory publishes; the "no fix" wording when none.</summary>
    private string FixedVersionText => _match?.Recommendation?.TargetVersion ?? _match?.FixedVersion ?? L["CveWatchNoFix"].Value;

    private string? RecommendationText
    {
        get
        {
            if (_match is not { Recommendation: { } recommendation } match) return null;
            var target = recommendation.TargetVersion ?? L["CveRemediationFixedVersion"].Value;
            var key = recommendation.Kind switch
            {
                CveRecommendationKind.BumpDependency => "CveRemediationBump",
                CveRecommendationKind.MajorUpgrade => "CveRemediationMajor",
                CveRecommendationKind.BumpBaseImage => "CveRemediationBaseImage",
                CveRecommendationKind.BumpRuntime => "CveRemediationRuntime",
                CveRecommendationKind.OsUpdate => "CveRemediationOsUpdate",
                CveRecommendationKind.EndOfLife => "CveRemediationEndOfLife",
                _ => "CveRemediationNoFix"
            };
            return string.Format(L[key], match.PackageName, match.Version, target);
        }
    }

    protected override async Task OnParametersSetAsync()
    {
        if (_loadedFindingId == FindingId) return;
        _loadedFindingId = FindingId;
        await ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        _busy = true;
        _loadFailed = false;
        try
        {
            _match = await Api.Analysis.GetCveWatchMatchByFindingAsync(FindingId);
            _remediation = _match is { ClosedAt: null } match ? await Api.Analysis.GetCveRemediationAsync(match.MatchId) : null;
            _fallback = null;
        }
        catch (HttpRequestException)
        {
            _loadFailed = true;
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task PrepareAsync()
    {
        if (_match is null) return;
        _busy = true;
        try
        {
            var result = await Api.Analysis.RequestCveRemediationAsync(_match.MatchId);
            _remediation = result.Remediation;
            if (result.CouldNotPrepare)
            {
                _fallback = result.Remediation.Fallback;
                return;
            }

            _fallback = null;
            Toast.Success("Saved", result.AlreadyActive ? "CveRemediationAlreadyActive" : "CveRemediationStarted");
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "CveRemediationRequestFailed");
        }
        finally
        {
            _busy = false;
        }
    }
}
