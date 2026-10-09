// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// SCRIPTS-003: <c>launch-windows.ps1 -c -te</c> builds in Release, but the E2E path copied the backend from
/// <c>bin\Debug</c>, published the front in Debug and looked for <c>playwright.ps1</c> under Debug. On a
/// fresh checkout the run failed after a successful build; on a used one it tested stale Debug outputs
/// instead of the build it had just made.
///
/// Like the other launcher guards, this reads the script rather than running it (launching the stack
/// is not something the test host can do): every build output the launcher reads must name the
/// configuration it built, never a literal one.
/// </summary>
public sealed class LauncherBuildConfigurationAuditTests
{
    private static string LauncherSource =>
        File.ReadAllText(Path.Combine(RepositoryScan.Root, "scripts", "launch-core.ps1"));

    [Fact]
    public void BuildOutputs_AreReadFromTheConfigurationTheLauncherBuilt()
    {
        var source = LauncherSource;

        Assert.Contains("--configuration $buildConfiguration", source, StringComparison.Ordinal);
        var literalOutputs = Regex.Matches(source, @"bin\\(Debug|Release)\\", RegexOptions.CultureInvariant)
            .Select(match => LineOf(source, match.Index))
            .ToList();
        Assert.True(literalOutputs.Count == 0,
            "Build outputs read from a literal configuration:\n" + string.Join('\n', literalOutputs));
    }

    [Fact]
    public void FrontSnapshot_IsPublishedInTheConfigurationTheLauncherBuilt()
    {
        var frontPublish = LauncherSource.Split('\n')
            .Single(line => line.Contains("dotnet publish (Join-Path $frontDir \"Aetheus.Front.csproj\")", StringComparison.Ordinal));

        Assert.Contains("-c $buildConfiguration ", frontPublish, StringComparison.Ordinal);
    }

    private static string LineOf(string source, int index)
    {
        var start = source.LastIndexOf('\n', index) + 1;
        var end = source.IndexOf('\n', index);
        return source[start..(end < 0 ? source.Length : end)].Trim();
    }
}
