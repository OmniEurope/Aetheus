// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// What the wizard says, on its Capabilities step, about the pipelines that would fail at their first run
/// with the templates' default commands (R-10 to R-13). It reads <see cref="PipelineSetupDetectionDto"/>,
/// the answers the readiness check drew from the branch files and the fleet: an answer that is not known
/// (null) produces no sentence, so nothing is claimed about what was not read.
/// </summary>
internal static class PipelineSetupDetectionText
{
    /// <summary>The templates whose default commands the detection looks at, asked for in one readiness
    /// check when the Capabilities step opens.</summary>
    public static readonly string[] DetectedTemplates =
        ["application-ci", "application-quality", "application-qa", PipelineSetupRoles.DeployProdTemplate];

    private const string CiTemplate = "application-ci";
    private const string QualityTemplate = "application-quality";
    private const string QaTemplate = "application-qa";
    private const string CiCommands = "APPLICATION_CI_COMMANDS";
    private const string QualityCommands = "APPLICATION_QUALITY_COMMANDS";

    /// <summary>One sentence per default command that is selected and cannot work, in the order of the options.</summary>
    public static IReadOnlyList<string> Warnings(
        IStringLocalizer<AppStrings> localizer,
        PipelineSetupDetectionDto? detection,
        string? branch,
        IReadOnlyCollection<string> selectedTemplates)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        ArgumentNullException.ThrowIfNull(selectedTemplates);
        if (detection is null) return [];

        var warnings = new List<string>();
        var branchName = branch ?? string.Empty;
        if (detection.HasDotNetSolution == false)
        {
            var variables = new List<string>();
            if (selectedTemplates.Contains(CiTemplate)) variables.Add(CiCommands);
            if (selectedTemplates.Contains(QualityTemplate)) variables.Add(QualityCommands);
            if (variables.Count > 0)
                warnings.Add(string.Format(localizer["PipelineSetupWarnNoSolution"].Value, branchName, string.Join(", ", variables)));
        }

        if (detection.HasDotNetTestProject == false && selectedTemplates.Contains(QaTemplate))
            warnings.Add(string.Format(localizer["PipelineSetupWarnNoTests"].Value, branchName));

        if (selectedTemplates.Contains(PipelineSetupRoles.DeployProdTemplate))
        {
            if (detection.HasComposeFile == false)
                warnings.Add(string.Format(localizer["PipelineSetupWarnNoCompose"].Value, branchName));
            if (detection.HasDockerRunner == false)
                warnings.Add(localizer["PipelineSetupWarnNoDocker"].Value);
        }

        return warnings;
    }

    /// <summary>Why QA is unticked by default, on the QA option; null when tests were found or not looked for.</summary>
    public static string? QaNoTestsTip(IStringLocalizer<AppStrings> localizer, PipelineSetupDetectionDto? detection, string? branch)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        return detection?.HasDotNetTestProject == false
            ? string.Format(localizer["PipelineSetupQaNoTestsTip"].Value, branch ?? string.Empty)
            : null;
    }
}
