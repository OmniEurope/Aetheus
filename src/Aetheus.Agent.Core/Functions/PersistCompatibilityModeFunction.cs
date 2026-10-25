// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>persist-compatibility-mode</c> function, port of
/// the former <c>persist-compatibility-mode.sh</c> script: publishes the compatibility attestation of a
/// QA run, which the run page shows and the assurance seal grades, under one owner.
/// <list type="bullet">
/// <item>Each of the five gate statuses is read from the workspace's <c>.qa-&lt;gate&gt;-gate-status</c>
/// file when it is not empty, else from its <c>QA_&lt;GATE&gt;_GATE_STATUS</c> variable, else 1 (no
/// evidence is a failure), and published as 0 when it reads exactly 0, else 1.</item>
/// <item><c>COMPATIBILITY_MODE</c> is <c>Bootstrap</c> when <c>.qa-bootstrap-mode</c> reads 1, with
/// <c>PREVIOUS_VERSION_TESTED=false</c> and a verdict on the current version alone
/// (<c>CurrentVersionVerified</c> when both current gates are 0, else <c>CurrentVersionDegraded</c>).</item>
/// <item>Otherwise it is <c>NMinusOne</c>, <c>PREVIOUS_VERSION_TESTED</c> is the content of
/// <c>.qa-previous-commit</c>, which must not be empty, and the verdict is <c>NMinusOneVerified</c> only
/// when the current version is verified and the three V-1 gates are 0, else
/// <c>NMinusOneDegraded</c>.</item>
/// </list>
/// Files are read as <c>$(cat file)</c> reads them: trailing newlines removed, nothing else.
///
/// Differences, deliberate: a failed function publishes nothing, where the script had already printed
/// the five statuses when it failed on the V-1 commit; that failure names its cause, where the script
/// left it to <c>cat</c> or to a silent <c>test -n</c>. A V-1 commit on more than one line is refused,
/// where the script printed a broken variable line. The three attestation outputs are named in upper
/// snake case like every catalogue output; the script published them as <c>CompatibilityMode</c>,
/// <c>PreviousVersionTested</c> and <c>CompatibilityVerdict</c>, which the run page and the seal still
/// read as a fallback.
/// </summary>
public sealed class PersistCompatibilityModeFunction : IPipelineFunction
{
    internal const string BootstrapMarker = ".qa-bootstrap-mode";
    internal const string PreviousCommitFile = ".qa-previous-commit";

    public string Name => PipelineFunctionCatalog.PersistCompatibilityMode.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var workspace = context.Workspace;

        var currentIntegration = await StatusAsync(context, workspace, "current-integration", cancellationToken).ConfigureAwait(false);
        var currentE2E = await StatusAsync(context, workspace, "current-e2e", cancellationToken).ConfigureAwait(false);
        var previousSmoke = await StatusAsync(context, workspace, "previous-smoke", cancellationToken).ConfigureAwait(false);
        var previousIntegration = await StatusAsync(context, workspace, "previous-integration", cancellationToken).ConfigureAwait(false);
        var previousE2E = await StatusAsync(context, workspace, "previous-e2e", cancellationToken).ConfigureAwait(false);

        var currentVerdict = currentIntegration == "0" && currentE2E == "0" ? "CurrentVersionVerified" : "CurrentVersionDegraded";
        string mode, previousTested, verdict;
        var bootstrapPath = Path.Combine(workspace, BootstrapMarker);
        if (!File.Exists(bootstrapPath))
            await context.Log($"{bootstrapPath} is missing; the run is read as not bootstrap.", TaskLogLevel.Warning).ConfigureAwait(false);
        if (await ReadAsync(bootstrapPath, cancellationToken).ConfigureAwait(false) == "1")
        {
            (mode, previousTested, verdict) = ("Bootstrap", "false", currentVerdict);
        }
        else
        {
            var (previous, refusal) = await PreviousCommitAsync(workspace, cancellationToken).ConfigureAwait(false);
            if (refusal is not null)
                return PipelineFunctionResult.Failure(refusal);
            // One verdict, one meaning: the V-1 suites and the smoke all ran and passed.
            var historicalPassed = previousSmoke == "0" && previousIntegration == "0" && previousE2E == "0";
            (mode, previousTested, verdict) = ("NMinusOne", previous,
                currentVerdict == "CurrentVersionVerified" && historicalPassed ? "NMinusOneVerified" : "NMinusOneDegraded");
        }

        context.SetOutput("QA_CURRENT_INTEGRATION_GATE_STATUS", currentIntegration);
        context.SetOutput("QA_CURRENT_E2E_GATE_STATUS", currentE2E);
        context.SetOutput("QA_PREVIOUS_SMOKE_GATE_STATUS", previousSmoke);
        context.SetOutput("QA_PREVIOUS_INTEGRATION_GATE_STATUS", previousIntegration);
        context.SetOutput("QA_PREVIOUS_E2E_GATE_STATUS", previousE2E);
        context.SetOutput("COMPATIBILITY_MODE", mode);
        context.SetOutput("PREVIOUS_VERSION_TESTED", previousTested);
        context.SetOutput("COMPATIBILITY_VERDICT", verdict);
        await context.Log($"Compatibility attestation: {mode}, V-1 tested {previousTested}, verdict {verdict}.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The script's <c>normalize_status "$(read_status file "${VAR:-1}")"</c>: the file when it is
    /// not empty, else the variable, else 1; then 0 only for exactly 0.</summary>
    private static async Task<string> StatusAsync(
        PipelineFunctionContext context, string workspace, string gate, CancellationToken cancellationToken)
    {
        var file = Path.Combine(workspace, $".qa-{gate}-gate-status");
        var raw = File.Exists(file) && new FileInfo(file).Length > 0
            ? await ReadAsync(file, cancellationToken).ConfigureAwait(false)
            : FunctionShell.Variable(context, $"QA_{gate.Replace('-', '_').ToUpperInvariant()}_GATE_STATUS") ?? "1";
        return raw == "0" ? "0" : "1";
    }

    /// <summary>The V-1 commit named by <c>.qa-previous-commit</c>, or why it cannot be read: the file is
    /// missing, empty, or holds more than one line.</summary>
    private static async Task<(string Previous, string? Refusal)> PreviousCommitAsync(
        string workspace, CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspace, PreviousCommitFile);
        if (!File.Exists(path))
            return (string.Empty, $"{path} is missing: the V-1 commit this run tested is unknown.");
        var previous = await ReadAsync(path, cancellationToken).ConfigureAwait(false) ?? string.Empty;
        if (previous.Length == 0)
            return (previous, $"{path} is empty: the V-1 commit this run tested is unknown.");
        if (previous.Contains('\n', StringComparison.Ordinal) || previous.Contains('\r', StringComparison.Ordinal))
            return (previous, $"{path} holds more than one line; it must name one V-1 commit.");
        return (previous, null);
    }

    /// <summary><c>$(cat file)</c>: the content with its trailing newlines removed, null when the file
    /// cannot be read.</summary>
    private static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken) =>
        File.Exists(path)
            ? (await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false)).TrimEnd('\n')
            : null;
}
