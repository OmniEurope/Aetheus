// SPDX-License-Identifier: EUPL-1.2

using System.Text.RegularExpressions;

namespace Aetheus.Back.Tests.Architecture;

/// <summary>Grid busy state reaches OE through the shared wrapper or the page's IsLoading parameter
/// and uses the table loading bar. Existing rows remain visible without an application
/// veil.</summary>
public sealed class GridWrapperAuditTests
{
    // Quote-aware: a '>' inside an attribute value (a lambda's arrow, a generic such as
    // Array.Empty<MonitoredAppStatusDto>()) does not end the tag; the former "=>" special case missed the
    // generic and read MonitoredAppsStatusGrid's tag as ending before its Busy.
    private static readonly Regex GridOpeningTag = new(@"<OmniDataGrid\b(?<attributes>(?:""[^""]*""|[^>""])*)>", RegexOptions.Singleline);
    private static readonly Regex BusyAttribute = new(@"\sBusy=""(?<value>[^""]*)""", RegexOptions.Compiled);
    private static readonly Regex LoadingBarOff = new(@"\sShowLoadingBar=""(?:false|@false)""", RegexOptions.Compiled);
    private static readonly Regex IsLoadingParameter = new(@"\[Parameter\]\s*public\s+bool\s+IsLoading\s*\{\s*get;\s*set;\s*\}", RegexOptions.Compiled);
    private const string Wrapper = "AetheusDataGrid.razor";
    private const string Veil = "aetheus-grid-loading";

    [Fact]
    public void A_Grid_Is_Handed_Busy_Only_From_Its_Own_IsLoading_Parameter()
    {
        var files = FrontRazorFiles();

        Assert.True(files.Count >= 100, $"Only {files.Count} razor files scanned; the guard is not seeing the app.");

        var offenders = new List<string>();
        var sanctioned = new List<string>();
        foreach (var file in files.Where(file => file.Name != Wrapper))
        {
            foreach (Match grid in GridOpeningTag.Matches(file.Text))
            {
                var busy = BusyAttribute.Match(grid.Groups["attributes"].Value);
                if (!busy.Success) continue;

                var codeBehind = file.Path + ".cs";
                var declaresIsLoading = File.Exists(codeBehind) && IsLoadingParameter.IsMatch(File.ReadAllText(codeBehind));
                if (busy.Groups["value"].Value == "@IsLoading" && declaresIsLoading)
                    sanctioned.Add(file.Name);
                else
                    offenders.Add($"{file.Name} (Busy=\"{busy.Groups["value"].Value}\")");
            }
        }

        Assert.True(offenders.Count == 0,
            "These hand Busy something other than the component's own [Parameter] bool IsLoading: "
            + string.Join(", ", offenders));
        // The scan must detect both page-fed grids to avoid passing vacuously.
        Assert.Contains("PipelineRunsGrid.razor", sanctioned);
        Assert.Contains("MonitoredAppsStatusGrid.razor", sanctioned);
    }

    [Fact]
    public void No_Grid_Turns_Off_The_Loading_Bar_Or_Draws_A_Veil()
    {
        var files = FrontRazorFiles();

        var barOff = files
            .Where(file => GridOpeningTag.Matches(file.Text)
                .Any(match => LoadingBarOff.IsMatch(match.Groups["attributes"].Value)))
            .Select(file => file.Name)
            .ToList();
        Assert.True(barOff.Count == 0, "These turn OE's grid loading bar off: " + string.Join(", ", barOff));

        var veiled = files.Where(file => file.Text.Contains(Veil, StringComparison.Ordinal)).Select(file => file.Name).ToList();
        Assert.True(veiled.Count == 0, "These still draw the Aetheus loading veil over a grid: " + string.Join(", ", veiled));

        var stylesheet = File.ReadAllText(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));
        Assert.DoesNotContain("." + Veil, stylesheet, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Wrapper_Hands_The_Page_Load_To_The_Grid_Busy_State()
    {
        var wrapper = Assert.Single(FrontRazorFiles(), file => file.Name == Wrapper);
        var grid = Assert.Single(GridOpeningTag.Matches(wrapper.Text)).Groups["attributes"].Value;

        Assert.Contains("Busy=\"@EffectiveBusy\"", grid, StringComparison.Ordinal);
        var codeBehind = File.ReadAllText(wrapper.Path + ".cs");
        // Page loading state is forwarded to the grid.
        Assert.Contains("private bool EffectiveBusy => IsLoading", codeBehind, StringComparison.Ordinal);
        Assert.Contains("ShowLoadingBar=\"true\"", grid, StringComparison.Ordinal);
    }

    private static List<(string Path, string Name, string Text)> FrontRazorFiles() => RepositoryScan
        .Enumerate(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front"), "*.razor")
        .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        .Select(path => (Path: path, Name: System.IO.Path.GetFileName(path), Text: File.ReadAllText(path)))
        .ToList();
}
