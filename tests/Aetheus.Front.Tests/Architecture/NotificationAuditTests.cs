// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>NotifyHelper centralizes toast duration, content and correlated error reporting. Direct
/// overlay notifications are forbidden while dialog usage remains allowed.</summary>
public sealed partial class NotificationAuditTests
{
    [GeneratedRegex(@"\bNotificationService\b")]
    private static partial Regex NotificationServiceType();

    /// <summary>A member, parameter or injection typed as the overlay service, with its name.</summary>
    [GeneratedRegex(@"\bOmniOverlayService\??\s+@?(?<name>[A-Za-z_]\w*)")]
    private static partial Regex OverlayMember();

    [GeneratedRegex(@"<OmniOverlayService>\(\)\s*\??\.\s*Notify\s*\(")]
    private static partial Regex ResolvedOverlayNotify();

    [Fact]
    public void NoFileButNotifyHelper_UsesNotificationServiceDirectly()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var files = FrontSources(front);

        var offenders = files
            .Where(file => !IsNotifyHelper(file))
            .Where(file => NotificationServiceType().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(front, file))
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void NoFileButNotifyHelper_CallsOmniOverlayServiceNotify()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var files = FrontSources(front);

        // The scan has to see the helper's own call, or it would pass on a pattern that matches nothing.
        Assert.True(
            CallsOverlayNotify(File.ReadAllText(files.Single(IsNotifyHelper))),
            "The detector no longer recognises NotifyHelper's own call to OmniOverlayService.Notify.");

        var offenders = files
            .Where(file => !IsNotifyHelper(file))
            .Where(file => CallsOverlayNotify(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(front, file))
            .ToList();

        Assert.Empty(offenders);
    }

    [Theory]
    [InlineData("@inject OmniOverlayService Overlays\n@code { void F() => Overlays.Notify(\"x\"); }", true)]
    [InlineData("[Inject] private OmniOverlayService Overlay { get; set; } = default!;\nvoid F() => Overlay?.Notify(\"x\");", true)]
    [InlineData("sealed class C(OmniOverlayService overlay) { void F() => overlay.Notify(\"x\", OmniSeverity.Danger); }", true)]
    [InlineData("void F() => Services.GetRequiredService<OmniOverlayService>().Notify(\"x\");", true)]
    [InlineData("sealed class C(OmniOverlayService overlay) { Task F() => overlay.OpenDialogAsync(request); }", false)]
    [InlineData("[Inject] private NotifyHelper Toast { get; set; } = default!;\nvoid F() => Toast.Notify(NotificationSeverity.Error, \"T\", \"m\");", false)]
    public void Detector_TellsAnOverlayNotifyCallFromTheHelpersOwn(string source, bool expected) =>
        Assert.Equal(expected, CallsOverlayNotify(source));

    private static List<string> FrontSources(string front)
    {
        var files = RepositoryScan.Enumerate(front, "*.cs")
            .Concat(RepositoryScan.Enumerate(front, "*.razor"))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count >= 20, $"The scan read only {files.Count} files: a guard over nothing proves nothing.");
        return files;
    }

    private static bool IsNotifyHelper(string file) =>
        file.EndsWith($"Components{Path.DirectorySeparatorChar}Shared{Path.DirectorySeparatorChar}NotifyHelper.cs", StringComparison.Ordinal);

    private static bool CallsOverlayNotify(string source)
    {
        if (ResolvedOverlayNotify().IsMatch(source))
            return true;

        return OverlayMember().Matches(source)
            .Select(match => Regex.Escape(match.Groups["name"].Value))
            .Distinct(StringComparer.Ordinal)
            .Any(name => Regex.IsMatch(source, $@"\b{name}\s*\??\.\s*Notify\s*\("));
    }
}
