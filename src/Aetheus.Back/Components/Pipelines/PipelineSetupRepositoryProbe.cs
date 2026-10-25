// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using Aetheus.Back.Components.Git;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// Reads the files of one branch to answer what the generic templates' default .NET and Docker
/// commands need: a solution, a test project a QA can run, a Compose file. It mirrors what those
/// commands look for (the patterns of their shell), within a budget of tree listings and file reads
/// so a large repository never makes the wizard wait: an answer that the budget did not allow is
/// <c>null</c> (not told), never a guess in either direction.
/// </summary>
internal sealed partial class PipelineSetupRepositoryProbe(
    IGitLightService git, ILogger logger, int repositoryId, string branch)
{
    /// <summary>The files the default production deployment accepts, in the order it looks for them.</summary>
    internal static readonly string[] ComposeFileNames =
        ["compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml"];

    /// <summary>The default commands look for a solution at the root, then within three levels: its files
    /// sit in directories at most two levels below the root.</summary>
    private const int SolutionDirectoryDepth = 2;

    /// <summary>The production deployment is told a Compose file by its path; it is looked for in the root and
    /// the directories up to this many levels below it (<c>deploy/compose/</c> is two).</summary>
    private const int ComposeDirectoryDepth = 3;

    private const int ListingBudget = 120;
    private const int ProjectReadBudget = 40;

    private static readonly HashSet<string> SkippedDirectories =
        new(["node_modules", ".git", ".pipeline-artifacts", "bin", "obj"], StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ProjectExtensions = [".csproj", ".fsproj", ".vbproj"];

    /// <summary>What marks a .NET test project in the default commands.</summary>
    [GeneratedRegex(@"Microsoft\.NET\.Test\.Sdk|Microsoft\.Testing\.Platform|xunit|nunit|MSTest|TUnit|IsTestProject", RegexOptions.CultureInvariant)]
    private static partial Regex TestProjectPattern { get; }

    /// <summary>A browser end-to-end project drives a running application the default commands never start.</summary>
    [GeneratedRegex(@"Microsoft\.Playwright|Selenium\.WebDriver", RegexOptions.CultureInvariant)]
    private static partial Regex BrowserTestPattern { get; }

    private readonly Dictionary<string, List<GitLightTreeEntryDto>> _listings = new(StringComparer.Ordinal);
    private int _listingsUsed;

    /// <summary>A Compose file the production deployment can be told to start (the root or within three levels
    /// of it); null when the budget stopped the search before it found one.</summary>
    public async Task<bool?> HasComposeFileAsync(CancellationToken ct)
    {
        var (files, complete) = await FindComposeFilesAsync(ct).ConfigureAwait(false);
        if (files.Count > 0) return true;
        return complete ? false : null;
    }

    /// <summary>Every Compose file within <see cref="ComposeDirectoryDepth"/> levels of the root, as paths
    /// relative to it. <c>Complete</c> is false when a listing could not be made: a file found is then proof,
    /// the absence of one is not.</summary>
    public async Task<(List<string> Files, bool Complete)> FindComposeFilesAsync(CancellationToken ct)
    {
        var files = new List<string>();
        var complete = true;
        var level = new List<string> { string.Empty };
        for (var depth = 0; depth <= ComposeDirectoryDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var directory in level)
            {
                var entries = await ListAsync(directory, ct).ConfigureAwait(false);
                if (entries is null)
                {
                    complete = false;
                    continue;
                }

                files.AddRange(entries
                    .Where(entry => entry.Type == GitTreeEntryType.Blob && ProductionComposeFiles.IsComposeFile(entry.Name))
                    .Select(entry => entry.Path));
                next.AddRange(SubDirectories(entries));
            }
            level = next;
        }

        // Directories left unread at the last level are deeper than the production deployment looks.
        return (files, complete);
    }

    /// <summary>A <c>*.sln</c> or <c>*.slnx</c> at the root or within three levels of it.</summary>
    public async Task<bool?> HasSolutionAsync(CancellationToken ct)
    {
        var complete = true;
        var level = new List<string> { string.Empty };
        for (var depth = 0; depth <= SolutionDirectoryDepth && level.Count > 0; depth++)
        {
            var next = new List<string>();
            foreach (var directory in level)
            {
                var entries = await ListAsync(directory, ct).ConfigureAwait(false);
                if (entries is null)
                {
                    complete = false;
                    continue;
                }

                if (entries.Exists(entry => entry.Type == GitTreeEntryType.Blob && IsSolution(entry.Name)))
                    return true;
                next.AddRange(SubDirectories(entries));
            }
            level = next;
        }

        // A directory left unlisted (budget) or deeper than the commands look is not proof of absence
        // except when every listing was made.
        return complete ? false : null;
    }

    /// <summary>A .NET test project that the default QA commands can run (not a browser end-to-end one).</summary>
    public async Task<bool?> HasRunnableTestProjectAsync(CancellationToken ct)
    {
        var complete = true;
        var projects = new List<string>();
        var pending = new Queue<string>();
        pending.Enqueue(string.Empty);
        while (pending.Count > 0)
        {
            var directory = pending.Dequeue();
            var entries = await ListAsync(directory, ct).ConfigureAwait(false);
            if (entries is null)
            {
                complete = false;
                continue;
            }

            projects.AddRange(entries
                .Where(entry => entry.Type == GitTreeEntryType.Blob && IsProject(entry.Name))
                .Select(entry => entry.Path));
            foreach (var child in SubDirectories(entries)) pending.Enqueue(child);
        }

        // The projects named like tests are read first: the answer is usually in the first one.
        var reads = 0;
        foreach (var path in projects.OrderBy(path => !path.Contains("test", StringComparison.OrdinalIgnoreCase)))
        {
            if (reads++ >= ProjectReadBudget) return null;
            var content = await ReadFileAsync(path, ct).ConfigureAwait(false);
            if (content is null)
            {
                complete = false;
                continue;
            }

            if (TestProjectPattern.IsMatch(content) && !BrowserTestPattern.IsMatch(content)) return true;
        }

        return complete ? false : null;
    }

    private static bool IsSolution(string name) =>
        name.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static bool IsProject(string name) =>
        ProjectExtensions.Any(extension => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<string> SubDirectories(List<GitLightTreeEntryDto> entries) =>
        entries.Where(entry => entry.Type == GitTreeEntryType.Tree && !SkippedDirectories.Contains(entry.Name))
            .Select(entry => entry.Path);

    /// <summary>One directory of the branch, listed once; null when the budget is spent or the listing failed.</summary>
    private async Task<List<GitLightTreeEntryDto>?> ListAsync(string directory, CancellationToken ct)
    {
        if (_listings.TryGetValue(directory, out var known)) return known;
        if (_listingsUsed >= ListingBudget) return null;
        _listingsUsed++;
        try
        {
            var entries = await git.GetTreeAsync(repositoryId, branch, directory.Length == 0 ? null : directory, ct)
                .ConfigureAwait(false);
            _listings[directory] = entries;
            return entries;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception, "Setup detection could not list '{Directory}' on '{Branch}' of repository {RepositoryId}.",
                directory, branch, repositoryId);
            return null;
        }
    }

    /// <summary>A text file of the branch; null when it is binary or could not be read.</summary>
    public async Task<string?> ReadFileAsync(string path, CancellationToken ct)
    {
        try
        {
            var blob = await git.GetBlobAsync(repositoryId, branch, path, ct).ConfigureAwait(false);
            return blob is { IsBinary: false, Content: { } content } ? content : null;
        }
        catch (InvalidOperationException exception)
        {
            logger.LogWarning(
                exception, "Setup detection could not read '{Path}' on '{Branch}' of repository {RepositoryId}.",
                path, branch, repositoryId);
            return null;
        }
    }
}
