// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// Creates a real directory link for the tests that follow one: a symbolic link where the OS allows it,
/// and on Windows without the SeCreateSymbolicLink privilege a junction, the same reparse point for
/// <see cref="DirectoryInfo.LinkTarget"/> and needing no privilege.
/// </summary>
internal static class DirectoryLinks
{
    internal static void Create(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is UnauthorizedAccessException or IOException)
        {
            CreateJunction(link, target, ex);
        }
    }

    private static void CreateJunction(string link, string target, Exception cause)
    {
        using var mklink = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            // cmd reads "/" as an option prefix: hand it backslash paths.
            ArgumentList = { "/c", "mklink", "/J", Path.GetFullPath(link), Path.GetFullPath(target) },
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start cmd.exe to create a junction.", cause);
        var error = mklink.StandardError.ReadToEnd();
        mklink.WaitForExit(10000);
        if (mklink.ExitCode != 0 || !Directory.Exists(link))
            throw new InvalidOperationException($"Could not create a junction at '{link}': {error}", cause);
    }

    /// <summary>Removes the link itself, never what it points at.</summary>
    internal static void Remove(string link)
    {
        if (new DirectoryInfo(link).LinkTarget is not null)
            Directory.Delete(link);
    }
}
