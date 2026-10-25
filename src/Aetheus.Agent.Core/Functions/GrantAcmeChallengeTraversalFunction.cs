// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>grant-acme-challenge-traversal</c> function, port of
/// the former <c>grant-acme-challenge-traversal.sh</c> script: when the ACME challenge web root
/// lives under the agent work directory, Apache needs the others-execute bit on that directory to
/// reach it, so <c>chmod o+x</c> on it must succeed; a web root elsewhere (under <c>/var/www</c> since a
/// recent installer) needs nothing. The web root itself is then opened with <c>chmod -R o+rX</c>, best
/// effort: it belongs to root, which already created it world-readable. Same commands, same order, same
/// messages as the script.
///
/// Differences, deliberate: <c>ACME_WEBROOT</c> is the <c>webroot</c> parameter, defaulting to the same
/// variable. The work directory stays the agent's <c>AETHEUS_AGENT_WORK_DIRECTORY</c>, never a
/// parameter. A failed <c>chmod o+x</c> names its exit code, where the script stopped on <c>set -e</c>
/// with chmod's own message only.
/// </summary>
public sealed class GrantAcmeChallengeTraversalFunction : IPipelineFunction
{
    private readonly IPipelineFunctionTools _tools;

    public GrantAcmeChallengeTraversalFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal GrantAcmeChallengeTraversalFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.GrantAcmeChallengeTraversal.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var workDirectory = FunctionShell.Variable(context, "AETHEUS_AGENT_WORK_DIRECTORY");
        if (workDirectory is null)
            return PipelineFunctionResult.Failure("AETHEUS_AGENT_WORK_DIRECTORY is required");
        var webroot = FunctionShell.Input(context, "webroot", "ACME_WEBROOT");
        if (webroot is null)
            return PipelineFunctionResult.Failure("ACME_WEBROOT is required");

        var chmod = FunctionShell.Tool(context, "chmod");
        var directory = context.Workspace ?? Environment.CurrentDirectory;
        if (webroot.StartsWith(workDirectory + "/", StringComparison.Ordinal))
        {
            // The traversal bit on the agent work directory is the whole fix and must succeed.
            var result = await _tools.RunAsync(
                FunctionShell.StartInfo(context, chmod, directory, "o+x", workDirectory),
                (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0 || result.TimedOut)
                return PipelineFunctionResult.Failure($"chmod o+x {workDirectory} failed with exit code {result.ExitCode}.");
        }
        else
        {
            await context.Log(">>> The ACME web root is not under the agent work directory; no traversal to grant.").ConfigureAwait(false);
        }

        // Best effort, never fatal, its output discarded: chmod -R o+rX "$ACME_WEBROOT" 2>/dev/null || true.
        await _tools.RunAsync(
            FunctionShell.StartInfo(context, chmod, directory, "-R", "o+rX", webroot),
            (_, _) => Task.CompletedTask, cancellationToken).ConfigureAwait(false);
        await context.Log(">>> Traversal granted; Apache can reach the ACME challenge directory.").ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }
}
