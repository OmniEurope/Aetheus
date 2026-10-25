// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>verify-release-ancestry</c> function, port of the former <c>verify-release-ancestry.sh</c> script,
/// the production gate that refuses to deploy a release the deploy branch does not contain. Same Git
/// commands in the same order, run in the workspace with <c>GIT_TERMINAL_PROMPT=0</c> and
/// <c>GIT_ASKPASS=/bin/false</c> so a fetch that would need a credential fails instead of waiting: the
/// provenance file must be a regular file; its bytes minus spaces, tabs, carriage returns and line
/// feeds (<c>tr -d ' \t\r\n'</c>) must be hexadecimal and 40 or 64 long; a shallow checkout is first
/// unshallowed from <c>origin</c> and a missing commit fetched by its id (both best effort, their
/// errors discarded like <c>2&gt;/dev/null || true</c>); a commit still unknown, or not an ancestor of
/// the deploy ref while the history stays shallow, is refused as unprovable, and an ancestry
/// proved absent gets the full refusal. Every message is the script's, verbatim, one log line per
/// line it printed (stdout at Info, its own stderr at Error, Git's stderr at Warning); a refusal
/// fails the step with its first line.
///
/// Differences, deliberate: a deploy ref that does not resolve fails with a message of its own after
/// Git's (the script ended there under <c>set -e</c> with Git's message and exit code 128 only); a
/// provenance file that cannot be read fails with the .NET message; a NUL byte in the file is refused
/// as not hexadecimal, where the shell's command substitution may drop it; a FIFO or device is read as
/// a file where <c>[ -f ]</c> called it missing. Git is <c>git</c> looked up on the step's PATH.
/// </summary>
public sealed class VerifyReleaseAncestryFunction : IPipelineFunction
{
    private readonly IPipelineFunctionTools _tools;

    public VerifyReleaseAncestryFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal VerifyReleaseAncestryFunction(IPipelineFunctionTools tools) => _tools = tools;

    public string Name => PipelineFunctionCatalog.VerifyReleaseAncestry.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var sourceCommitFile = context.Parameters["source_commit_file"];
        var deployRef = context.Parameters.GetValueOrDefault("deploy_ref") is { Length: > 0 } given ? given : "HEAD";

