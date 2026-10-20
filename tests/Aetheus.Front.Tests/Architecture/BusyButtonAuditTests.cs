// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Busy buttons preserve size and content through OE busy state and the shared veil,
/// without label swaps or spinner branches.</summary>
public sealed partial class BusyButtonAuditTests
{
    [GeneratedRegex(@"(?<![\w-])(IsBusy|BusyText)=""")]
    private static partial Regex LegacyBusyParameter();

    [GeneratedRegex(@"<OmniButton\b[^>]*?(?<!/)>(?<body>.*?)</OmniButton>", RegexOptions.Singleline)]
    private static partial Regex ButtonWithBody();

    [GeneratedRegex(@"@if\s*\([^)]*\b(?:busy|Busy|saving|Saving|loading|Loading|running|Running)\b[^)]*\)")]
    private static partial Regex BusyBranch();

    [Fact]
    public void NoButton_UsesTheSpinnerOrSwapsItsContent_WhileBusy()
    {
        var front = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");
        var files = RepositoryScan.Enumerate(front, "*.razor")
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .ToList();
        Assert.True(files.Count >= 100, $"The scan read only {files.Count} .razor files: a guard over nothing proves nothing.");

        var findings = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            var name = Path.GetRelativePath(front, file);
            if (LegacyBusyParameter().IsMatch(text)) findings.Add($"{name}: IsBusy= / BusyText= (use OmniButton Busy)");
            foreach (Match button in ButtonWithBody().Matches(text))
                if (BusyBranch().IsMatch(button.Groups["body"].Value))
                    findings.Add($"{name}: a busy branch inside an OmniButton changes its content");
        }

        Assert.Empty(findings);
    }

    [Fact]
    public void TheStylesheet_CarriesTheSharedVeil_OnTheSurfaceToken()
    {
        var css = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        var veil = Regex.Match(css, @"\.btn-busy::after\s*\{(?<body>[^}]*)\}");
        Assert.True(veil.Success, "app.css has no .btn-busy::after rule");
        Assert.Contains("var(--surface)", veil.Groups["body"].Value, StringComparison.Ordinal);
        Assert.Matches(@"--surface:\s*var\(--omni-color-surface\)", css);
    }
}
