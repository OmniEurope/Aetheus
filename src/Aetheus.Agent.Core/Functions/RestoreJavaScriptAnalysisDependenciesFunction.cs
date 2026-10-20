// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>restore-javascript-analysis-dependencies</c> function, port of
/// <c>deploy/scripts/restore-javascript-analysis-dependencies.sh</c>: restores the workspace's
/// JavaScript dependencies exactly as the committed root <c>package-lock.json</c> pins them, with the
/// pinned Node.js (<see cref="PinnedNodeRuntime"/>) first on <c>PATH</c>, lifecycle scripts disabled and
/// network retries bounded. A failed <c>npm ci</c> is retried twice, after 4 then 6 seconds.
///
/// Same failures as the script, with the same messages; the script told a missing npm or lock file
/// (exit 2) from a failed restore (exit 1) by its exit code, which a function failure does not carry.
/// </summary>
public sealed class RestoreJavaScriptAnalysisDependenciesFunction : IPipelineFunction
{
    internal const int MaxAttempts = 3;

    internal static readonly string[] NpmCiArguments =
    [
        "ci", "--ignore-scripts", "--no-audit", "--no-fund", "--prefer-offline",
        "--fetch-retries=2",
        "--fetch-retry-mintimeout=1000",
        "--fetch-retry-maxtimeout=5000",
        "--fetch-timeout=60000"
    ];

    private readonly IPipelineFunctionTools _tools;
    private readonly PinnedNodeRuntime _node;

    public RestoreJavaScriptAnalysisDependenciesFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal RestoreJavaScriptAnalysisDependenciesFunction(IPipelineFunctionTools tools, PinnedDownload? nodeArchive = null)
    {
        _tools = tools;
        _node = new PinnedNodeRuntime(tools, nodeArchive ?? PinnedNodeRuntime.OfficialArchive);
    }

    public string Name => PipelineFunctionCatalog.RestoreJavaScriptAnalysisDependencies.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");

        var node = await _node.ResolveAsync(context, cancellationToken).ConfigureAwait(false);
        if (node.Executable is null)
            return PipelineFunctionResult.Failure(node.Error!);
        var nodeDirectory = Path.GetDirectoryName(node.Executable)!;
        var npm = Path.Combine(nodeDirectory, "npm");
        if (!FunctionShell.IsExecutable(npm))
            return PipelineFunctionResult.Failure("The pinned Node.js runtime does not provide npm.");
        if (!File.Exists(Path.Combine(context.Workspace, "package-lock.json")))
            return PipelineFunctionResult.Failure("JavaScript analysis requires the committed root package-lock.json.");

        var startInfo = FunctionShell.StartInfo(context, npm, context.Workspace, NpmCiArguments);
        startInfo.Environment["PATH"] = nodeDirectory + Path.PathSeparator + FunctionShell.Variable(context, "PATH");
        return await RestoreAsync(context, startInfo, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PipelineFunctionResult> RestoreAsync(
        PipelineFunctionContext context, System.Diagnostics.ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var run = await _tools.RunAsync(startInfo, (line, level) => context.Log(line, level), cancellationToken)
                .ConfigureAwait(false);
            if (run.ExitCode == 0)
                return PipelineFunctionResult.Success();
            if (attempt >= MaxAttempts)
                return PipelineFunctionResult.Failure($"npm ci failed after {attempt} bounded attempts.");
            await context.Log($"npm ci attempt {attempt} failed; retrying after a bounded backoff.", TaskLogLevel.Warning)
                .ConfigureAwait(false);
            await _tools.DelayAsync(TimeSpan.FromSeconds((attempt + 1) * 2), cancellationToken).ConfigureAwait(false);
        }
    }
}
