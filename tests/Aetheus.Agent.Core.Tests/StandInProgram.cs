// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// A real executable standing in for an external program (<c>cp</c>, <c>docker</c>): a batch file on
/// Windows, an executable shell script elsewhere. The code under test starts it as it would start the
/// real program, with the same argument list.
/// </summary>
internal static class StandInProgram
{
    internal static string Write(string directory, string name, string windowsBody, string unixBody)
    {
        var path = Path.Combine(directory, OperatingSystem.IsWindows() ? $"{name}.cmd" : $"{name}.sh");
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, "@echo off\r\n" + windowsBody.ReplaceLineEndings("\r\n") + "\r\n");
        }
        else
        {
            File.WriteAllText(path, "#!/bin/sh\n" + unixBody.ReplaceLineEndings("\n") + "\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }
}
