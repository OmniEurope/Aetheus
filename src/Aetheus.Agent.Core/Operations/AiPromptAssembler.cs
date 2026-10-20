// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Builds what an AI step sends: a system prompt owned by Aetheus, the same for every provider, and a
/// user prompt made of the task's prompt, its <c>prompt_file</c>, the contents of its
/// <c>context_paths</c> and, with <c>git_range</c>, the log and diff of that range. The model has no
/// tools, so this is all it sees. Every attached part has a hard byte budget and says when it was cut.
///
/// Files are read only inside the workspace, and never through a symbolic link: a repository can
/// commit a link to the agent's own credentials, and its target would otherwise be sent to the
/// provider. Git runs with argv only, with external diff and textconv drivers disabled.
/// </summary>
internal static class AiPromptAssembler
{
    internal const int PromptFileBudgetBytes = 100_000;
    internal const int ContextFileBudgetBytes = 64_000;
    internal const int ContextBudgetBytes = 200_000;
    internal const int GitLogBudgetBytes = 32_000;
    internal const int GitDiffBudgetBytes = 200_000;
    internal const int FallbackCommitCount = 10;
    private const int MaxDirectoryEntries = 200;
    private const string Fence = "````";

    public static string SystemPrompt(bool gate, bool findings)
    {
        var builder = new StringBuilder();
        builder.AppendLine(
            "You are an assistant that Aetheus, a CI/CD and infrastructure tool, runs for one automated task. " +
            "You have no tools: you cannot run commands, open files or browse. Everything you may use is in the " +
            "user message: the task, then any repository files and git history the run attached. Never invent " +
            "the content of a file you were not shown; say what is missing instead. Write your answer in Markdown.");
        if (gate)
            builder.AppendLine().AppendLine(
                "End your report with one line, alone on it: VERDICT: PASS or VERDICT: FAIL.");
        if (findings)
            builder.AppendLine().AppendLine(
                "Finish your answer with exactly one fenced code block tagged json (after the VERDICT line, if " +
                "any) holding {\"findings\":[{\"file\":\"<repository-relative path>\",\"line\":<1-based line, 0 if " +
                "unknown>,\"severity\":\"error|warning|note\",\"rule\":\"<short kebab-case id>\",\"message\":\"<one " +
                "sentence>\"}]}. Use error for a likely bug or vulnerability, warning for a risk, note for a remark. " +
                "Write {\"findings\":[]} when there is nothing to report. Report only what you can point to in the " +
                "material you were given.");
        return builder.ToString().TrimEnd();
    }

    public static async Task<string> BuildUserPromptAsync(
        AiRunConfiguration configuration,
        string workingDirectory,
        IShellRunner shellRunner,
        Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var builder = new StringBuilder(configuration.Prompt.TrimEnd());
        if (!string.IsNullOrWhiteSpace(configuration.PromptFile))
        {
            var path = ResolveInside(workingDirectory, configuration.PromptFile)
                ?? throw new InvalidOperationException("AI prompt_file is not a safe path inside the workspace.");
            if (!File.Exists(path))
                throw new InvalidOperationException($"AI prompt_file '{configuration.PromptFile}' does not exist.");
            if (new FileInfo(path).Length > PromptFileBudgetBytes)
                throw new InvalidOperationException(
                    $"AI prompt_file '{configuration.PromptFile}' exceeds {PromptFileBudgetBytes} bytes.");
            builder.AppendLine().AppendLine();
            builder.Append((await File.ReadAllTextAsync(path, ct).ConfigureAwait(false)).TrimEnd());
        }
        if (configuration.ContextPaths.Count > 0)
            await AppendContextAsync(builder, configuration.ContextPaths, workingDirectory, onOutput, ct)
                .ConfigureAwait(false);
        if (configuration.GitRange is not null)
            await AppendGitContextAsync(builder, configuration.GitRange, workingDirectory, shellRunner, onOutput, ct)
                .ConfigureAwait(false);
        return builder.ToString();
    }

    private static async Task AppendContextAsync(
        StringBuilder builder, List<string> paths, string workingDirectory,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        builder.AppendLine().AppendLine().AppendLine("## Context files");
        var remaining = ContextBudgetBytes;
        foreach (var relative in paths)
        {
            builder.AppendLine().AppendLine($"### {relative}");
            var path = ResolveInside(workingDirectory, relative);
            if (path is null)
            {
                builder.AppendLine("(not attached: the path leaves the workspace or goes through a symbolic link)");
                await onOutput($"AI context path '{relative}' refused: outside the workspace or through a link.", TaskLogLevel.Warning)
                    .ConfigureAwait(false);
                continue;
            }
            if (Directory.Exists(path))
            {
                var entries = Directory.EnumerateFileSystemEntries(path)
                    .Select(entry => Path.GetFileName(entry) + (Directory.Exists(entry) ? "/" : string.Empty))
                    .Order(StringComparer.Ordinal)
                    .Take(MaxDirectoryEntries + 1)
                    .ToList();
                builder.AppendLine("Directory entries:");
                foreach (var entry in entries.Take(MaxDirectoryEntries)) builder.AppendLine($"- {entry}");
                if (entries.Count > MaxDirectoryEntries)
                    builder.AppendLine($"(list cut at {MaxDirectoryEntries} entries)");
                continue;
            }
            if (!File.Exists(path))
            {
                builder.AppendLine("(not attached: no such file in the workspace)");
                continue;
            }
            if (remaining <= 0)
            {
                builder.AppendLine($"(not attached: the {ContextBudgetBytes}-byte context budget is used up)");
                continue;
            }
            var budget = Math.Min(ContextFileBudgetBytes, remaining);
            var (text, cut) = await ReadBoundedAsync(path, budget, ct).ConfigureAwait(false);
            remaining -= Encoding.UTF8.GetByteCount(text);
            AppendFenced(builder, text, "text");
            if (cut)
            {
                builder.AppendLine($"(file cut at {budget} bytes)");
                await onOutput($"AI context file '{relative}' was cut at {budget} bytes.", TaskLogLevel.Warning)
                    .ConfigureAwait(false);
            }
        }
    }

