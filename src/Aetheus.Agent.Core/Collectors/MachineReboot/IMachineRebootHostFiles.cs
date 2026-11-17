// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Collectors.MachineReboot;

/// <summary>The read-only view of the host the machine-reboot reader needs: files and directories with the agent's own
/// rights. Whatever is absent or refused reads as nothing (null, empty list), never as an error and never worked around.</summary>
internal interface IMachineRebootHostFiles
{
    /// <summary>The text of a file, or null when it is absent, too large or refused.</summary>
    string? ReadText(string path);

    /// <summary>The full paths of the files directly inside a directory, in ordinal order; empty when it is absent or refused.</summary>
    IReadOnlyList<string> ListFiles(string directory);

    /// <summary>The full paths of the directories directly inside a directory, in ordinal order; empty when it is absent or refused.</summary>
    IReadOnlyList<string> ListDirectories(string directory);

    /// <summary>Whether a path is a readable file, a link to one included.</summary>
    bool FileExists(string path);
}

/// <summary>Reads the real host. No shell, no sudo.</summary>
internal sealed class MachineRebootHostFiles : IMachineRebootHostFiles
{
    // Bounds what a hostile or broken file can make the agent hold in memory.
    private const long MaxFileBytes = 1024 * 1024;

    public string? ReadText(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            if (stream.Length > MaxFileBytes) return null;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public IReadOnlyList<string> ListFiles(string directory) => Enumerate(directory, Directory.EnumerateFiles);

    public IReadOnlyList<string> ListDirectories(string directory) => Enumerate(directory, Directory.EnumerateDirectories);

    public bool FileExists(string path) => File.Exists(path);

    private static List<string> Enumerate(string directory, Func<string, IEnumerable<string>> list)
    {
        try
        {
            return [.. list(directory).Order(StringComparer.Ordinal)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
