// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Components.Analysis.DependencyBump;

/// <summary>
/// The files a bump reads, keyed by repository path with forward slashes. A bump works on text it is given
/// and never touches a disk, so the caller decides where the files come from (a clone, a Git object). A lock file
/// may be given with an empty content: only its presence is read, never its text.
/// </summary>
internal sealed class RepositoryFiles
{
    private readonly Dictionary<string, string> _files;

    private RepositoryFiles(Dictionary<string, string> files) => _files = files;

    /// <summary>Null when a path is not a path inside the repository (see <see cref="Normalize"/>).</summary>
    public static RepositoryFiles? Create(IReadOnlyDictionary<string, string> files)
    {
        var normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (path, content) in files)
        {
            if (Normalize(path) is not { } clean || content is null) return null;
            normalized[clean] = content;
        }

        return new RepositoryFiles(normalized);
    }

    /// <summary>Forward slashes, no leading <c>./</c> or <c>/</c>, no empty, <c>.</c> or <c>..</c> segment and no drive. Null otherwise.</summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var segments = path.Replace('\\', '/').Split('/', StringSplitOptions.None).ToList();
        while (segments.Count > 0 && segments[0] is "" or ".") segments.RemoveAt(0);
        if (segments.Count == 0 || segments.Any(segment => segment is "" or "." or ".." || segment.Contains(':', StringComparison.Ordinal)))
            return null;
        return string.Join('/', segments);
    }

    public static string DirectoryOf(string path)
    {
        var slash = path.LastIndexOf('/');
        return slash < 0 ? string.Empty : path[..slash];
    }

    public static string NameOf(string path) => path[(path.LastIndexOf('/') + 1)..];

    public static string Combine(string directory, string name) => directory.Length == 0 ? name : directory + "/" + name;

    public IEnumerable<string> Paths => _files.Keys;

    public bool Contains(string path) => _files.ContainsKey(path);

    public string? Read(string path) => _files.GetValueOrDefault(path);

    /// <summary>The files directly in <paramref name="directory"/>.</summary>
    public IEnumerable<string> InDirectory(string directory) =>
        _files.Keys.Where(path => string.Equals(DirectoryOf(path), directory, StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    /// <summary>The files in <paramref name="directory"/> and below it.</summary>
    public IEnumerable<string> Under(string directory) =>
        _files.Keys.Where(path => directory.Length == 0 || path.StartsWith(directory + "/", StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    /// <summary><paramref name="directory"/>, its parent, and so on up to the root (the empty string), nearest first.</summary>
    public static IEnumerable<string> AncestorsOrSelf(string directory)
    {
        var current = directory;
        while (true)
        {
            yield return current;
            if (current.Length == 0) yield break;
            current = DirectoryOf(current);
        }
    }

    /// <summary>The nearest of <paramref name="directory"/> and its parents that holds a file named <paramref name="name"/> (compared without case).</summary>
    public string? NearestUp(string directory, string name) =>
        AncestorsOrSelf(directory)
            .Select(ancestor => InDirectory(ancestor).FirstOrDefault(path => NameOf(path).Equals(name, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(found => found is not null);

    /// <summary>The first file of <paramref name="directory"/> named <paramref name="name"/> (compared without case), or null.</summary>
    public string? Find(string directory, string name) =>
        InDirectory(directory).FirstOrDefault(path => NameOf(path).Equals(name, StringComparison.OrdinalIgnoreCase));
}
