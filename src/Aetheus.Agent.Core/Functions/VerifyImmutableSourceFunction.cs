// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-immutable-source</c> function, port of
/// the former <c>verify-immutable-source.sh</c> script: <c>git rev-parse HEAD</c> must be the
/// requested revision and, in <c>full</c> mode, a shallow checkout is first deepened with
/// <c>git fetch --unshallow origin</c> and must then hold the complete history, so a secret scan cannot
/// report on history it never received. Same Git commands, in the same order, as the script.
///
/// Differences, deliberate: <c>BUILD_SOURCEVERSION</c> and <c>AETHEUS_GITLEAKS_MODE</c> are the
/// <c>head</c> and <c>mode</c> parameters, defaulting to the same variables (mode
/// <c>release-range</c>); like the script, any mode other than <c>full</c> only checks HEAD. Every
/// refusal names its cause, where the script's <c>test</c> failed with no message; Git is <c>GIT</c>,
/// default <c>git</c>, run in the workspace.
/// </summary>
public sealed class VerifyImmutableSourceFunction : IPipelineFunction
{
    private readonly IPipelineFunctionTools _tools;

    public VerifyImmutableSourceFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal VerifyImmutableSourceFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.VerifyImmutableSource.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var git = new Git(_tools, context, FunctionShell.Tool(context, FunctionShell.Variable(context, "GIT") ?? "git"));
        var expected = FunctionShell.Input(context, "head", "BUILD_SOURCEVERSION");
        if (expected is null)
            return PipelineFunctionResult.Failure("BUILD_SOURCEVERSION is not set: the revision to verify is unknown.");
        var head = await git.CaptureAsync(cancellationToken, "rev-parse", "HEAD").ConfigureAwait(false);
        if (!string.Equals(head, expected, StringComparison.Ordinal))
            return PipelineFunctionResult.Failure($"The checkout is at {head ?? "no readable HEAD"}, not at the requested revision {expected}.");

        var full = (FunctionShell.Input(context, "mode", "AETHEUS_GITLEAKS_MODE") ?? "release-range") == "full";
        if (!full)
        {
            await context.Log($"Checkout verified at {expected}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        if (await git.CaptureAsync(cancellationToken, "rev-parse", "--is-shallow-repository").ConfigureAwait(false) == "true")
        {
            await context.Log("Full mode on a shallow checkout: fetching the complete history from origin.").ConfigureAwait(false);
            var fetch = await git.RunAsync(cancellationToken, "fetch", "--unshallow", "origin").ConfigureAwait(false);
            if (fetch != 0)
                return PipelineFunctionResult.Failure($"git fetch --unshallow origin failed with exit code {fetch}.");
        }
        if (await git.CaptureAsync(cancellationToken, "rev-parse", "--is-shallow-repository").ConfigureAwait(false) != "false")
            return PipelineFunctionResult.Failure("Full mode requires the complete history, but the checkout is still shallow.");

        await context.Log($"Checkout verified at {expected} with its complete history.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>Git run in the workspace with the step's environment.</summary>
    private sealed class Git(IPipelineFunctionTools tools, PipelineFunctionContext context, string executable)
    {
        /// <summary><c>$(git args)</c>, null when Git failed.</summary>
        public Task<string?> CaptureAsync(CancellationToken cancellationToken, params string[] arguments) =>
            FunctionShell.CaptureAsync(tools, StartInfo(arguments), cancellationToken);

        /// <summary>A Git command whose output goes to the step log; its exit code.</summary>
        public async Task<int> RunAsync(CancellationToken cancellationToken, params string[] arguments)
        {
            var result = await tools.RunAsync(StartInfo(arguments), (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
            return result.TimedOut ? -1 : result.ExitCode;
        }

        private ProcessStartInfo StartInfo(string[] arguments) =>
            FunctionShell.StartInfo(context, executable, context.Workspace!, arguments);
    }
}
