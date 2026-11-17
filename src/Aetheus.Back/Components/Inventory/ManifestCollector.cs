// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Back.Components.Inventory;

internal sealed record ManifestFile(string Path, string BlobId, ManifestKind Kind)
{
    public string Directory => Path.Contains('/', StringComparison.Ordinal) ? Path[..Path.LastIndexOf('/')] : string.Empty;

    public string Technology => ManifestCatalog.Technology(Kind);
}

/// <summary>
/// What the manifests of one revision state together. A lock file decides for its folder: the project
/// file beside it adds nothing, and a project file with no lock file is what makes the read partial.
/// </summary>
internal sealed class ManifestCollector
{
    private static readonly ManifestKind[] JavaScriptLocks = [ManifestKind.NpmLock, ManifestKind.PnpmLock, ManifestKind.YarnLock];
    private static readonly ManifestKind[] PythonLocks = [ManifestKind.PoetryLock, ManifestKind.UvLock];

    private readonly Dictionary<ManifestKind, Action<ManifestFile, byte[]>> _readers;
    private readonly IReadOnlyList<ManifestFile> _manifests;
    private readonly List<(string Directory, IReadOnlyDictionary<string, string> Versions)> _central = [];

    public ManifestCollector(IReadOnlyList<ManifestFile> manifests)
    {
        _manifests = manifests;
        _readers = new Dictionary<ManifestKind, Action<ManifestFile, byte[]>>
        {
            [ManifestKind.PackagesLock] = (file, bytes) => Add(file, DotnetManifestParser.ParseLock(bytes)),
            [ManifestKind.CentralPackages] = (file, bytes) => _central.Add((file.Directory, DotnetManifestParser.ParseCentralVersions(Text(bytes)))),
            [ManifestKind.DotnetProject] = ReadDotnetProject,
            [ManifestKind.GlobalJson] = (file, bytes) => AddOne(file, DotnetManifestParser.ParseGlobalJson(bytes)),
            [ManifestKind.NpmLock] = (file, bytes) => Add(file, NodeManifestParser.ParseNpmLock(bytes)),
            [ManifestKind.PnpmLock] = (file, bytes) => Add(file, NodeManifestParser.ParsePnpmLock(Text(bytes))),
            [ManifestKind.YarnLock] = (file, bytes) => Add(file, NodeManifestParser.ParseYarnLock(Text(bytes))),
            [ManifestKind.PackageJson] = ReadPackageJson,
            [ManifestKind.Nvmrc] = (file, bytes) => AddOne(file, NodeManifestParser.ParseNvmrc(Text(bytes))),
            [ManifestKind.ComposerLock] = (file, bytes) => Add(file, PhpManifestParser.ParseLock(bytes)),
            [ManifestKind.ComposerJson] = ReadComposerJson,
            [ManifestKind.PythonRequirements] = ReadRequirements,
            [ManifestKind.PoetryLock] = (file, bytes) => Add(file, TextManifestParser.ParseTomlLock(Text(bytes), InventoryEcosystems.PyPI)),
            [ManifestKind.UvLock] = (file, bytes) => Add(file, TextManifestParser.ParseTomlLock(Text(bytes), InventoryEcosystems.PyPI)),
            [ManifestKind.CargoLock] = (file, bytes) => Add(file, TextManifestParser.ParseTomlLock(Text(bytes), InventoryEcosystems.Crates)),
            [ManifestKind.RubyLock] = (file, bytes) => Add(file, TextManifestParser.ParseGemfileLock(Text(bytes))),
            [ManifestKind.MavenPom] = ReadPom,
            [ManifestKind.GoMod] = ReadGoMod,
            [ManifestKind.Dockerfile] = ReadDockerfile
        };
    }

    public List<InventoryEntry> Entries { get; } = [];

    public List<InventoryGap> Gaps { get; } = [];

