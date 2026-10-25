// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// Port of the former <c>detect-project-languages.sh</c> script: reports which language toolchains the
/// repository holds, so later steps can be conditioned on them. Same rules as the script: a language is
/// present when a product file of that language exists below the root, skipping the directories that
/// never hold product code (build outputs, dependency caches, tool folders and the annotated
/// <c>tests/security-rules</c> corpus). Names are matched case-sensitively and links are not followed,
/// like <c>find</c> does.
///
/// One difference, deliberate: the script prunes by directory name even when the scan root itself bears
/// that name; here only the directories below the root are pruned, because the root is what the step
/// asked to scan.
/// </summary>
public sealed class DetectProjectLanguagesFunction : IPipelineFunction
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.Ordinal)
    {
        ".git", ".claude", ".codex", "bin", "obj", "node_modules", ".venv", "target", "build",
        "TestResults", ".analysis-coverage", "coverage"
    };

    private static readonly (string Output, string[] Extensions)[] Languages =
    [
        ("HAS_DOTNET", [".csproj"]),
        ("HAS_JAVASCRIPT", [".js", ".jsx", ".ts", ".tsx"]),
        ("HAS_PYTHON", [".py"]),
        ("HAS_JAVA", [".java"])
    ];

    public string Name => PipelineFunctionCatalog.DetectProjectLanguages.Name;

    public Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return Task.FromResult(PipelineFunctionResult.Failure("The workspace is not set."));

        var root = ResolveRoot(context.Workspace, context.Parameters.GetValueOrDefault("root"), out var error);
        if (root is null)
            return Task.FromResult(PipelineFunctionResult.Failure(error!));
        if (!Directory.Exists(root))
            return Task.FromResult(PipelineFunctionResult.Failure($"Language detection root does not exist: {root}"));

        var found = Scan(root, cancellationToken);
        foreach (var (output, _) in Languages)
            context.SetOutput(output, found.Contains(output) ? "true" : "false");
        return Task.FromResult(PipelineFunctionResult.Success());
    }

    /// <summary>The scan root inside the workspace, or null with the reason when it escapes it.</summary>
    private static string? ResolveRoot(string workspace, string? root, out string? error)
    {
        error = null;
        var workspaceFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        var full = string.IsNullOrWhiteSpace(root)
            ? workspaceFull
            : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root, workspaceFull));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var inside = string.Equals(full, workspaceFull, comparison)
            || full.StartsWith(workspaceFull + Path.DirectorySeparatorChar, comparison);
        if (inside)
            return full;
        error = $"The root '{root}' is outside the workspace.";
        return null;
    }

    private static HashSet<string> Scan(string root, CancellationToken cancellationToken)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0 && found.Count < Languages.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            RecordFiles(directory, found);
            foreach (var child in SafeEnumerate(directory, directories: true).Where(child => !IsExcluded(child)))
                pending.Push(child);
        }
        return found;
    }

    private static void RecordFiles(string directory, HashSet<string> found)
    {
        foreach (var file in SafeEnumerate(directory, directories: false))
        {
            var extension = Path.GetExtension(file);
            foreach (var (output, extensions) in Languages)
                if (extensions.Contains(extension, StringComparer.Ordinal))
                    found.Add(output);
        }
    }

    private static bool IsExcluded(string directory)
    {
        var name = Path.GetFileName(directory);
        if (ExcludedDirectories.Contains(name))
            return true;
        return string.Equals(name, "security-rules", StringComparison.Ordinal)
            && string.Equals(Path.GetFileName(Path.GetDirectoryName(directory)), "tests", StringComparison.Ordinal);
    }

    /// <summary>Real directories or real files only (a link is neither for <c>find -type</c>); an
    /// unreadable directory is skipped, as <c>find</c> reports it and goes on.</summary>
    private static IEnumerable<string> SafeEnumerate(string directory, bool directories)
    {
        try
        {
            var entries = directories ? Directory.GetDirectories(directory) : Directory.GetFiles(directory);
            return entries.Where(entry => !File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
