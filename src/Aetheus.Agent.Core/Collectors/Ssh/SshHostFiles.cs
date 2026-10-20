// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors.Ssh;

/// <summary>Reads with the agent's own rights only. A permission refusal is reported as
/// <see cref="AuthorizedKeysFileState.Unreadable"/>, never worked around.</summary>
internal sealed class SshHostFiles : ISshHostFiles
{
    // Bounds what a hostile or broken file can make the agent hold in memory.
    private const long MaxFileBytes = 1024 * 1024;

    public SshFileRead Read(string path)
    {
        try
        {
            // Opened rather than tested with File.Exists, which also answers false when a parent
            // directory may not be traversed: the open tells "absent" from "refused" apart.
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxFileBytes) return SshFileRead.Unreadable;
            using var reader = new StreamReader(stream);
            return new SshFileRead(AuthorizedKeysFileState.Read, reader.ReadToEnd());
        }
        catch (FileNotFoundException)
        {
            return SshFileRead.Absent;
        }
        catch (DirectoryNotFoundException)
        {
            return SshFileRead.Absent;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return SshFileRead.Unreadable;
        }
    }

    public bool DirectoryExists(string directory) => Directory.Exists(directory);

    public IReadOnlyList<string>? List(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                // Same ambiguity as for files: a parent the agent may not traverse hides it.
                _ = Directory.EnumerateFileSystemEntries(directory).FirstOrDefault();
                return [];
            }
            return Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).ToList();
        }
        catch (DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