    public HashSet<string> DirectNames { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The files to open, central versions first: a project file needs them. A technology that is
    /// recognized but has no reader is recorded at once, and a project file beside its lock file is skipped.</summary>
    public IReadOnlyList<ManifestFile> Plan()
    {
        var plan = new List<ManifestFile>();
        foreach (var manifest in _manifests)
        {
            if (manifest.Kind == ManifestKind.Gradle
                || (manifest.Kind == ManifestKind.PythonProject && !HasLock(manifest, PythonLocks)))
                Gaps.Add(new InventoryGap(InventoryGapCodes.NotRead, manifest.Technology));
            else if (_readers.ContainsKey(manifest.Kind) && Needed(manifest))
                plan.Add(manifest);
        }

        return [.. plan.OrderBy(manifest => manifest.Kind == ManifestKind.CentralPackages ? 0 : 1).ThenBy(manifest => manifest.Path, StringComparer.Ordinal)];
    }

    private bool Needed(ManifestFile manifest) => manifest.Kind switch
    {
        ManifestKind.DotnetProject => !HasLock(manifest, [ManifestKind.PackagesLock]),
        ManifestKind.CentralPackages => _manifests.Any(other => other.Kind == ManifestKind.DotnetProject
            && Covers(manifest.Directory, other.Directory) && !HasLock(other, [ManifestKind.PackagesLock])),
        _ => true
    };

    public void Read(ManifestFile file, byte[] content) => _readers[file.Kind](file, content);

    private void Add(ManifestFile file, IEnumerable<InventoryEntry> entries) =>
        Entries.AddRange(entries.Select(entry => entry with { SourcePath = file.Path }));

    private void AddOne(ManifestFile file, InventoryEntry? entry)
    {
        if (entry is not null) Add(file, [entry]);
    }

    private void ReadDotnetProject(ManifestFile file, byte[] bytes)
    {
        var central = _central
            .Where(props => Covers(props.Directory, file.Directory))
            .OrderByDescending(props => props.Directory.Length)
            .Select(props => props.Versions)
            .FirstOrDefault() ?? new Dictionary<string, string>();
        var (entries, unresolved) = DotnetManifestParser.ParseProject(Text(bytes), central);
        Add(file, entries);
        if (entries.Count > 0 || unresolved > 0) Gaps.Add(new InventoryGap(InventoryGapCodes.NoLockFile, file.Technology));
        RecordCount(unresolved, InventoryGapCodes.Unpinned, file);
    }

    private void ReadPackageJson(ManifestFile file, byte[] bytes)
    {
        var (direct, runtime) = NodeManifestParser.ParsePackageJson(bytes);
        DirectNames.UnionWith(direct);
        AddOne(file, runtime);
        // A workspace member shares the lock file of the repository root.
        if (direct.Count > 0 && !HasLockAbove(file, JavaScriptLocks))
            Gaps.Add(new InventoryGap(InventoryGapCodes.NoLockFile, file.Technology));
    }

    private void ReadComposerJson(ManifestFile file, byte[] bytes)
    {
        var (entries, direct, unpinned) = PhpManifestParser.ParseProject(bytes);
        DirectNames.UnionWith(direct);
        if (HasLock(file, [ManifestKind.ComposerLock]))
        {
            Add(file, entries.Where(entry => entry.Kind == ProjectInventoryItemKind.Runtime));
            return;
        }

        Add(file, entries);
        if (direct.Count > 0) Gaps.Add(new InventoryGap(InventoryGapCodes.NoLockFile, file.Technology));
        RecordCount(unpinned, InventoryGapCodes.Unpinned, file);
    }

    private void ReadRequirements(ManifestFile file, byte[] bytes)
    {
        var (entries, unpinned) = TextManifestParser.ParseRequirements(Text(bytes));
        Add(file, entries);
        if (HasLock(file, PythonLocks)) return;
        Gaps.Add(new InventoryGap(InventoryGapCodes.NoLockFile, file.Technology));
        RecordCount(unpinned, InventoryGapCodes.Unpinned, file);
    }

    private void ReadPom(ManifestFile file, byte[] bytes)
    {
        var (entries, unresolved) = TextManifestParser.ParsePom(Text(bytes));
        Add(file, entries);
        Gaps.Add(new InventoryGap(InventoryGapCodes.NoLockFile, file.Technology));
        RecordCount(unresolved, InventoryGapCodes.Unpinned, file);
    }

    private void ReadGoMod(ManifestFile file, byte[] bytes)
    {
        var (entries, complete) = TextManifestParser.ParseGoMod(Text(bytes));
        Add(file, entries);
        if (!complete) Gaps.Add(new InventoryGap(InventoryGapCodes.NoLockFile, file.Technology));
    }

    private void ReadDockerfile(ManifestFile file, byte[] bytes)
    {
        var (entries, unresolved) = DockerfileParser.Parse(Text(bytes));
        Add(file, entries);
        RecordCount(unresolved, InventoryGapCodes.UnresolvedImage, file);
    }

    private void RecordCount(int count, string code, ManifestFile file)
    {
        for (var index = 0; index < count; index++) Gaps.Add(new InventoryGap(code, file.Technology));
    }

    private bool HasLock(ManifestFile file, IReadOnlyCollection<ManifestKind> kinds) =>
        _manifests.Any(other => kinds.Contains(other.Kind) && other.Directory == file.Directory);

    private bool HasLockAbove(ManifestFile file, IReadOnlyCollection<ManifestKind> kinds) =>
        _manifests.Any(other => kinds.Contains(other.Kind) && Covers(other.Directory, file.Directory));

    /// <summary>Whether a file of <paramref name="ancestor"/> applies to <paramref name="directory"/>, itself included.</summary>
    private static bool Covers(string ancestor, string directory) =>
        ancestor.Length == 0 || directory == ancestor || directory.StartsWith(ancestor + "/", StringComparison.Ordinal);

    private static string Text(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
