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

    internal const string DotNetLanguage = "dotnet";
    internal const string PythonLanguage = "python";
    internal const string JavaLanguage = "java";

    /// <summary>The product files of the languages the quality pipeline has a producer for, as its
    /// <c>detect-project-languages</c> function reads them (case-sensitive extensions).</summary>
    private static readonly (string Language, string Extension)[] LanguageExtensions =
        [(DotNetLanguage, ".csproj"), (PythonLanguage, ".py"), (JavaLanguage, ".java")];

    /// <summary>What that function never enters.</summary>
    private static readonly HashSet<string> NotProductCodeDirectories = new(
        [".git", ".claude", ".codex", "bin", "obj", "node_modules", ".venv", "target", "build",
            "TestResults", ".analysis-coverage", "coverage"],
        StringComparer.Ordinal);

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

    /// <summary>R-66, R-68: the versioned files the production deployment renders with the project's variables, read as
    /// the deployment reads them: the environment sample and the Apache templates below <c>.pipeline/configs/</c>.
    /// A directory the branch does not hold is not listed, so a project without them costs no failed read.</summary>
    public async Task<ProductionTemplates> ReadProductionTemplatesAsync(CancellationToken ct)
    {
        const string pipelineDirectory = ".pipeline";
        const string configsDirectory = ".pipeline/configs";
        var apache = new List<string>();
        var apachePaths = new List<string>();
        string? envSample = null;
        var root = await ListAsync(string.Empty, ct).ConfigureAwait(false);
        if (root is null) return new ProductionTemplates(null, apache, false);
        if (!HasDirectory(root, pipelineDirectory)) return new ProductionTemplates(null, apache);
        var pipeline = await ListAsync(pipelineDirectory, ct).ConfigureAwait(false);
        if (pipeline is null) return new ProductionTemplates(null, apache, false);
        if (!HasDirectory(pipeline, configsDirectory)) return new ProductionTemplates(null, apache);
        var configs = await ListAsync(configsDirectory, ct).ConfigureAwait(false);
        if (configs is null) return new ProductionTemplates(null, apache, false);

        // R-7: a directory the branch holds but that cannot be listed, or a file that cannot be read, is a template
        // the setup did not see, not a template that does not exist.
        var complete = true;
        if (HasDirectory(configs, ProductionTemplateVariables.EnvDirectory))
        {
            var envFiles = await ListAsync(ProductionTemplateVariables.EnvDirectory, ct).ConfigureAwait(false);
            if (envFiles is null) complete = false;
            else if (envFiles.Exists(entry => entry.Type == GitTreeEntryType.Blob && entry.Path == ProductionEnvSampleBuilder.Path))
            {
                envSample = await ReadFileAsync(ProductionEnvSampleBuilder.Path, ct).ConfigureAwait(false);
                complete &= envSample is not null;
            }
        }

        if (HasDirectory(configs, ProductionTemplateVariables.ApacheDirectory))
        {
            var apacheFiles = await ListAsync(ProductionTemplateVariables.ApacheDirectory, ct).ConfigureAwait(false);
            if (apacheFiles is null) complete = false;
            else
                foreach (var file in apacheFiles.Where(entry =>
                             entry.Type == GitTreeEntryType.Blob && entry.Name.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)))
                    if (await ReadFileAsync(file.Path, ct).ConfigureAwait(false) is { } text)
                    {
                        apache.Add(text);
                        apachePaths.Add(file.Path);
                    }
                    else
                        complete = false;
        }
        return new ProductionTemplates(envSample, apache, complete, apachePaths);
    }

    private static bool HasDirectory(List<GitLightTreeEntryDto>? entries, string path) =>
        entries?.Exists(entry => entry.Type == GitTreeEntryType.Tree && entry.Path == path) == true;

    /// <summary>R-30: a <c>global.json</c> at the root of the branch; null when the root could not be listed.</summary>
    public async Task<bool?> HasGlobalJsonAsync(CancellationToken ct)
    {
        var entries = await ListAsync(string.Empty, ct).ConfigureAwait(false);
        return entries?.Exists(entry => entry.Type == GitTreeEntryType.Blob && entry.Name == "global.json");
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

    /// <summary>Which of the languages the quality pipeline has a producer for (.NET, Python, Java) the branch holds a
    /// file of, the way the pipeline's own <c>detect-project-languages</c> function reads a workspace: a product file of the
    /// language below the root, skipping the directories that never hold product code. Each answer is null when the
    /// budget stopped the search before it found a file, since a file found is proof and the absence of one is not.</summary>
    public async Task<(bool? DotNet, bool? Python, bool? Java)> FindLanguagesAsync(CancellationToken ct)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var complete = true;
        var pending = new Queue<string>();
        pending.Enqueue(string.Empty);
        while (pending.Count > 0 && found.Count < LanguageExtensions.Length)
        {
            var directory = pending.Dequeue();
            var entries = await ListAsync(directory, ct).ConfigureAwait(false);
            if (entries is null)
            {
                complete = false;
                continue;
            }

            foreach (var entry in entries.Where(entry => entry.Type == GitTreeEntryType.Blob))
                foreach (var (language, extension) in LanguageExtensions)
                    if (entry.Name.EndsWith(extension, StringComparison.Ordinal)) found.Add(language);
            foreach (var child in entries.Where(entry => entry.Type == GitTreeEntryType.Tree && !IsNotProductCode(entry, directory)))
                pending.Enqueue(child.Path);
        }

        bool? Answer(string language) => found.Contains(language) ? true : complete ? false : null;
        return (Answer(DotNetLanguage), Answer(PythonLanguage), Answer(JavaLanguage));
    }

    /// <summary>What <see cref="FindBrowserTestsAsync"/> found. <c>Project</c>, <c>Framework</c> and <c>TestingPlatform</c>
    /// are set for a .NET Playwright project only: its project file, the target framework its build folder is named after
    /// (null when neither the project nor a <c>Directory.Build.props</c> above it says it) and whether the repository runs
    /// <c>dotnet test</c> on Microsoft.Testing.Platform.</summary>
    internal sealed record BrowserTestFinding(
        bool? Found, string? Tool, string? Directory, string? Project = null, string? Framework = null, bool TestingPlatform = false);

    /// <summary>R-59: the browser tests the QA pipeline can run against its deployed stack: a <c>package.json</c> that declares
    /// Playwright (<c>@playwright/test</c> or <c>playwright</c>) or Cypress, else a .NET project that references
    /// <c>Microsoft.Playwright</c> (a <c>PackageReference</c>; its version may be central, in <c>Directory.Packages.props</c>),
    /// anywhere below the root outside the directories that never hold the project's own files. <c>Found</c> is false only when
    /// every directory was listed and every package and project file read; a file found is proof, so a search that the budget
    /// stopped early answers null, never false.</summary>
    public async Task<BrowserTestFinding> FindBrowserTestsAsync(CancellationToken ct)
    {
        var complete = true;
        var reads = 0;
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

            foreach (var package in entries.Where(entry => entry.Type == GitTreeEntryType.Blob && entry.Name == "package.json"))
            {
                if (reads++ >= ProjectReadBudget)
                {
                    complete = false;
                    break;
                }
                if (await ReadFileAsync(package.Path, ct).ConfigureAwait(false) is not { } content)
                {
                    complete = false;
                    continue;
                }
                if (BrowserToolOf(content) is { } tool) return new BrowserTestFinding(true, tool, directory);
            }
            projects.AddRange(entries
                .Where(entry => entry.Type == GitTreeEntryType.Blob && IsProject(entry.Name))
                .Select(entry => entry.Path));
            foreach (var child in SubDirectories(entries)) pending.Enqueue(child);
        }

        // The projects named like end-to-end or test projects are read first: the answer is usually in the first one.
        var projectReads = 0;
        foreach (var path in projects.OrderBy(path => !LooksLikeBrowserTestProject(path)))
        {
            if (projectReads++ >= ProjectReadBudget)
            {
                complete = false;
                break;
            }
            if (await ReadFileAsync(path, ct).ConfigureAwait(false) is not { } content)
            {
                complete = false;
                continue;
            }
            if (!PlaywrightPackageReferencePattern.IsMatch(content)) continue;

            var directory = ParentOf(path);
            var framework = TargetFrameworkOf(content) ?? await InheritedTargetFrameworkAsync(directory, ct).ConfigureAwait(false);
            return new BrowserTestFinding(
                true, DotNetPlaywrightTool, directory, path, framework, await UsesTestingPlatformAsync(ct).ConfigureAwait(false));
        }

        return new BrowserTestFinding(complete ? false : null, null, null);
    }

    private static string ParentOf(string path) => path.Contains('/', StringComparison.Ordinal) ? path[..path.LastIndexOf('/')] : string.Empty;

    private static bool LooksLikeBrowserTestProject(string path) =>
        path.Contains("e2e", StringComparison.OrdinalIgnoreCase)
        || path.Contains("playwright", StringComparison.OrdinalIgnoreCase)
        || path.Contains("test", StringComparison.OrdinalIgnoreCase);

    /// <summary>The first target framework a project file names, when it is a plain moniker (a property reference is not).</summary>
    private static string? TargetFrameworkOf(string projectOrProps) =>
        TargetFrameworkPattern.Match(projectOrProps) is { Success: true } match && PlainMonikerPattern.IsMatch(match.Groups[1].Value)
            ? match.Groups[1].Value
            : null;

    /// <summary>A project that names no target framework takes the one of the nearest <c>Directory.Build.props</c> above it
    /// that names one, as MSBuild imports it.</summary>
    private async Task<string?> InheritedTargetFrameworkAsync(string directory, CancellationToken ct)
    {
        for (var current = directory; ; current = ParentOf(current))
        {
            var entries = await ListAsync(current, ct).ConfigureAwait(false);
            if (entries?.Find(entry => entry.Type == GitTreeEntryType.Blob && entry.Name == "Directory.Build.props") is { } props
                && await ReadFileAsync(props.Path, ct).ConfigureAwait(false) is { } content
                && TargetFrameworkOf(content) is { } framework)
                return framework;
            if (current.Length == 0) return null;
        }
    }

    /// <summary>Since the .NET 10 SDK, <c>dotnet test</c> runs on Microsoft.Testing.Platform when the <c>global.json</c> at the
    /// root says so; its options (<c>--project</c>, <c>--report-trx</c>) are not the VSTest ones.</summary>
    private async Task<bool> UsesTestingPlatformAsync(CancellationToken ct)
    {
        var root = await ListAsync(string.Empty, ct).ConfigureAwait(false);
        return root?.Find(entry => entry.Type == GitTreeEntryType.Blob && entry.Name == "global.json") is { } globalJson
            && await ReadFileAsync(globalJson.Path, ct).ConfigureAwait(false) is { } content
            && TestingPlatformRunnerPattern.IsMatch(content);
    }

    /// <summary>Playwright is read before Cypress: a project that has both runs the first one.</summary>
    private static string? BrowserToolOf(string packageJson) =>
        PlaywrightPackagePattern.IsMatch(packageJson) ? PlaywrightTool
        : CypressPackagePattern.IsMatch(packageJson) ? CypressTool
        : null;

    internal const string PlaywrightTool = "playwright";
    internal const string CypressTool = "cypress";
    internal const string DotNetPlaywrightTool = "playwright-dotnet";

    /// <summary>A project that references the Playwright for .NET package (<c>Microsoft.Playwright</c>, or one of its test
    /// adapters such as <c>Microsoft.Playwright.NUnit</c>). A <c>PackageVersion</c> alone, in <c>Directory.Packages.props</c>,
    /// is no reference: the project that uses it carries the <c>PackageReference</c>, without a version.</summary>
    [GeneratedRegex(@"<PackageReference\s[^>]*\bInclude\s*=\s*""Microsoft\.Playwright(\.[A-Za-z]+)?""", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex PlaywrightPackageReferencePattern { get; }

    [GeneratedRegex(@"<TargetFrameworks?>\s*([^<;\s]+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TargetFrameworkPattern { get; }

    [GeneratedRegex(@"\A[A-Za-z0-9.\-]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex PlainMonikerPattern { get; }

    [GeneratedRegex("\"runner\"\\s*:\\s*\"Microsoft\\.Testing\\.Platform\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex TestingPlatformRunnerPattern { get; }

    [GeneratedRegex("\"(@playwright/test|playwright)\"\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex PlaywrightPackagePattern { get; }

    [GeneratedRegex("\"cypress\"\\s*:", RegexOptions.CultureInvariant)]
    private static partial Regex CypressPackagePattern { get; }

    /// <summary>The directories the pipeline's language detection does not enter, and the annotated rule corpus of the
    /// platform's own scanners (<c>tests/security-rules</c>).</summary>
    private static bool IsNotProductCode(GitLightTreeEntryDto directory, string parent) =>
        NotProductCodeDirectories.Contains(directory.Name)
        || (directory.Name == "security-rules" && (parent == "tests" || parent.EndsWith("/tests", StringComparison.Ordinal)));

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
