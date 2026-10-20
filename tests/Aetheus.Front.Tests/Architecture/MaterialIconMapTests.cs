// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Every static and explicitly enumerated dynamic Material icon name must resolve to a
/// Phosphor mapping.</summary>
public sealed partial class MaterialIconMapTests
{
    private static readonly string[] DynamicNames =
    [
        "build_circle", "code_off", "construction", "dark_mode", "drive_file_rename", "expand_less",
        "file_copy", "folder_zip", "health_and_safety", "language", "light_mode", "memory", "note_add",
        "pause_circle", "timer", "timer_off", "toggle_on", "touch_app", "webhook", "window",
        "server", "pipeline", "rzi-server", "folder", "commit", "layers", "dns",
        "account_tree", "library_books"
    ];

    [GeneratedRegex("\\bIcon=\"(?<name>[a-z][a-z0-9_]*)\"")]
    private static partial Regex StaticIcon();

    [Fact]
    public void EveryStaticIconOfTheMainUi_HasAPhosphorReplacement()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var missing = RepositoryScan.Enumerate(front, "*.razor")
            .SelectMany(file => StaticIcon().Matches(File.ReadAllText(file)).Select(match => (file, name: match.Groups["name"].Value)))
            .Where(entry => !MaterialIconMap.Names.Contains(entry.name))
            .Select(entry => $"{Path.GetRelativePath(RepositoryScan.Root, entry.file)}: {entry.name}")
            .Distinct()
            .ToList();

        Assert.True(missing.Count == 0, "Material icons without a Phosphor replacement in MaterialIconMap:\n  " + string.Join("\n  ", missing));
    }

    [Fact]
    public void EveryDynamicIcon_HasAPhosphorReplacement() =>
        Assert.All(DynamicNames, name => Assert.Contains(name, MaterialIconMap.Names));

    [Fact]
    public void EveryReplacement_IsADefinedPhosphorIcon() =>
        Assert.All(MaterialIconMap.Names, name => Assert.True(Enum.IsDefined(MaterialIconMap.Resolve(name)), name));

    [Fact]
    public void AnUnknownName_IsReportedRatherThanDrawnAsSomethingElse() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => MaterialIconMap.Resolve("not_an_icon"));
}
