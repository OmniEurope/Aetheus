// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>authorize-baseline-bootstrap</c> function, port of
/// <c>deploy/scripts/candidate/authorize-baseline-bootstrap.sh</c>: a candidate that has no
/// rollback-capable deployed baseline (<c>DELIVERY_BASELINE_BOOTSTRAP</c> true, the value
/// <c>export-baseline-identity</c> publishes) is refused unless the bootstrap is explicitly allowed,
/// before any expensive qualification runs. Same test and same messages as the script.
///
/// Differences, deliberate: <c>DELIVERY_BASELINE_BOOTSTRAP</c> and <c>ALLOW_BOOTSTRAP</c> are the
/// <c>bootstrap</c> and <c>allow_bootstrap</c> parameters, defaulting to the same variables (an unset
/// or empty bootstrap reads as true, like the script's <c>${DELIVERY_BASELINE_BOOTSTRAP:-true}</c>).
/// When a bootstrap is needed and <c>ALLOW_BOOTSTRAP</c> is unset or empty, the refusal names that
/// cause, where the script's <c>set -u</c> stopped on an unbound variable (unset) or refused with the
/// ordinary message (empty).
/// </summary>
public sealed class AuthorizeBaselineBootstrapFunction : IPipelineFunction
{
    public string Name => PipelineFunctionCatalog.AuthorizeBaselineBootstrap.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var bootstrap = FunctionShell.Input(context, "bootstrap", "DELIVERY_BASELINE_BOOTSTRAP") ?? "true";
        if (bootstrap == "true")
        {
            var allow = FunctionShell.Input(context, "allow_bootstrap", "ALLOW_BOOTSTRAP");
            if (allow is null)
                return PipelineFunctionResult.Failure(
                    "No rollback-capable deployed baseline exists and ALLOW_BOOTSTRAP is not set; refusing bootstrap before CI.");
            if (allow != "true")
                return PipelineFunctionResult.Failure(
                    "No rollback-capable deployed baseline exists and allowBootstrap is false; refusing bootstrap before CI.");
        }

        await context.Log("Baseline authorization accepted before CI.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }
}