    private static async Task AppendGitContextAsync(
        StringBuilder builder, string range, string workingDirectory, IShellRunner shellRunner,
        Func<string, TaskLogLevel, Task> onOutput, CancellationToken ct)
    {
        if (!Directory.Exists(Path.Combine(workingDirectory, ".git")))
        {
            builder.AppendLine().AppendLine().AppendLine(
                $"## Git context\n(not attached: the workspace is not a git repository, so git_range '{range}' could not be read)");
            await onOutput("AI git_range ignored: the workspace is not a git repository.", TaskLogLevel.Warning)
                .ConfigureAwait(false);
            return;
        }
        string[] logArguments;
        string[] diffArguments;
        string title;
        if (IsValidRange(range))
        {
            title = $"## Git context: {range}";
            logArguments = ["log", "--stat", "--no-color", "--max-count=200", range, "--"];
            diffArguments = ["diff", "--no-color", "--no-ext-diff", "--no-textconv", range, "--"];
        }
        else
        {
            title = $"## Git context: the last {FallbackCommitCount} commits";
            await onOutput(
                $"AI git_range '{range}' is not a commit range (A..B); the last {FallbackCommitCount} commits are attached instead.",
                TaskLogLevel.Warning).ConfigureAwait(false);
            logArguments = ["log", "--stat", "--no-color", $"--max-count={FallbackCommitCount}", "--"];
            diffArguments = ["log", "-p", "--no-color", "--no-ext-diff", "--no-textconv", "--format=commit %H", $"--max-count={FallbackCommitCount}", "--"];
        }
        builder.AppendLine().AppendLine().AppendLine(title);
        if (!IsValidRange(range))
            builder.AppendLine($"(git_range '{range}' is not a commit range of the form A..B)");
        await AppendGitOutputAsync(builder, "git log --stat", logArguments, GitLogBudgetBytes, "text",
            workingDirectory, shellRunner, onOutput, ct).ConfigureAwait(false);
        await AppendGitOutputAsync(builder, "git diff", diffArguments, GitDiffBudgetBytes, "diff",
            workingDirectory, shellRunner, onOutput, ct).ConfigureAwait(false);
    }

    private static async Task AppendGitOutputAsync(
        StringBuilder builder, string heading, string[] arguments, int budget, string language,
        string workingDirectory, IShellRunner shellRunner, Func<string, TaskLogLevel, Task> onOutput,
        CancellationToken ct)
    {
        var result = await shellRunner.RunExecAsync(
            "git",
            ["-C", workingDirectory, .. arguments],
            new Dictionary<string, string>(),
            workingDirectory,
            inheritEnvironment: true,
            budget,
            ct,
            TimeSpan.FromSeconds(60)).ConfigureAwait(false);
        builder.AppendLine().AppendLine($"### {heading}");
        if (result.ExitCode != 0)
        {
            var reason = result.StdErr.Trim();
            builder.AppendLine($"(not attached: git failed: {reason})");
            await onOutput($"AI git context: {heading} failed: {reason}", TaskLogLevel.Warning).ConfigureAwait(false);
            return;
        }
        var (text, cut) = Cut(result.StdOut, budget);
        AppendFenced(builder, text, language);
        if (cut || result.Truncated)
        {
            builder.AppendLine($"({heading} cut at {budget} bytes)");
            await onOutput($"AI git context: {heading} was cut at {budget} bytes.", TaskLogLevel.Warning)
                .ConfigureAwait(false);
        }
    }

    /// <summary><c>A..B</c> where each side is a ref or a SHA: no option, no path, no three-dot form.</summary>
    internal static bool IsValidRange(string range)
    {
        var parts = range.Split("..");
        return parts.Length == 2 && parts.All(IsValidRevision);
    }

    private static bool IsValidRevision(string revision) =>
        revision.Length is > 0 and <= 100
        && revision[0] is not ('-' or '.')
        && revision.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '/' or '~' or '^' or '.' or '-');

    /// <summary>The full path of a workspace-relative path, or null when it is rooted, climbs out, or
    /// crosses a symbolic link on the way.</summary>
    internal static string? ResolveInside(string workingDirectory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)
            || Path.IsPathRooted(relative)
            || relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Any(segment => segment is "." or ".."))
            return null;
        var root = Path.GetFullPath(workingDirectory).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            return null;
        for (var current = full; current.Length > root.Length; current = Path.GetDirectoryName(current)!)
        {
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null) return null;
        }
        return full;
    }

    private static async Task<(string Text, bool Cut)> ReadBoundedAsync(string path, int maxBytes, CancellationToken ct)
    {
        await using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 81920, useAsync: true);
        var buffer = new byte[(int)Math.Min(stream.Length, (long)maxBytes)];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (read == 0) break;
            total += read;
        }
        return (Encoding.UTF8.GetString(buffer, 0, total), stream.Length > maxBytes);
    }

    private static (string Text, bool Cut) Cut(string value, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        return bytes.Length <= maxBytes
            ? (value, false)
            : (Encoding.UTF8.GetString(bytes, 0, maxBytes), true);
    }

    private static void AppendFenced(StringBuilder builder, string text, string language)
    {
        builder.AppendLine($"{Fence}{language}");
        builder.AppendLine(text.TrimEnd());
        builder.AppendLine(Fence);
    }
}
