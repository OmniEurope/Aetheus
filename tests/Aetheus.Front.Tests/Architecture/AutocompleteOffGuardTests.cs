// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Tests.Architecture;

/// <summary>R-12: the browser's autofill and history stay active. Aetheus never writes
/// <c>Autocomplete="off"</c> except in a reviewed case (one-time code or secret value) listed below.</summary>
public sealed class AutocompleteOffGuardTests
{
    /// <summary>Reviewed lines that may use <c>Autocomplete="off"</c>: relative file, a marker the line must
    /// contain, and the reason (one-time code or secret value only). Empty today: TOTP codes use
    /// <c>one-time-code</c> and secrets use <c>new-password</c>.</summary>
    private static readonly (string File, string Marker, string Reason)[] OffAllowList = [];

    [Fact]
    public void EveryAutocompleteOff_IsInTheAllowList()
    {
        var files = RepositoryScan.Enumerate(FrontDirectory(), "*.razor")
            .Concat(RepositoryScan.Enumerate(FrontDirectory(), "*.cs"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                        && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .ToList();
        Assert.True(files.Count > 100, "The scan of src/Aetheus.Front found almost no source: it is broken.");

        var violations = files
            .SelectMany(file => File.ReadAllLines(file).Select((line, index) => (file, line, index)))
            .Where(x => x.line.Contains("Autocomplete=\"off\"", StringComparison.OrdinalIgnoreCase))
            .Where(x => !OffAllowList.Any(a => Rel(x.file) == a.File && x.line.Contains(a.Marker, StringComparison.Ordinal)))
            .Select(x => $"{Rel(x.file)}:{x.index + 1}: {x.line.Trim()}")
            .ToList();

        Assert.True(violations.Count == 0,
            "R-12: Aetheus never disables browser autocomplete itself. Remove it, give the exact WHATWG token "
            + "(email, username, organization...), or for a one-time code / secret use one-time-code / new-password. "
            + "A reviewed exception goes in OffAllowList with its reason:\n  " + string.Join("\n  ", violations));
    }

    private static string Rel(string file) => Path.GetRelativePath(FrontDirectory(), file).Replace('\\', '/');

    private static string FrontDirectory() => Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
}
