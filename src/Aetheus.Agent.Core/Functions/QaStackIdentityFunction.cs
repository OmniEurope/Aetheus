// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>qa-stack-identity</c> function, port of <c>deploy/scripts/qa/qa-stack-identity.sh</c>, which
/// the QA steps sourced: the project slug (<see cref="DeployIdentityFunction.Slug"/>, the same
/// <c>tr | sed</c>), the PostgreSQL role (the slug with <c>-</c> as <c>_</c>, prefixed <c>p_</c> when it
/// does not start with a letter) and its <c>_e2e</c> database, and the Compose project
/// <c>&lt;slug&gt;-qa-rollback-&lt;run&gt;</c> the script's <c>qa_stack</c> printed.
///
/// Difference, deliberate: the run id must be digits. The steps that validated it before calling
/// <c>qa_stack</c> refused anything else with the same message; the others passed
/// <c>BUILD_BUILDID</c>, which is always digits.
/// </summary>
public sealed class QaStackIdentityFunction : IPipelineFunction
{
    public string Name => PipelineFunctionCatalog.QaStackIdentity.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = FunctionShell.Input(context, "run_id", "BUILD_BUILDID") ?? string.Empty;
        if (run.Length == 0 || !run.All(char.IsAsciiDigit))
            return PipelineFunctionResult.Failure("BUILD_BUILDID is invalid.");
        var app = DeployIdentityFunction.Slug(FunctionShell.Input(context, "project_name", "BUILD_PROJECTNAME") ?? string.Empty);
        if (app.Length == 0)
            return PipelineFunctionResult.Failure("BUILD_PROJECTNAME is missing or has no usable character; the QA stack cannot be named.");

        // PostgreSQL identifiers start with a letter; a project named "2048" still gets a valid role.
        var role = app.Replace('-', '_');
        var user = app[0] is >= 'a' and <= 'z' ? role : "p_" + role;
        var database = user + "_e2e";
        var stack = $"{app}-qa-rollback-{run}";

        await context.Log($"QA stack {stack}: database {database} owned by {user}.").ConfigureAwait(false);
        context.SetOutput("QA_APP", app);
        context.SetOutput("QA_DB_USER", user);
        context.SetOutput("QA_DB_NAME", database);
        context.SetOutput("QA_STACK", stack);
        return PipelineFunctionResult.Success();
    }
}
