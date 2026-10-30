// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The warning a run keeps when one of its steps failed before it could start (a system step the control
/// plane refused to dispatch): which kind of step, its name, and the reason. A stored problem, so the run
/// page says the sentence in the user's language; the reason is the engine's own English sentence and is
/// written as the detail of it. The English of each warning is the sentence earlier builds stored.
/// </summary>
internal static class PipelineStepFailureWarning
{
    public static string RestoreArtifacts(string step, string reason) =>
        Of(PipelineProblemCodes.StepFailedRestoreArtifacts, "Restore-artifacts", step, reason);

    public static string RestoreBackup(string step, string reason) =>
        Of(PipelineProblemCodes.StepFailedRestoreBackup, "Restore-backup", step, reason);

    public static string AdvanceBranch(string step, string reason) =>
        Of(PipelineProblemCodes.StepFailedAdvanceBranch, "Advance branch", step, reason);

    public static string Deploy(string step, string reason) =>
        Of(PipelineProblemCodes.StepFailedDeploy, "Deploy", step, reason);

    public static string Scanner(string step, string reason) =>
        Of(PipelineProblemCodes.StepFailedScanner, "Scanner", step, reason);

    public static string Release(string step, string? reason) =>
        Of(PipelineProblemCodes.StepFailedRelease, "Release", step, reason ?? string.Empty);

    public static string Trigger(string step, string reason) =>
        Of(PipelineProblemCodes.StepFailedTrigger, "Trigger", step, reason);

    /// <summary>The failure of a host operation step; <paramref name="operation"/> is the label the factory
    /// has always used (<c>Apache-proxy</c>, <c>Apache-config</c>, <c>Certbot</c>, <c>Smoke</c>,
    /// <c>Blue-green</c>).</summary>
    public static string HostOperation(string operation, string step, string reason) => operation switch
    {
        "Apache-proxy" => Of(PipelineProblemCodes.StepFailedApacheProxy, operation, step, reason),
        "Apache-config" => Of(PipelineProblemCodes.StepFailedApacheConfig, operation, step, reason),
        "Certbot" => Of(PipelineProblemCodes.StepFailedCertbot, operation, step, reason),
        "Smoke" => Of(PipelineProblemCodes.StepFailedSmoke, operation, step, reason),
        "Blue-green" => Of(PipelineProblemCodes.StepFailedBlueGreen, operation, step, reason),
        _ => $"{operation} step '{step}': {reason}"
    };

    /// <summary>The compose variables a blue-green step could not forward because the run does not define them.</summary>
    public static string ComposeEnvNotForwarded(string step, IReadOnlyCollection<string> variables)
    {
        var names = string.Join(", ", variables);
        return PipelineProblem.Of(
            PipelineProblemCodes.BlueGreenComposeEnvNotForwarded,
            $"Blue-green step '{step}': compose_env did not forward {names}, which this run does not define at this point.",
            ("step", step), ("variables", names)).ToStored();
    }

    private static string Of(string code, string label, string step, string reason) =>
        PipelineProblem.Of(code, $"{label} step '{step}': {reason}", ("step", step), ("detail", reason)).ToStored();
}
