// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>run-affected-dotnet-format</c> function, port of
/// <c>deploy/scripts/run-affected-dotnet-format.sh</c> (the script no longer exists): checks formatting
/// only, running <c>dotnet format whitespace</c> then <c>dotnet format style</c> with
/// <c>--verify-no-changes</c> on the files changed since the delivery baseline
/// (<see cref="AffectedFormatScope"/>, whole solution whenever the narrowing is not provably safe). The
/// analyzers pass of a bare <c>dotnet format</c> is not run: it is most of the time and duplicates the
/// Roslyn security report. Each run writes its JSON report under <c>.analysis-format/whitespace</c> or
/// <c>.analysis-format/style</c>; they are merged into one <c>.analysis-format/format-report.json</c> and
/// a SARIF log beside it, and <c>DOTNET_FORMAT_MODE</c> (<c>full</c>, <c>affected</c> or
/// <c>affected-empty</c>) and <c>DOTNET_FORMAT_GATE_STATUS</c> (0 no finding in either run, 1 findings)
/// are published. For each run, an exit code and a report that disagree, or a missing report, fail the
/// step.
///
/// Differences, deliberate:
/// <list type="bullet">
/// <item>The solution is the required <c>solution</c> parameter; the script hard-coded
/// <c>Aetheus.slnx</c>, the only thing in it specific to this repository.</item>
/// <item>The script's environment inputs become parameters that default to the same variables:
/// <c>mode</c> (<c>AETHEUS_FORMAT_MODE</c>, default <c>affected</c>), <c>baseline</c>
/// (<c>DELIVERY_BASELINE_SOURCE_SHA</c>), <c>head</c> (<c>BUILD_SOURCEVERSION</c>) and <c>dotnet</c>
/// (<c>DOTNET</c>, default <c>dotnet</c>); Git is still <c>GIT</c>, default <c>git</c>.</item>
/// <item>The JSON-to-SARIF conversion is in-process (<see cref="DotnetFormatSarif"/>), so Node.js is no
/// longer needed; the count it returns is an integer, so the script's count check has no
/// equivalent.</item>
/// <item>Git and dotnet run in the workspace, where the script used its working directory (the
/// workspace in every pipeline calling it).</item>
/// <item>A change to <c>Directory.Build.props</c> or <c>.targets</c> checks the whole solution; the
/// script's pattern missed them (see <see cref="AffectedFormatScope"/>).</item>
/// <item>A failed function publishes nothing, where the script had already published
/// <c>DOTNET_FORMAT_MODE</c> when the format run then failed.</item>
/// <item><c>restore: false</c> skips the <c>dotnet restore</c> the script always ran, for a check that
/// runs after the compilation of the same workspace: that compilation restored the solution in its own
/// configuration, and a second restore in the default one would rewrite every <c>obj/project.assets.json</c>
/// under the steps running beside it.</item>
/// </list>
/// </summary>
public sealed class RunAffectedDotnetFormatFunction : IPipelineFunction
{
    internal const string ReportDirectoryName = ".analysis-format";

    /// <summary>The formatting checks, in run order. <c>dotnet format</c> without a subcommand adds an
    /// analyzers pass (about three quarters of its time) that the Roslyn security report already covers.</summary>
    private static readonly string[] Subcommands = ["whitespace", "style"];

    private readonly IPipelineFunctionTools _tools;

    public RunAffectedDotnetFormatFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal RunAffectedDotnetFormatFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.RunAffectedDotnetFormat.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var mode = FunctionShell.Input(context, "mode", "AETHEUS_FORMAT_MODE") ?? "affected";
        if (mode is not ("affected" or "full"))
            return PipelineFunctionResult.Failure("AETHEUS_FORMAT_MODE must be affected or full.");
        var solution = context.Parameters.GetValueOrDefault("solution");
        if (string.IsNullOrWhiteSpace(solution))
            return PipelineFunctionResult.Failure("The solution parameter is required.");
        var restore = context.Parameters.GetValueOrDefault("restore") ?? "true";
        if (restore is not ("true" or "false"))
            return PipelineFunctionResult.Failure("The restore parameter must be true or false.");

