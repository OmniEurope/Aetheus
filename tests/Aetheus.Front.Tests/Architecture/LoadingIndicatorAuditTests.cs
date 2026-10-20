// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Guards the product-wide loading identity: indeterminate waits use AetheusLoader (OE's logo
/// loader holding the Aetheus plane), while OE progress bars remain available for determinate progress.</summary>
public sealed class LoadingIndicatorAuditTests
{
    [Fact]
    public void IndeterminateCircularSpinners_AreNotUsed()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var violations = new List<string>();

        foreach (var file in RepositoryScan.Enumerate(frontDir, "*.razor"))
        {
            // The top-bar page-load indicator uses an indeterminate OE bar; in-page waits use
            // AetheusLoader.
            if (Path.GetFileName(file) == "PageLoadProgressBar.razor") continue;
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(source, @"<OmniProgressBar\b(?:(?:""[^""]*"")|[^>])*?/>", RegexOptions.CultureInvariant))
            {
                if (match.Value.Contains("Value=", StringComparison.Ordinal)) continue;
                // Recette R2-030 (2026-10-01): the user asked for a bar on a service row while its
                // operation runs. Only that one shape is allowed, a labelled linear bar (nothing spins,
                // decision D11 holds); any other valueless bar in the file is still refused. Recette R3-22
                // moved that one banner, unchanged, out of the grid into its own component.
                if (Path.GetFileName(file) == "ServiceActionsInFlightBanner.razor"
                    && match.Value.Contains("Shape=\"OmniProgressShape.Linear\"", StringComparison.Ordinal)
                    && match.Value.Contains("Label=", StringComparison.Ordinal)) continue;
                var line = source.AsSpan(0, match.Index).Count('\n') + 1;
                violations.Add($"{Path.GetRelativePath(frontDir, file)}:{line}");
            }
        }

        Assert.True(violations.Count == 0,
            "Indeterminate waits must render AetheusLoader instead of a valueless OmniProgressBar:\n  "
            + string.Join("\n  ", violations));
    }

    /// <summary>Loading uses the shared wrapper around OE's logo loader so product mark and wording
    /// stay consistent.</summary>
    [Fact]
    public void AetheusLoader_IsTheOeLogoLoaderHoldingThePlane()
    {
        var frontDir = Path.Combine(FindRepoRoot(), "src", "Aetheus.Front");
        var loaderFile = Path.Combine(frontDir, "Components", "Shared", "AetheusLoader.razor");
        var loader = File.ReadAllText(loaderFile);

        Assert.Matches(@"<OmniLogoLoader\b", loader);
        // The animated splash plane uses CSS keyframes.
        Assert.Matches(@"<OmniLogoLoader\b[^>]*\bAnimatedMark=""true""", loader);
        Assert.Contains("class=\"p-ship\"", loader, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"<(animate|animateTransform|animateMotion|set)\b", loader);

        var direct = RepositoryScan.Enumerate(frontDir, "*.razor")
            .Where(file => !string.Equals(file, loaderFile, StringComparison.OrdinalIgnoreCase))
            .Where(file => Regex.IsMatch(File.ReadAllText(file), @"<OmniLogoLoader\b", RegexOptions.CultureInvariant))
            .Select(file => Path.GetRelativePath(frontDir, file))
            .ToList();
        Assert.True(direct.Count == 0,
            "Render AetheusLoader, not OmniLogoLoader directly, so every wait shows the plane and the same words:\n  "
            + string.Join("\n  ", direct));

        var css = File.ReadAllText(Path.Combine(frontDir, "wwwroot", "css", "app.css"));
        Assert.DoesNotMatch(@"@keyframes\s+aetheus-loader", css);
    }

    /// <summary>Merged from the former backend-side copy of this guard: no valueless Mode-based bar either.</summary>
    [Fact]
    public void No_Indeterminate_Progress_Bar_Is_Used_As_A_Loader()
    {
        var offenders = FrontRazorFiles()
            .Where(file => file.Text.Contains("ProgressBarMode.Indeterminate", StringComparison.Ordinal))
            .Select(file => file.Name)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"These use an indeterminate bar where AetheusLoader is the app's loading indicator: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void Nothing_Rotates_In_The_Stylesheet()
    {
        var css = File.ReadAllText(Path.Combine(
            RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        Assert.DoesNotContain("rotate(360deg)", css, StringComparison.Ordinal);
        Assert.DoesNotContain("@keyframes spin", css, StringComparison.Ordinal);

        // The shared loading plane must not hide the empty states of every other grid.
        Assert.Contains(".omni-data-grid__state", css, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\.omni-data-grid__state\s*\{[^}]*display\s*:\s*none", css);
    }

    private static IEnumerable<(string Name, string Text)> FrontRazorFiles() =>
        RepositoryScan.Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front"), "*.razor")
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Select(path => (Name: Path.GetFileName(path), Text: File.ReadAllText(path)));

    private static string FindRepoRoot() => Aetheus.Front.Tests.Architecture.RepositoryScan.Root;
}
