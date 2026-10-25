// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Git for the verification: argv only, run in the directory given with the step's environment, its
/// standard output returned as text without the trailing line feeds (<c>$(git ...)</c>) and its standard
/// error kept for the message of a failure. The archive is written with line endings as stored
/// (<c>core.autocrlf=false</c>, <c>core.eol=lf</c>): the target repository normalises line endings with
/// the same <c>.gitattributes</c>.
/// </summary>
internal sealed class PublicDistributionGit(IPipelineFunctionTools tools, PipelineFunctionContext context)
{
    private const string StderrPrefix = "[stderr] ";
    private const string ExitLinePrefix = "[process] exit-code=";

    private readonly string _executable = FunctionShell.Tool(context, "git");

    /// <summary>Runs Git and returns its standard output; any other outcome than exit code 0 is a failure
    /// of the step, with Git's own message.</summary>
    public async Task<string> RunAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var (exitCode, stdout, stderr) = await ExecuteAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
            throw new PublicDistributionException(
                $"git {string.Join(' ', arguments)} failed with exit code {exitCode}{(stderr.Length > 0 ? ": " + stderr : ".")}");
        return stdout;
    }

    /// <summary>Runs Git and returns its standard output, or null when it exits with another code than 0
    /// (<c>git show</c> of a path the commit does not have).</summary>
    public async Task<string?> TryRunAsync(string directory, CancellationToken cancellationToken, params string[] arguments)
    {
        var (exitCode, stdout, _) = await ExecuteAsync(directory, arguments, cancellationToken).ConfigureAwait(false);
        return exitCode == 0 ? stdout : null;
    }

    /// <summary><c>git archive --format=tar -o &lt;archive&gt; &lt;revision&gt;</c> of the repository in
    /// <paramref name="directory"/>.</summary>
    public async Task ArchiveAsync(string directory, string revision, string archivePath, CancellationToken cancellationToken) =>
        await RunAsync(
            directory, cancellationToken,
            "-c", "core.autocrlf=false", "-c", "core.eol=lf", "archive", "--format=tar", "-o", archivePath, revision).ConfigureAwait(false);

    private async Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string directory, string[] arguments, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var startInfo = FunctionShell.StartInfo(context, _executable, directory, arguments);
        var result = await tools.RunAsync(startInfo, (line, level) =>
        {
            if (line.StartsWith(ExitLinePrefix, StringComparison.Ordinal))
                return Task.CompletedTask;
            if (level == TaskLogLevel.Info)
                stdout.Append(line).Append('\n');
            else
                stderr.Append(stderr.Length > 0 ? " " : string.Empty).Append(line.StartsWith(StderrPrefix, StringComparison.Ordinal) ? line[StderrPrefix.Length..] : line);
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return (result.TimedOut ? -1 : result.ExitCode, stdout.ToString().TrimEnd('\n'), stderr.ToString());
    }
}
