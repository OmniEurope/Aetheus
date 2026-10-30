// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

public partial class PipelineRunPreflightDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public IReadOnlyList<PreflightStageDto> Unresolved { get; set; } = [];

    private List<CauseGroup> Groups => BuildGroups();

    /// <summary>The stages blocked because no agent matches their target: the only ones the "no agent matches" summary
    /// is about. A stage blocked by an empty server parameter is not one of them (R-63).</summary>
    private int NoAgentCount => Unresolved.Count(stage => stage.EmptyTargetParameter is null);

    /// <summary>One distinct blocking cause (one French sentence), the stages it blocks, what to do about
    /// it, and the action that does it when there is a page for that. A cause that is an empty server parameter is
    /// said in that one sentence and its button alone: the stages it blocks are not listed (R-63).</summary>
    internal sealed record CauseGroup(
        string Reason, List<string> Stages, string? Hint, Remediation? Remediation, bool IsEmptyParameter = false);

    /// <summary>An action that resolves a cause, rather than a message telling the user to go find it. An
    /// <c>Href</c> that is null only closes the dialog, to give the choice back to the launch dialog.</summary>
    internal sealed record Remediation(string? Explanation, string ActionLabel, string ActionIcon, string? Href);

    /// <summary>
    /// One cause per distinct target, whatever the number of stages that name it. A parameter left empty
    /// (no production server chosen) is one cause of its own: the stages are blocked because nothing was
    /// chosen, not because an agent by that name is missing.
    /// </summary>
    private List<CauseGroup> BuildGroups() =>
        [.. Unresolved
            .GroupBy(CauseKey, StringComparer.Ordinal)
            .Select(group => group.First().EmptyTargetParameter is not null
                ? new CauseGroup(CauseText(group.First()), [], null, ChooseServer(group), IsEmptyParameter: true)
                : new CauseGroup(CauseText(group.First()), [.. group.Select(Label)], null, RemediationFor(group.First())))];

    /// <summary>R-63: the server is chosen where it is defined. A stage of a pipeline this one triggers (the deployment
    /// chained from a candidate) takes it from the project's production environment, so the button opens the
    /// environments; a stage of the pipeline being launched takes it from the launch dialog, which this one gives back.</summary>
    private Remediation ChooseServer(IEnumerable<PreflightStageDto> stages) => new(
        null, L["PreflightChooseServer"].Value, "dns",
        stages.Any(stage => !string.IsNullOrEmpty(stage.PipelineName)) ? "environments" : null);

    private static string CauseKey(PreflightStageDto stage) =>
        stage.EmptyTargetParameter is { } empty ? $"parameter:{empty.Name}"
        : stage.Target.Length > 0 ? $"{stage.TargetKind}:{stage.Target}"
        : stage.TargetKind == PreflightTargetKind.Agent ? "agent:any"
        : $"reason:{stage.Reason}";

    /// <summary>The cause in French, from what the stage targets rather than from the backend's English
    /// reason, which stays the fallback for a stage with no target to name.</summary>
    private string CauseText(PreflightStageDto stage)
    {
        if (stage.EmptyTargetParameter is { } empty)
            return string.Format(L["PreflightEmptyParameterCause"], ParameterText.Pick(empty.Label, empty.LabelFr));
        // A stage that names no agent can run on any online agent: none is online.
        if (stage.Target.Length == 0 && stage.TargetKind == PreflightTargetKind.Agent)
            return L["PreflightCauseAnyAgent"];
        if (stage.Target.Length == 0)
            return Shorten(stage.Reason ?? L["PreflightUnknownReason"].Value);
        return string.Format(L[stage.TargetKind switch
        {
            PreflightTargetKind.Environment => "PreflightCauseEnvironment",
            PreflightTargetKind.Pool => "PreflightCausePool",
            _ => "PreflightCauseAgent"
        }], stage.Target);
    }

    /// <summary>
    /// The badge for one blocked stage. The preview now follows the chain, so a stage named "Deploy"
    /// may belong to the pipeline being launched or to any pipeline it triggers: the badge has to say
    /// which, or the user reads a blocked stage that does not exist in the pipeline in front of them.
    /// </summary>
    private static string Label(PreflightStageDto stage) =>
        stage.PipelineName is { Length: > 0 } pipeline ? $"{pipeline} / {stage.StageName}" : stage.StageName;

    /// <summary>
    /// The backend repeats the stage name inside its own reason ("Stage 'Migrate': no online pipeline
    /// runner..."), which is redundant once the stages are listed as badges. Drop that prefix so equal
    /// causes actually group together instead of differing only by stage name.
    /// </summary>
    private static string Shorten(string reason)
    {
        var marker = "': ";
        var at = reason.IndexOf(marker, StringComparison.Ordinal);
        return at >= 0 && reason.StartsWith("Stage '", StringComparison.Ordinal)
            ? reason[(at + marker.Length)..]
            : reason;
    }

    /// <summary>
    /// Maps a cause to the page that fixes it. An unresolved environment target is nearly always a
    /// server that is not attached to that environment (deleting and re-adding an agent empties the
    /// binding), so the environment page is where the fix happens; an agent target points at the
    /// server itself.
    /// </summary>
    private Remediation? RemediationFor(PreflightStageDto stage) => stage.EmptyTargetParameter is not null ? null : stage.TargetKind switch
    {
        PreflightTargetKind.Environment => new Remediation(
            L["PreflightFixEnvironmentHint"].Value,
            L["PreflightFixEnvironmentAction"].Value,
            "layers",
            "environments"),
        PreflightTargetKind.Agent or PreflightTargetKind.Pool => new Remediation(
            L["PreflightFixServerHint"].Value,
            L["PreflightFixServerAction"].Value,
            "dns",
            "servers"),
        _ => null,
    };

    private Task GoAsync(string? href)
    {
        Dialog.Close(false);
        if (href is not null) Nav.NavigateTo(href);
        return Task.CompletedTask;
    }
}