        var path = Path.GetFullPath(Path.Combine(context.Workspace, sourceCommitFile));
        if (!IsRegularFile(path))
            return await RefuseAsync(context,
                $"Release provenance file '{sourceCommitFile}' is missing; cannot prove the deploy branch carries this release.").ConfigureAwait(false);
        var releaseSha = ReadCommitId(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
        if (releaseSha is null)
            return await RefuseAsync(context, $"Release provenance file '{sourceCommitFile}' does not hold a commit id.").ConfigureAwait(false);
        if (releaseSha.Length is not (40 or 64))
            return await RefuseAsync(context,
                $"Release provenance '{releaseSha}' is not a full commit id; refusing to guess which commit it means.").ConfigureAwait(false);

        var git = new Git(_tools, context);
        return await DecideAsync(context, git, releaseSha, deployRef, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<PipelineFunctionResult> DecideAsync(
        PipelineFunctionContext context, Git git, string releaseSha, string deployRef, CancellationToken cancellationToken)
    {
        if (await git.IsShallowAsync(cancellationToken).ConfigureAwait(false))
            await git.RunAsync(Git.Output.DiscardStderr, cancellationToken, "fetch", "--quiet", "--unshallow", "origin").ConfigureAwait(false);
        if (!await git.HasCommitAsync(releaseSha, cancellationToken).ConfigureAwait(false))
            await git.RunAsync(Git.Output.DiscardStderr, cancellationToken, "fetch", "--quiet", "origin", releaseSha).ConfigureAwait(false);

        var deploy = await git.RunAsync(Git.Output.Capture, cancellationToken, "rev-parse", "--short", deployRef).ConfigureAwait(false);
        if (deploy.ExitCode != 0)
            return PipelineFunctionResult.Failure(
                $"The deploy ref '{deployRef}' does not resolve: git rev-parse --short exited with code {deploy.ExitCode}.");
        var deploySha = deploy.Stdout;

        if (!await git.HasCommitAsync(releaseSha, cancellationToken).ConfigureAwait(false))
            return await RefuseAsync(context,
                $"Release commit {releaseSha} is unknown to this checkout, so the deploy branch cannot be shown to contain it.",
                "Deepen the deploy checkout (AETHEUS_GIT_HISTORY_DEPTH) or confirm the release was published to this remote.").ConfigureAwait(false);

        var ancestry = await git.RunAsync(Git.Output.Log, cancellationToken, "merge-base", "--is-ancestor", releaseSha, deployRef).ConfigureAwait(false);
        if (ancestry.ExitCode == 0)
        {
            var release = await git.RunAsync(Git.Output.Capture, cancellationToken, "rev-parse", "--short", releaseSha).ConfigureAwait(false);
            await context.Log($"Deploy branch ({deploySha}) contains release commit {release.Stdout}.").ConfigureAwait(false);
            return PipelineFunctionResult.Success();
        }

        if (await git.IsShallowAsync(cancellationToken).ConfigureAwait(false))
            return await RefuseAsync(context,
                $"Release commit {releaseSha} is not reachable from {deploySha}, but this checkout is still shallow,",
                "so the deploy branch cannot be shown either to contain or to lack it. Deepen the checkout and retry.").ConfigureAwait(false);

        return await RefuseAsync(context,
            "Refusing to deploy: the deploy branch does not contain this release.",
            "",
            $"  release commit : {releaseSha}",
            $"  deploy branch  : {deploySha} ({deployRef})",
            "",
            "The image would be the one this release built, but the deployment instructions would come from an",
            "older branch state. Anything the release added to the deployment contract - a new BG_COMPOSE_ENV",
            "entry, a new Compose variable, a changed migration step - would be silently dropped, and the run",
            "would still report success.",
            "",
            "Promote the release commit to the deploy branch first, then run this deployment again.").ConfigureAwait(false);
    }

    /// <summary>Writes the lines the script printed to stderr, then fails the step with the first.</summary>
    private static async Task<PipelineFunctionResult> RefuseAsync(PipelineFunctionContext context, params string[] lines)
    {
        foreach (var line in lines)
            await context.Log(line, TaskLogLevel.Error).ConfigureAwait(false);
        return PipelineFunctionResult.Failure(lines[0]);
    }

    /// <summary><c>[ -f path ]</c>: a regular file, a symbolic link followed to its final target.</summary>
    private static bool IsRegularFile(string path)
    {
        if (!File.Exists(path))
            return false;
        var target = File.ResolveLinkTarget(path, returnFinalTarget: true);
        return target is null || (target.Exists && target is FileInfo);
    }

    /// <summary><c>$(tr -d ' \t\r\n' &lt; file)</c> checked against <c>*[!0-9a-fA-F]*</c>: the remaining
    /// bytes when every one is a hexadecimal digit (possibly none), null otherwise.</summary>
    private static string? ReadCommitId(byte[] content)
    {
        var kept = content.Where(value => value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n')).ToArray();
        return kept.All(value => char.IsAsciiHexDigit((char)value)) ? Encoding.ASCII.GetString(kept) : null;
    }

    /// <summary>Git run in the workspace with the step's environment and no credential prompt. Its
    /// standard output is either captured, like <c>$(git ...)</c>, or written to the log; its standard
    /// error is written to the log unless discarded, like <c>2&gt;/dev/null</c>.</summary>
    private sealed class Git(IPipelineFunctionTools tools, PipelineFunctionContext context)
    {
        private const string StderrPrefix = "[stderr] ";
        private const string ExitLinePrefix = "[process] exit-code=";

        public enum Output
        {
            Log,
            Capture,
            DiscardStderr
        }

        private readonly string _executable = FunctionShell.Tool(context, "git");

        /// <summary><c>[ "$(git rev-parse --is-shallow-repository)" = true ]</c>.</summary>
        public async Task<bool> IsShallowAsync(CancellationToken cancellationToken) =>
            (await RunAsync(Output.Capture, cancellationToken, "rev-parse", "--is-shallow-repository").ConfigureAwait(false)).Stdout == "true";

        /// <summary><c>git cat-file -e "${sha}^{commit}" 2&gt;/dev/null</c> succeeded.</summary>
        public async Task<bool> HasCommitAsync(string sha, CancellationToken cancellationToken) =>
            (await RunAsync(Output.DiscardStderr, cancellationToken, "cat-file", "-e", sha + "^{commit}").ConfigureAwait(false)).ExitCode == 0;

        /// <summary>Runs Git to completion: its exit code (-1 when it timed out) and, when captured, its
        /// standard output without the trailing line feeds, whatever the exit code, as a command
        /// substitution reads it.</summary>
        public async Task<(int ExitCode, string Stdout)> RunAsync(Output output, CancellationToken cancellationToken, params string[] arguments)
        {
            var stdout = new StringBuilder();
            var result = await tools.RunAsync(StartInfo(arguments), (line, level) =>
            {
                if (line.StartsWith(ExitLinePrefix, StringComparison.Ordinal))
                    return Task.CompletedTask;
                if (level == TaskLogLevel.Warning && line.StartsWith(StderrPrefix, StringComparison.Ordinal))
                    return output == Output.DiscardStderr ? Task.CompletedTask : context.Log(line[StderrPrefix.Length..], TaskLogLevel.Warning);
                if (level == TaskLogLevel.Info && output == Output.Capture)
                {
                    stdout.Append(line).Append('\n');
                    return Task.CompletedTask;
                }
                return context.Log(line, level);
            }, cancellationToken).ConfigureAwait(false);
            return (result.TimedOut ? -1 : result.ExitCode, stdout.ToString().TrimEnd('\n'));
        }

        private ProcessStartInfo StartInfo(string[] arguments)
        {
            var startInfo = FunctionShell.StartInfo(context, _executable, context.Workspace!, arguments);
            startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            startInfo.Environment["GIT_ASKPASS"] = "/bin/false";
            return startInfo;
        }
    }
}
