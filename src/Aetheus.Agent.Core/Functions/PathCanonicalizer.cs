// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// <c>readlink -m</c>: the path with every <c>.</c> and <c>..</c> resolved and every symbolic link
/// (or Windows junction) along it followed, the last component and the ones after a missing component
/// allowed to not exist. Separators come out as <c>/</c>, so the same text is compared on every platform.
/// </summary>
internal static class PathCanonicalizer
{
    private const int MaximumLinks = 40;

    public static string ReadlinkM(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var pending = new LinkedList<string>(Segments(full[root.Length..]));
        var resolved = new List<string>();
        var links = 0;
        while (pending.First is { } next)
        {
            pending.RemoveFirst();
            if (next.Value is "" or ".")
                continue;
            if (next.Value == "..")
            {
                if (resolved.Count > 0)
                    resolved.RemoveAt(resolved.Count - 1);
                continue;
            }

            var target = LinkTarget(Path.Combine(root, Path.Combine([.. resolved, next.Value])));
            if (target is null)
            {
                resolved.Add(next.Value);
                continue;
            }
            if (++links > MaximumLinks)
                throw new IOException($"Too many levels of symbolic links: {path}");
            if (Path.IsPathRooted(target))
            {
                root = Path.GetPathRoot(target)!;
                resolved.Clear();
                target = target[root.Length..];
            }
            foreach (var segment in Segments(target).Reverse())
                pending.AddFirst(segment);
        }
        return (root + string.Join('/', resolved)).Replace('\\', '/');
    }

    private static string[] Segments(string path) => path.Split(['/', '\\']);

    /// <summary>Where the link at <paramref name="path"/> points, or null when it is not a link or does not
    /// exist.</summary>
    private static string? LinkTarget(string path)
    {
        FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        return info.LinkTarget;
    }
}
