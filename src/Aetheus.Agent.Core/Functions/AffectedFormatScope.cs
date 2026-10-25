// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Aetheus.Agent.Core.Functions;

/// <summary>What <c>dotnet format</c> must check: the whole solution, or the listed C# and Razor
/// files (none at all when the change touched no such file).</summary>
internal sealed record FormatScope(bool Full, IReadOnlyList<string> Files)
{
    public static FormatScope Whole { get; } = new(true, []);
}

/// <summary>
/// The scope decision of the former <c>run-affected-dotnet-format.sh</c> script, fail-closed exactly as
/// the script decides it: only a 40-hex head and a 40-hex baseline that Git knows, that is an ancestor
/// of the head, and whose diff adds or modifies files only (no deletion, rename or copy, no path with
/// whitespace, no formatting configuration, solution or project file) narrows the check to the added
/// and modified <c>.cs</c> and <c>.razor</c> files. Anything else checks the whole solution.
/// </summary>
internal sealed partial class AffectedFormatScope(IPipelineFunctionTools tools, PipelineFunctionContext context, string git, string workingDirectory)
{
    [GeneratedRegex("^[0-9a-fA-F]{40}$")]
    private static partial Regex FullSha();

    [GeneratedRegex("^(D|R|C)[0-9]*[ \\t\\r\\f\\v]")]
    private static partial Regex RemovingChange();

    // Deliberate difference: the script's pattern ended its Directory.Build alternative at the dot
    // ("Directory\.Build\.)$"), so it never matched Directory.Build.props or .targets and a change to
    // them was formatted as affected. Here they force the whole solution, as the script intended.
    [GeneratedRegex("(^|/)(\\.editorconfig|\\.globalconfig|Directory\\.Build\\.[^/]+|Directory\\.Packages\\.props|[^/]+\\.(slnx?|csproj))$")]
    private static partial Regex ConfigurationPath();

    [GeneratedRegex("\\.(cs|razor)$")]
    private static partial Regex SourcePath();

    public async Task<FormatScope> DecideAsync(bool affectedRequested, string? baseline, string? head, CancellationToken cancellationToken)
    {
        if (!affectedRequested || head is null || !FullSha().IsMatch(head) || baseline is null || !FullSha().IsMatch(baseline))
            return FormatScope.Whole;
        if (!await SucceedsAsync(false, cancellationToken, "cat-file", "-e", baseline + "^{commit}").ConfigureAwait(false)
            || !await SucceedsAsync(true, cancellationToken, "merge-base", "--is-ancestor", baseline, head).ConfigureAwait(false))
            return FormatScope.Whole;

        var changes = await FunctionShell
            .CaptureAsync(tools, Git("diff", "--name-status", "--find-renames", baseline + ".." + head), cancellationToken)
            .ConfigureAwait(false);
        return changes is null ? FormatScope.Whole : FromChanges(changes);
    }

    /// <summary>The scope a <c>git diff --name-status</c> listing allows.</summary>
    internal static FormatScope FromChanges(string changes)
    {
        var lines = changes.Split('\n');
        // cut -f2- prints a line holding no tab whole, so such a line is checked whole too.
        var paths = lines.Select(line => line.Split('\t', 2) is [_, var rest] ? rest : line).ToArray();
        if (lines.Any(line => RemovingChange().IsMatch(line))
            || paths.Any(path => path.Any(IsShellSpace))
            || paths.Any(path => ConfigurationPath().IsMatch(path)))
            return FormatScope.Whole;
        var files = lines
            .Select(line => line.Split('\t'))
            .Where(fields => fields.Length > 1 && fields[0] is "A" or "M")
            .Select(fields => fields[1])
            .Where(path => SourcePath().IsMatch(path))
            .ToArray();
        return new FormatScope(false, files);
    }

    private async Task<bool> SucceedsAsync(bool logged, CancellationToken cancellationToken, params string[] arguments)
    {
        Func<string, TaskLogLevel, Task> onOutput = logged ? (line, level) => context.Log(line, level) : (_, _) => Task.CompletedTask;
        var run = await tools.RunAsync(Git(arguments), onOutput, cancellationToken).ConfigureAwait(false);
        return run.ExitCode == 0 && !run.TimedOut;
    }

    /// <summary><c>[[:space:]]</c> in the C locale the script's grep ran in.</summary>
    private static bool IsShellSpace(char character) => character is ' ' or '\t' or '\r' or '\f' or '\v';

    private ProcessStartInfo Git(params string[] arguments) => FunctionShell.StartInfo(context, git, workingDirectory, arguments);
}
