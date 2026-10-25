// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The directory operations a static site publication is made of, with the meaning the shell gave them:
/// <c>cp -a src/. dst/</c> (links stay links, modes and times are kept), <c>find dir -mindepth 1
/// -maxdepth 1 -exec rm -rf</c>, <c>mv</c> of every entry of a directory, <c>chmod -R a+rX</c> and
/// <c>rm -rf</c>. Nothing here ever follows a link: a link is copied, moved or removed as itself.
/// </summary>
internal static class DirectoryTree
{
    private const UnixFileMode ReadForAll = UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
    private const UnixFileMode ExecuteForAll = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;

    private static readonly EnumerationOptions s_everything = new()
    {
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        RecurseSubdirectories = false
    };

    /// <summary>The entries of a directory (files, directories and links, hidden ones included).</summary>
    public static IEnumerable<FileSystemInfo> Entries(string directory) =>
        new DirectoryInfo(directory).EnumerateFileSystemInfos("*", s_everything);

    /// <summary>Every regular file under a directory, in no promised order; links are neither read nor
    /// descended into.</summary>
    public static IEnumerable<FileInfo> Files(string directory)
    {
        foreach (var entry in Entries(directory))
        {
            if (entry.LinkTarget is not null)
                continue;
            if (entry is DirectoryInfo subdirectory)
            {
                foreach (var file in Files(subdirectory.FullName))
                    yield return file;
            }
            else if (entry is FileInfo file)
            {
                yield return file;
            }
        }
    }

    /// <summary><c>cp -a source/. destination/</c>, leaving out the entries <paramref name="skip"/> names.</summary>
    public static void CopyContents(string source, string destination, Func<string, bool>? skip = null)
    {
        foreach (var entry in Entries(source).Where(entry => skip is null || !skip(entry.Name)))
            Copy(entry, Path.Combine(destination, entry.Name));
    }

    private static void Copy(FileSystemInfo entry, string target)
    {
        if (entry.LinkTarget is { } link)
        {
            if (entry is DirectoryInfo)
                Directory.CreateSymbolicLink(target, link);
            else
                File.CreateSymbolicLink(target, link);
            return;
        }

        if (entry is DirectoryInfo directory)
        {
            Directory.CreateDirectory(target);
            CopyContents(directory.FullName, target);
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(target, directory.UnixFileMode);
            Directory.SetLastWriteTimeUtc(target, directory.LastWriteTimeUtc);
            return;
        }

        File.Copy(entry.FullName, target);
        File.SetLastWriteTimeUtc(target, entry.LastWriteTimeUtc);
    }

    /// <summary><c>find directory -mindepth 1 -maxdepth 1 -exec rm -rf {} +</c>, sparing the entries
    /// <paramref name="keep"/> names.</summary>
    public static void RemoveContents(string directory, params string[] keep)
    {
        foreach (var entry in Entries(directory).Where(entry => !keep.Contains(entry.Name, StringComparer.Ordinal)).ToList())
            Remove(entry);
    }

    /// <summary><c>rm -rf</c> of one path: a link goes, its target stays, and a path that is not there is
    /// not an error.</summary>
    public static void RemoveTree(string path)
    {
        FileSystemInfo entry = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
        if (entry.Exists || entry.LinkTarget is not null)
            Remove(entry);
    }

    private static void Remove(FileSystemInfo entry)
    {
        if (entry is DirectoryInfo directory && entry.LinkTarget is null)
            directory.Delete(recursive: true);
        else if (entry is DirectoryInfo)
            Directory.Delete(entry.FullName);
        else
            File.Delete(entry.FullName);
    }

    /// <summary><c>find source -mindepth 1 -maxdepth 1 -exec mv {} destination/ \;</c>.</summary>
    public static void MoveContents(string source, string destination)
    {
        foreach (var entry in Entries(source).ToList())
        {
            var target = Path.Combine(destination, entry.Name);
            if (entry is DirectoryInfo)
                Directory.Move(entry.FullName, target);
            else
                File.Move(entry.FullName, target);
        }
    }

    /// <summary><c>chmod -R a+rX directory</c>: readable by everyone, and executable by everyone when it
    /// is a directory or already executable by someone. A file system with no POSIX modes has nothing to
    /// change.</summary>
    public static void GrantReadToAll(string directory)
    {
        if (OperatingSystem.IsWindows())
            return;
        foreach (var entry in Entries(directory).Where(entry => entry.LinkTarget is null))
        {
            var mode = entry.UnixFileMode | ReadForAll;
            var executable = entry is DirectoryInfo || (mode & ExecuteForAll) != 0;
            File.SetUnixFileMode(entry.FullName, executable ? mode | ExecuteForAll : mode);
            if (entry is DirectoryInfo subdirectory)
                GrantReadToAll(subdirectory.FullName);
        }
    }
}
