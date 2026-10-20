// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// A360-41. The scan floor only holds while every guard keeps using it. Nothing stopped the next
/// architecture guard from calling <c>Directory.EnumerateFiles</c> directly and silently reintroducing
/// the defect the floor exists to prevent: a guard whose scan comes back empty passes, because every
/// assertion it makes is vacuously true over an empty set.
///
/// This is the guard that guards the guards. It is deliberately narrow: it only looks at the
/// architecture folders, where a repository-wide scan is the entire point of the file.
/// </summary>
public sealed class ScanFloorGuardTests
{
    /// <summary>
    /// Files allowed to call the raw API, each for a stated reason. The helper itself must call it, and
    /// scans of a temporary directory created by the test are legitimately allowed to be empty.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // The floor helper itself is where the raw call has to live.
        "RepositoryScan.cs",
        // This guard names the raw API in the string literals it hunts for; matching itself would make
        // it permanently red for the wrong reason.
        "ScanFloorGuardTests.cs",
    };

    [Theory]
    [InlineData("tests/Aetheus.Back.Tests/Architecture")]
    [InlineData("tests/Aetheus.Front.Tests/Architecture")]
    public void NoArchitectureGuard_ScansTheRepositoryWithoutAFloor(string relativeFolder)
    {
        var folder = Path.Combine(RepositoryScan.Root, relativeFolder.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(Directory.Exists(folder), $"Guard folder not found: {relativeFolder}");

        var offenders = new List<string>();
        foreach (var file in RepositoryScan.EnumerateTopLevel(folder, "*.cs"))
        {
            if (Allowed.Contains(Path.GetFileName(file))) continue;

            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                // Skip the doc comments that legitimately NAME the raw API while explaining the floor.
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)
                    || trimmed.StartsWith("///", StringComparison.Ordinal)
                    || trimmed.StartsWith("*", StringComparison.Ordinal)) continue;

                if (line.Contains("Directory.EnumerateFiles", StringComparison.Ordinal)
                    || line.Contains("Directory.GetFiles", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}:{index + 1}");
            }
        }

        Assert.True(
            offenders.Count == 0,
            "An architecture guard must scan through RepositoryScan.Enumerate, which fails on an empty "
            + "scan. A raw enumeration passes vacuously when its path or filter is wrong, which is how "
            + "a guard reports green while protecting nothing. Found:"
            + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", offenders));
    }
}
