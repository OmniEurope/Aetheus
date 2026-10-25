// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>publish-failed-tests</c> function, port of the former <c>publish-failed-tests.sh</c> script and
/// of the <c>publish</c> mode of <c>list-failed-tests.mjs</c> it ran: publishes, beside a gate status,
/// the names of the tests that made it non-zero, as <c>&lt;VARIABLE&gt;=A, B, +N more</c> with a
/// <c>Failed tests (N): ...</c> log line. The variable is named by the step (the catalog's
/// <see cref="PipelineFunctionDescriptor.NamedOutputParameter"/>) because the interface reads it next to
/// the status it explains (<c>UNIT_TEST_GATE_STATUS</c> and <c>UNIT_TEST_FAILED_TESTS</c>).
///
/// Naming the tests is a diagnostic, never the verdict: the script always exited 0, saying in the log
/// when the names could not be published, and so does the function: it never fails the step.
///
/// Differences, deliberate: the script was called only when the gate status was not 0
/// (<c>[ "$STATUS" = 0 ] || sh publish-failed-tests.sh ...</c>), a condition the step's shell held; the
/// <c>statuses</c> parameter carries it, since a function step runs beside the shell that computes the
/// aggregate status rather than inside it.
/// </summary>
public sealed class PublishFailedTestsFunction : IPipelineFunction
{
    public string Name => PipelineFunctionCatalog.PublishFailedTests.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        var variable = context.Parameters["variable"];

        if (AllClean(context.Parameters.GetValueOrDefault("statuses")))
        {
            await context.Log($"Every status is 0; no failed test names to publish for {variable}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        try
        {
            // The script's ^[A-Z][A-Z0-9_]*$: the run-variable shape, starting with a letter.
            if (!PipelineRunVariableName.IsValid(variable) || variable[0] is < 'A' or > 'Z')
                throw new ArgumentException($"Invalid variable name '{variable}'.");
            var names = FailedTestNames.Collect(Sources(context));
            if (names.Count > 0)
            {
                await context.Log(FailedTestNames.LogLine(names)).ConfigureAwait(false);
                context.SetOutput(variable, FailedTestNames.Format(names));
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await context.Log($"The failed test names for {variable} could not be published: {ex.Message}", TaskLogLevel.Warning)
                .ConfigureAwait(false);
        }
        return PipelineFunctionResult.Success();
    }

    /// <summary>True only when statuses were given and every one of them is exactly 0.</summary>
    internal static bool AllClean(string? statuses)
    {
        var values = (statuses ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return values.Length > 0 && values.All(value => value == "0");
    }

    private static IEnumerable<string> Sources(PipelineFunctionContext context) =>
        context.Parameters["sources"]
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(source => Path.Combine(context.Workspace ?? string.Empty, source));
}
