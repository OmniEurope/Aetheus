// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>run-qa-e2e-suite</c> function, port of the immutable-image path of
/// <c>deploy/scripts/run-qa-e2e-suite.sh</c>: the browser suite runs from an image whose
/// <c>org.opencontainers.image.revision</c> label must be the expected commit, on the QA stack's
/// network (the front container's network that is not <c>*_backend</c>), once a readiness container has
/// reached the front. The suite container runs <c>dotnet test</c> inside the image with the script's
/// environment and command line; its results are copied out after it stopped, the raw exit code it
/// recorded and the TRX are classified by <see cref="DotnetTestResultClassifier"/>, the skipped tests
/// are counted and named, and the failed names recorded. Status 1 is published as
/// <c>E2E_SUITE_STATUS</c> where the script exited 10; anything that leaves no gradable evidence fails
/// the step. Both containers are removed on every path once the results directory exists.
///
/// Difference, deliberate: the script's fallback that built an E2E image from the source tree when no
/// image was given is not ported; the image and the expected commit are required parameters (every
/// QA call passed them), and the suite's project inside the image is the <c>test_project</c>
/// parameter instead of <c>tests/Aetheus.E2E/Aetheus.E2E.csproj</c>.
/// </summary>
public sealed partial class RunQaE2eSuiteFunction : IPipelineFunction
{
    /// <summary>The suite container's script, verbatim from the script's <c>sh -c</c>. A raw string keeps the
    /// source file's line endings, CRLF in a Windows checkout, and <c>sh</c> reads the carriage return after
    /// <c>set -u</c> as an illegal option (QA run 1278, agent built from a Windows tree): the text is
    /// normalized to LF.</summary>
    internal static readonly string RunnerScript = """
        set -u
            RESULTS=/tmp/aetheus-e2e-results
            rm -rf "$RESULTS"
            mkdir -p "$RESULTS"
            TRX="$RESULTS/e2e.trx"
            RAW_EXIT=0
            if [ -n "$E2E_TEST_FILTER" ]; then set -- --filter "$E2E_TEST_FILTER"; else set --; fi
            dotnet test "$E2E_TEST_PROJECT" -c Release --no-build --no-restore "$@" \
              --logger "console;verbosity=minimal" --logger "trx;LogFileName=e2e.trx" \
              --results-directory "$RESULTS" --nologo \
              -- NUnit.NumberOfTestWorkers="$E2E_WORKERS" || RAW_EXIT=$?
            printf "%s\n" "$RAW_EXIT" > "$RESULTS/raw-exit-code"
        """.ReplaceLineEndings("\n");

    private readonly IPipelineFunctionTools _tools;

    public RunQaE2eSuiteFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal RunQaE2eSuiteFunction(IPipelineFunctionTools tools) => _tools = tools;

    [GeneratedRegex("notExecuted=\"([0-9]*)\"")]
    private static partial Regex NotExecutedCounter();

    [GeneratedRegex("<UnitTestResult [^>]*outcome=\"NotExecuted\"[^>]*>")]
    private static partial Regex SkippedResult();

    [GeneratedRegex("testName=\"[^\"]*\"")]
    private static partial Regex TestNameAttribute();

    public string Name => PipelineFunctionCatalog.RunQaE2eSuite.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var run = E2eRun.From(context);
        if (run.Refusal() is { } refusal)
            return PipelineFunctionResult.Failure(refusal);
        var interval = FunctionProgress.Interval(context);
        if (interval is null)
            return PipelineFunctionResult.Failure($"{FunctionProgress.IntervalVariable} must be a positive integer.");
        await LogSettingsAsync(context, run).ConfigureAwait(false);
        if (!File.Exists(run.PasswordFile) || new FileInfo(run.PasswordFile).Length == 0)
            return PipelineFunctionResult.Failure($"The admin password file is missing or empty: {run.PasswordFile}");

        var docker = new FunctionDocker(_tools, context);
        if (await ImageRefusalAsync(docker, run, cancellationToken).ConfigureAwait(false) is { } imageRefusal)
            return PipelineFunctionResult.Failure(imageRefusal);

        if (Directory.Exists(run.Results))
            Directory.Delete(run.Results, recursive: true);
        Directory.CreateDirectory(run.Results);
        var execution = new E2eExecution(_tools, context, docker, run, interval.Value);
        int status;
        try
        {
            var outcome = await execution.RunAsync(cancellationToken).ConfigureAwait(false);
            if (outcome.Error is not null)
                return PipelineFunctionResult.Failure(outcome.Error);
            status = outcome.Status;
        }
        finally
        {
            await docker.QuietAsync(CancellationToken.None, ["rm", "-f", run.ReadyContainer, run.RunnerContainer], keepErrors: false)
                .ConfigureAwait(false);
        }
        return await ReportAsync(context, run, status, cancellationToken).ConfigureAwait(false);
    }

    private static async Task LogSettingsAsync(PipelineFunctionContext context, E2eRun run)
    {
        await context.Log($"E2E suite {run.Label} runs with {run.Workers} NUnit worker(s).").ConfigureAwait(false);
        await context.Log($"E2E suite {run.Label} targets a payload-only V-1: {run.PreviousPayloadOnly}.").ConfigureAwait(false);
        await context.Log($"E2E suite {run.Label} test filter: {(run.TestFilter.Length == 0 ? "none" : run.TestFilter)}.").ConfigureAwait(false);
    }

    private static async Task<string?> ImageRefusalAsync(FunctionDocker docker, E2eRun run, CancellationToken cancellationToken)
    {
        var labels = await docker.LinesAsync(cancellationToken,
            "image", "inspect", "--format", "{{index .Config.Labels \"org.opencontainers.image.revision\"}}", run.Image).ConfigureAwait(false);
        if (labels is null)
            return $"docker image inspect {run.Image} failed.";
        var actual = labels.Count > 0 ? labels[0] : string.Empty;
        return actual == run.ExpectedCommit ? null : $"Immutable E2E image revision {actual} does not match {run.ExpectedCommit}.";
    }

    /// <summary>The script's closing lines: the skipped tests counted and named (a skip is NotExecuted
    /// in the TRX, never Passed), the failed names recorded, the status published.</summary>
    private static async Task<PipelineFunctionResult> ReportAsync(PipelineFunctionContext context, E2eRun run, int status, CancellationToken cancellationToken)
    {
        var trx = await File.ReadAllTextAsync(run.Trx, cancellationToken).ConfigureAwait(false);
        var skippedMatch = NotExecutedCounter().Match(trx);
        var skipped = skippedMatch.Success && skippedMatch.Groups[1].Value.Length > 0 ? skippedMatch.Groups[1].Value : "0";
        await context.Log($"E2E suite {run.Label} produced an executed-test proof against {run.Back} with findings status {status}, {skipped} test(s) skipped.")
            .ConfigureAwait(false);
        foreach (Match result in SkippedResult().Matches(trx))
            foreach (Match name in TestNameAttribute().Matches(result.Value))
                await context.Log("  skipped: " + name.Value).ConfigureAwait(false);
        if (status != 0)
            await FailedTestNames.RecordAsync(context, run.Trx, Path.Combine(run.Results, "failed-tests.txt"), cancellationToken).ConfigureAwait(false);
        context.SetOutput("E2E_SUITE_STATUS", status.ToString(CultureInfo.InvariantCulture));
        return PipelineFunctionResult.Success();
    }
}
