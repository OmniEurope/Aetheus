// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>prune-stale-qa-compose</c> function, port of <c>deploy/scripts/prune-stale-qa-compose.sh</c>:
/// lists the Compose projects that label a container, a network or a volume, keeps those named
/// <c>&lt;prefix&gt;-&lt;run&gt;</c> or <c>&lt;prefix&gt;-rollback-&lt;run&gt;</c>, protects the current
/// and newer runs and removes the containers, networks and volumes of every older one, then requires
/// none of them to remain. Same Docker commands, in the same order, and the same messages.
///
/// Differences, deliberate: the prefix the script fixed to <c>aetheus-qa</c> is the required
/// <c>project_prefix</c> parameter, and the script's first argument (else <c>BUILD_BUILDID</c>) is the
/// <c>run_id</c> parameter (else the same variable). A listing Docker fails is a failure, where the
/// script's command substitution read it as no project; a refused removal and a resource that survives
/// its removal are named, where the script's <c>set -e</c> and <c>test -z</c> stopped without a
/// message. Run ids are compared as integers of any length, where the shell's <c>-ge</c> rejected one
/// beyond 64 bits. Projects are sorted ordinally, as <c>sort -u</c> does under the C locale.
/// </summary>
public sealed partial class PruneStaleQaComposeFunction : IPipelineFunction
{
    private const string ProjectLabel = "label=com.docker.compose.project";
    private const string ProjectLabelFormat = "{{.Label \"com.docker.compose.project\"}}";

    [GeneratedRegex("^[a-z0-9][a-z0-9_.-]*\\z")]
    private static partial Regex ProjectPrefix();

    private readonly IPipelineFunctionTools _tools;

    public PruneStaleQaComposeFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal PruneStaleQaComposeFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.PruneStaleQaCompose.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var runId = FunctionShell.Input(context, "run_id", "BUILD_BUILDID") ?? string.Empty;
        if (runId.Length == 0 || !runId.All(char.IsAsciiDigit))
            return PipelineFunctionResult.Failure("Current QA run id is missing or invalid.");
        var prefix = context.Parameters.GetValueOrDefault("project_prefix") ?? string.Empty;
        if (!ProjectPrefix().IsMatch(prefix))
            return PipelineFunctionResult.Failure($"Not a Compose project prefix: '{prefix}'.");

        var docker = new FunctionDocker(_tools, context);
        var projects = await ListProjectsAsync(docker, prefix, cancellationToken).ConfigureAwait(false);
        if (projects is null)
            return PipelineFunctionResult.Failure("Docker could not list the Compose projects of the host.");

        var current = Parse(runId);
        foreach (var project in projects)
        {
            var projectRunId = project.StartsWith(prefix + "-rollback-", StringComparison.Ordinal)
                ? project[(prefix.Length + "-rollback-".Length)..]
                : project[(prefix.Length + 1)..];
            if (Parse(projectRunId) >= current)
            {
                await context.Log($"Protected current or newer QA Compose project '{project}'.").ConfigureAwait(false);
                continue;
            }

            await context.Log($"Removing stale QA Compose project '{project}' before storage measurement.").ConfigureAwait(false);
            var failure = await RemoveAsync(docker, project, cancellationToken).ConfigureAwait(false);
            if (failure is not null)
                return PipelineFunctionResult.Failure(failure);
        }

        await context.Log("Stale QA Compose resource maintenance completed.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    /// <summary>The per-run projects of that prefix labelling a container, a network or a volume,
    /// sorted and unique; null when a listing failed.</summary>
    private static async Task<IReadOnlyList<string>?> ListProjectsAsync(
        FunctionDocker docker, string prefix, CancellationToken cancellationToken)
    {
        var pattern = new Regex("^" + Regex.Escape(prefix) + "-(rollback-)?[0-9]+\\z", RegexOptions.CultureInvariant);
        var projects = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var resource in new[] { "ps -a", "network ls", "volume ls" })
        {
            string[] arguments = [.. resource.Split(' '), "--filter", ProjectLabel, "--format", ProjectLabelFormat];
            var lines = await docker.LinesAsync(cancellationToken, arguments).ConfigureAwait(false);
            if (lines is null)
                return null;
            projects.UnionWith(lines.Where(line => pattern.IsMatch(line)));
        }
        return [.. projects];
    }

    /// <summary>Removes the project's containers, networks and volumes, stopping at the first removal
    /// Docker refuses as the script's <c>set -e</c> did; null on success, else the failure (that removal,
    /// or the kind of resource that survived).</summary>
    private static async Task<string?> RemoveAsync(FunctionDocker docker, string project, CancellationToken cancellationToken)
    {
        var filter = $"{ProjectLabel}={project}";
        (string Kind, string[] List, string[] Remove)[] resources =
        [
            ("containers", ["ps", "-aq", "--filter", filter], ["rm", "-f"]),
            ("networks", ["network", "ls", "-q", "--filter", filter], ["network", "rm"]),
            ("volumes", ["volume", "ls", "-q", "--filter", filter], ["volume", "rm"])
        ];
        foreach (var (_, list, remove) in resources)
            foreach (var id in await docker.LinesAsync(cancellationToken, list).ConfigureAwait(false) ?? [])
                if (await docker.RunAsync(cancellationToken, [.. remove, id]).ConfigureAwait(false) is var exitCode and not 0)
                    return $"docker {string.Join(' ', remove)} {id} failed with exit code {exitCode}.";
        foreach (var (kind, list, _) in resources)
            if (await docker.LinesAsync(cancellationToken, list).ConfigureAwait(false) is not { Count: 0 })
                return $"The {kind} of QA Compose project '{project}' survived the removal.";
        return null;
    }

    private static BigInteger Parse(string digits) => BigInteger.Parse(digits, CultureInfo.InvariantCulture);
}