        var run = new FormatRun(context, _tools, solution, FunctionShell.Tool(context, FunctionShell.Input(context, "dotnet", "DOTNET") ?? "dotnet"), restore == "true");
        run.ResetReportDirectory();
        var scope = await new AffectedFormatScope(_tools, context, FunctionShell.Tool(context, FunctionShell.Variable(context, "GIT") ?? "git"), context.Workspace)
            .DecideAsync(mode == "affected", FunctionShell.Input(context, "baseline", "DELIVERY_BASELINE_SOURCE_SHA"), FunctionShell.Input(context, "head", "BUILD_SOURCEVERSION"), cancellationToken)
            .ConfigureAwait(false);
        return await run.ExecuteAsync(scope, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One format run: restore unless an earlier compilation did, then the check over the decided
    /// scope, then its evidence.</summary>
    private sealed class FormatRun(PipelineFunctionContext context, IPipelineFunctionTools tools, string solution, string dotnet, bool restore)
    {
        private readonly string _workspace = context.Workspace!;
        private string ReportDirectory => Path.Combine(_workspace, ReportDirectoryName);
        private string ReportJson => Path.Combine(ReportDirectory, "format-report.json");
        private string ReportSarif => Path.Combine(ReportDirectory, "dotnet-format.sarif");

        public void ResetReportDirectory()
        {
            if (Directory.Exists(ReportDirectory))
                Directory.Delete(ReportDirectory, recursive: true);
            Directory.CreateDirectory(ReportDirectory);
        }

        public async Task<PipelineFunctionResult> ExecuteAsync(FormatScope scope, CancellationToken cancellationToken)
        {
            var restoreFailure = await RestoreAsync(cancellationToken).ConfigureAwait(false);
            if (restoreFailure is not null)
                return restoreFailure;
            if (scope.Full)
            {
                await context.Log("Formatting mode: full (fail-closed fallback or explicit nightly mode).").ConfigureAwait(false);
                context.SetOutput("DOTNET_FORMAT_MODE", "full");
                return await FormatAsync([], cancellationToken).ConfigureAwait(false);
            }
            if (scope.Files.Count == 0)
                return await PublishNothingChangedAsync(cancellationToken).ConfigureAwait(false);

            await context.Log($"Formatting mode: affected ({scope.Files.Count} files).").ConfigureAwait(false);
            context.SetOutput("DOTNET_FORMAT_MODE", "affected");
            return await FormatAsync(["--include", .. scope.Files], cancellationToken).ConfigureAwait(false);
        }

        private async Task<PipelineFunctionResult?> RestoreAsync(CancellationToken cancellationToken)
        {
            if (!restore)
            {
                await context.Log($"Restore skipped (restore: false): {solution} was restored by the compilation of this workspace.").ConfigureAwait(false);
                return null;
            }
            var exitCode = await RunAsync(["restore", solution, "--verbosity", "minimal"], cancellationToken).ConfigureAwait(false);
            return exitCode == 0
                ? null
                : PipelineFunctionResult.Failure($"dotnet restore {solution} failed with exit code {exitCode}.");
        }

        private async Task<PipelineFunctionResult> PublishNothingChangedAsync(CancellationToken cancellationToken)
        {
            await context.Log("Formatting mode: affected; no changed C# or Razor file.").ConfigureAwait(false);
            context.SetOutput("DOTNET_FORMAT_MODE", "affected-empty");
            await File.WriteAllTextAsync(ReportJson, "[]\n", cancellationToken).ConfigureAwait(false);
            var (count, failure) = await ConvertAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null)
                return failure;
            if (count != 0 || !HasContent(ReportSarif))
                return PipelineFunctionResult.Failure("The empty dotnet format report did not convert to an empty SARIF log.");
            context.SetOutput("DOTNET_FORMAT_GATE_STATUS", "0");
            return PipelineFunctionResult.Success();
        }

        private async Task<PipelineFunctionResult> FormatAsync(string[] include, CancellationToken cancellationToken)
        {
            var reports = new List<JsonNode>();
            var total = 0;
            foreach (var subcommand in Subcommands)
            {
                var reportDirectory = Path.Combine(ReportDirectory, subcommand);
                Directory.CreateDirectory(reportDirectory);
                string[] arguments =
                    ["format", subcommand, solution, "--no-restore", "--verify-no-changes", "--verbosity", "minimal", .. include, "--report", reportDirectory];
                var exitCode = await RunAsync(arguments, cancellationToken).ConfigureAwait(false);
                var report = Path.Combine(reportDirectory, "format-report.json");
                if (!HasContent(report))
                    return PipelineFunctionResult.Failure("dotnet format did not produce its required JSON evidence.");
                int runCount;
                try
                {
                    var text = await File.ReadAllTextAsync(report, cancellationToken).ConfigureAwait(false);
                    runCount = DotnetFormatSarif.Convert(text, _workspace).Count;
                    reports.Add(JsonNode.Parse(text)!);
                }
                catch (InvalidDataException ex)
                {
                    return PipelineFunctionResult.Failure(ex.Message);
                }
                if (!(exitCode == 0 && runCount == 0) && !(exitCode != 0 && runCount > 0))
                    return PipelineFunctionResult.Failure("dotnet format exit code and evidence are inconsistent.");
                total += runCount;
            }

            var merged = new JsonArray();
            foreach (var report in reports)
                foreach (var entry in report.AsArray())
                    merged.Add(entry!.DeepClone());
            await File.WriteAllTextAsync(ReportJson, merged.ToJsonString(), cancellationToken).ConfigureAwait(false);
            var (mergedCount, failure) = await ConvertAsync(cancellationToken).ConfigureAwait(false);
            if (failure is not null)
                return failure;
            if (mergedCount != total)
                return PipelineFunctionResult.Failure("dotnet format exit code and evidence are inconsistent.");
            var status = total > 0 ? 1 : 0;
            if (!HasContent(ReportSarif))
                return PipelineFunctionResult.Failure("The dotnet format SARIF evidence is missing.");
            context.SetOutput("DOTNET_FORMAT_GATE_STATUS", status.ToString(System.Globalization.CultureInfo.InvariantCulture));
            await context.Log($"dotnet format findings status: {status} ({total} finding(s)); SARIF evidence recorded.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        private async Task<(int Count, PipelineFunctionResult? Failure)> ConvertAsync(CancellationToken cancellationToken)
        {
            try
            {
                var report = await File.ReadAllTextAsync(ReportJson, cancellationToken).ConfigureAwait(false);
                var (sarif, count) = DotnetFormatSarif.Convert(report, _workspace);
                await File.WriteAllTextAsync(ReportSarif, sarif, cancellationToken).ConfigureAwait(false);
                return (count, null);
            }
            catch (InvalidDataException ex)
            {
                return (0, PipelineFunctionResult.Failure(ex.Message));
            }
        }

        private async Task<int> RunAsync(string[] arguments, CancellationToken cancellationToken)
        {
            ProcessStartInfo startInfo = FunctionShell.StartInfo(context, dotnet, _workspace, arguments);
            var result = await tools.RunAsync(startInfo, (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
            return result.TimedOut ? -1 : result.ExitCode;
        }

        private static bool HasContent(string path) => File.Exists(path) && new FileInfo(path).Length > 0;
    }
}
