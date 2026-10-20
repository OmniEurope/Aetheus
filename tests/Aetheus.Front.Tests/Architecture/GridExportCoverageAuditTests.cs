// SPDX-License-Identifier: EUPL-1.2
using System.Text.RegularExpressions;

namespace Aetheus.Front.Tests.Architecture;

/// <summary>Approved exporting tables use Markdown with source metadata and all filtered rows;
/// exceptions require user approval and a recorded reason. Guards retain paged export readers,
/// distinct titles and writable columns rather than relying on displayed rows.</summary>
public sealed class GridExportCoverageAuditTests
{
    private static readonly string FrontRoot = Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front");

    /// <summary>The wrapper itself forwards the parameters; the sweep checks its callers.</summary>
    private const string WrapperFile = "AetheusDataGrid.razor";

    private const string OptOut = "ExportMarkdown=\"false\"";

    /// <summary>Tables without export are listed by file and row type with their reasons; approval
    /// of these exceptions remains pending.</summary>
    private static readonly Dictionary<string, string> OptOuts = new(StringComparer.Ordinal)
    {
        [Key("Components/Servers/ServerDetailSections/DockerContainersTab.razor", "DockerEnvVarDto")] =
            "The environment of a container holds its passwords and keys in plain text; a file would carry them out.",
        [Key("Components/Settings/AdminSettings.razor", "RegistrationTokenDto")] =
            "Agent registration tokens are credentials: the cell masks them, the export would write them in clear.",
        [Key("Components/Pipelines/ReleasePickerGrid.razor", "ReleaseDto")] =
            "A picker inside the run dialog, holding only the recent releases offered for the choice; the full list is the Releases page, which exports.",
        [Key("Components/Users/RoleAssignmentPicker.razor", "RoleDto")] =
            "A picker inside a form (roles ticked for a user or a token), not a list; the Roles page exports the roles.",
    };

    /// <summary>Tables with Markdown export bars are listed with their purposes: logs, findings and
    /// performance.</summary>
    private static readonly Dictionary<string, string> MarkdownTables = new(StringComparer.Ordinal)
    {
        [Key("Components/Projects/ProjectDetailSections/ProjectQualitySection.razor", "AnalysisFindingDto")] =
            "Useful for AI: the project's open findings, to have them triaged or fixed.",
        [Key("Components/Pipelines/PipelineRunGateFindings.razor", "AnalysisFindingDto")] =
            "Useful for AI: the findings that blocked a run's quality gate, to have them fixed.",
        [Key("Components/Shared/AppLogsView.razor", "AppLogEntryDto")] =
            "Useful for AI: a monitored application's logs, to diagnose an incident.",
        [Key("Components/Shared/AppErrorsView.razor", "AppErrorEventDto")] =
            "Useful for AI: a monitored application's error groups with their frames, to diagnose them.",
        [Key("Components/Logs/SystemLogs.razor", "SystemLogEntryDto")] =
            "Useful for AI: the backend's logs, to diagnose an incident.",
        [Key("Components/Shared/AppPerformanceView.razor", "AppRouteTimingDto")] =
            "Useful for AI: a monitored application's route timings with the window and percentile method in the header (R-474), to find what to optimize.",
        [Key("Components/Logs/Performance.razor", "ApiCallTimingDto")] =
            "Useful for AI: the slowest API calls, with the server window in the header, to find what to optimize.",
        [Key("Components/Logs/Performance.razor", "ApiEndpointTimingDto")] =
            "Useful for AI: per-route percentiles, with the server window in the header, to find what to optimize.",
        [Key("Components/Servers/ServerDetailSections/ManageableServiceGrid.razor", "ServiceInfoDto")] =
            "Useful for AI: a server's services and their status, to diagnose a host (recette R3-53, user request of 2026-10-12).",
        [Key("Components/Logs/Performance.razor", "ClientApiCall")] =
            "Useful for AI: what this browser waited for, to tell the network from the server.",
    };

    private static readonly Regex WrapperOpen = new(@"<(?:Aetheus\.Front\.Components\.Shared\.)?AetheusDataGrid(?![A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex RawOpen = new(@"<OmniDataGrid(?![A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex ColumnOpen = new(@"<OmniDataGridColumn(?![A-Za-z])", RegexOptions.Compiled);

    /// <summary>Components that declare a column set of their own, checked in their own file.</summary>
    private static readonly string[] ColumnComponents = ["<AnalysisFindingColumns"];

    private static string Key(string file, string item) => $"{file}|{item}";

    private sealed record Grid(string File, string Tag, string Body, bool Wrapper)
    {
        public string Item => Regex.Match(Tag, @"TItem=""(?<item>[^""]+)""") is { Success: true } match
            ? match.Groups["item"].Value
            : "?";

        public string Key => GridExportCoverageAuditTests.Key(File, Item);

        public bool OptedOut => Tag.Contains(OptOut, StringComparison.Ordinal);

        public bool OffersMarkdown => !OptedOut
            && (Tag.Contains("ShowFooterBar=\"true\"", StringComparison.Ordinal)
                || Tag.Contains("ShowHeaderBar=\"true\"", StringComparison.Ordinal));
    }

    private static IReadOnlyList<Grid> Grids()
    {
        var grids = new List<Grid>();
        foreach (var path in RepositoryScan.Enumerate(FrontRoot, "*.razor"))
        {
            if (Path.GetFileName(path).Equals(WrapperFile, StringComparison.OrdinalIgnoreCase)) continue;
            var text = File.ReadAllText(path);
            var file = Path.GetRelativePath(FrontRoot, path).Replace('\\', '/');
            grids.AddRange(GridsIn(text, WrapperOpen, "AetheusDataGrid", file, wrapper: true));
            grids.AddRange(GridsIn(text, RawOpen, "OmniDataGrid", file, wrapper: false));
        }

        if (grids.Count(grid => grid.Wrapper) == 0 || grids.Count(grid => !grid.Wrapper) == 0)
            throw new InvalidOperationException("No grid found. A guard that scans nothing passes vacuously.");
        return grids;
    }

    private static IEnumerable<Grid> GridsIn(string text, Regex opening, string element, string file, bool wrapper)
    {
        foreach (Match open in opening.Matches(text))
        {
            var tagEnd = TagEnd(text, open.Index + open.Length);
            var tag = text[open.Index..(tagEnd + 1)];
            var close = text.IndexOf($"{element}>", tagEnd, StringComparison.Ordinal);
            var body = tag.EndsWith("/>", StringComparison.Ordinal) || close < 0 ? string.Empty : text[(tagEnd + 1)..close];
            yield return new Grid(file, tag, body, wrapper);
        }
    }

    /// <summary>The index of the '&gt;' closing a tag, outside quoted values (a lambda's arrow is not one).</summary>
    private static int TagEnd(string text, int index)
    {
        char? quote = null;
        for (; index < text.Length; index++)
        {
            var current = text[index];
            if (quote is not null)
            {
                if (current == quote) quote = null;
                continue;
            }
            if (current is '"' or '\'') { quote = current; continue; }
            if (current == '>') return index;
        }
        throw new InvalidOperationException("Unterminated grid tag.");
    }

    private static bool DeclaresAny(string tag, string attribute) =>
        Regex.IsMatch(tag, $@"(?<=[\s<]){Regex.Escape(attribute)}\s*=\s*""");

    private static string? ValueOf(string tag, string attribute)
    {
        var match = Regex.Match(tag, $@"(?<=[\s<]){Regex.Escape(attribute)}\s*=\s*""(?<value>(?:[^""]|""[^""]*""])*)""");
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static IEnumerable<Grid> Exporting() => Grids().Where(grid => !grid.OptedOut);

    [Fact]
    public void EveryOptOut_IsApproved_AndEveryApprovedOptOut_Exists()
    {
        var grids = Grids();
        var optedOut = grids.Where(grid => grid.OptedOut).Select(grid => grid.Key).ToHashSet(StringComparer.Ordinal);

        var unapproved = optedOut.Where(key => !OptOuts.ContainsKey(key)).ToList();
        var stale = OptOuts.Keys.Where(key => !optedOut.Contains(key)).ToList();

        Assert.True(unapproved.Count == 0,
            "A table opted out of the export without an approved reason (recette R2-124). Export it, or "
            + "ask the user and add it to OptOuts with its reason:\n" + string.Join("\n", unapproved));
        Assert.True(stale.Count == 0, "OptOuts names a table that no longer opts out:\n" + string.Join("\n", stale));
    }

    [Fact]
    public void MarkdownIsOffered_ExactlyByTheListedTables()
    {
        var offering = Grids().Where(grid => grid.OffersMarkdown).Select(grid => grid.Key).ToHashSet(StringComparer.Ordinal);

        var unlisted = offering.Where(key => !MarkdownTables.ContainsKey(key)).ToList();
        var missing = MarkdownTables.Keys.Where(key => !offering.Contains(key)).ToList();

        Assert.True(unlisted.Count == 0,
            "A table offers Markdown without being listed (recette R2-124, decision of 2026-10-05: Markdown only "
            + "where exporting to an AI is useful). Drop it, or ask the user and add it to MarkdownTables with "
            + "its reason:\n" + string.Join("\n", unlisted));
        Assert.True(missing.Count == 0, "MarkdownTables names a table that no longer offers Markdown:\n" + string.Join("\n", missing));
    }

    [Fact]
    public void NoTable_PicksItsOwnFormats()
    {
        string[] formatSwitches = ["ExportFormats", "ExportPosition", "ExportCsv", "ExportExcel", "ExportPdf"];
        var offenders = Grids()
            .Where(grid => formatSwitches.Any(attribute => DeclaresAny(grid.Tag, attribute))
                || (ValueOf(grid.Tag, "ExportMarkdown") is { } markdown && markdown != "false"))
            .Select(grid => grid.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A table shows the Markdown bar with ShowFooterBar=\"true\" or opts out with ExportMarkdown=\"false\"; "
            + "CSV, Excel and PDF stay off (preset and wrapper) and the obsolete ExportFormats/ExportPosition are not "
            + "written, so the two lists of this guard see every choice:\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryRemoteWrapperGrid_ReadsItsExportPageByPage()
    {
        var offenders = Exporting()
            .Where(grid => grid.Wrapper && DeclaresAny(grid.Tag, "LoadData"))
            .Where(grid => !DeclaresAny(grid.Tag, "ExportRead") && !DeclaresAny(grid.Tag, "ExportLoad"))
            .Select(grid => grid.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A grid fed by LoadData reads its export through ExportRead (or ExportLoad), every page from the "
            + "API; without it the wrapper throws at render. Missing in:\n" + string.Join("\n", offenders));
    }

    /// <summary>R3-54: the export buttons, their cancel and the column chooser are drawn as OmniButtons
    /// of the page's size (OE 1.8.1); left null they are 16 px tall, out of step with the other buttons.</summary>
    [Fact]
    public void EveryGridWithABar_SizesItsBarButtonsLikeThePageButtons()
    {
        const string Size = "BarButtonSize=\"OmniControlSize.Medium\"";
        string[] barSwitches = ["ShowFooterBar", "ShowHeaderBar", "AllowColumnHide"];
        var offenders = Grids()
            .Where(grid => !grid.Wrapper && barSwitches.Any(attribute => DeclaresAny(grid.Tag, attribute)))
            .Where(grid => !grid.Tag.Contains(Size, StringComparison.Ordinal))
            .Select(grid => grid.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"A raw OmniDataGrid with a header or footer bar or a column chooser must set {Size} (recette R3-54). Missing in:\n"
            + string.Join("\n", offenders));

        var wrapper = File.ReadAllText(Path.Combine(FrontRoot, "Components", "Shared", WrapperFile));
        Assert.Contains(Size, wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryRawGrid_SaysWhereItsRowsComeFrom()
    {
        var offenders = Exporting()
            .Where(grid => !grid.Wrapper && !DeclaresAny(grid.Tag, "ExportTitle"))
            .Select(grid => grid.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "A raw OmniDataGrid exports (default preset) and must title its file with where the rows come "
            + "from: ExportTitle=\"@ExportContext.Title(...)\" ExportFields=\"@ExportContext.Fields()\". Missing in:\n"
            + string.Join("\n", offenders));
    }

    [Fact]
    public void TheExportingTablesOfOneFile_HaveDistinctTitles()
    {
        var offenders = Exporting()
            .GroupBy(grid => grid.File)
            .Where(file => file.Count() > 1)
            .SelectMany(file => file
                .GroupBy(grid => ValueOf(grid.Tag, "ExportTitle") ?? ValueOf(grid.Tag, "ExportName") ?? string.Empty)
                .Where(title => title.Count() > 1)
                .Select(title => $"{file.Key}: {title.Count()} tables titled '{title.Key}'"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "Two exporting tables of one page would write files of the same title and name; give each an "
            + "ExportName (wrapper) or an ExportTitle (raw grid):\n" + string.Join("\n", offenders));
    }

    [Fact]
    public void EveryExportingTable_HasAColumnTheExportCanWrite()
    {
        var offenders = Exporting()
            .Where(grid => !ColumnComponents.Any(component => grid.Body.Contains(component, StringComparison.Ordinal)))
            .Where(grid => !ColumnTags(grid.Body).Any(Writable))
            .Select(grid => grid.Key)
            .ToList();

        Assert.True(offenders.Count == 0,
            "OE refuses to export a table whose columns are all templates: give the columns that hold data an "
            + "ExportValue (or a Property). No writable column in:\n" + string.Join("\n", offenders));
    }

    private static IEnumerable<string> ColumnTags(string body) =>
        ColumnOpen.Matches(body).Select(open => body[open.Index..(TagEnd(body, open.Index + open.Length) + 1)]);

    private static bool Writable(string column) =>
        (DeclaresAny(column, "Property") || DeclaresAny(column, "Value") || DeclaresAny(column, "ExportValue"))
        && !Regex.IsMatch(column, @"(?<=\s)Exportable\s*=\s*""false""");

    /// <summary>The guard reads what it claims to read: the findings table exports with its reader and
    /// its name, and the secret-bearing tables are the opted-out ones.</summary>
    [Fact]
    public void TheGuard_SeesTheFindingsTableAndTheOptOuts()
    {
        var grids = Grids();
        var findings = Assert.Single(grids, grid => grid.File.EndsWith("ProjectQualitySection.razor", StringComparison.Ordinal));
        Assert.False(findings.OptedOut);
        Assert.Equal("@ReadFindingsAsync", ValueOf(findings.Tag, "ExportRead"));
        Assert.Equal("@L[\"AnalysisFindings\"]", ValueOf(findings.Tag, "ExportName"));
        Assert.Equal(OptOuts.Count, grids.Count(grid => grid.OptedOut));
        Assert.True(findings.OffersMarkdown);
        Assert.False(Assert.Single(grids, grid => grid.File.EndsWith("Users/Users.razor", StringComparison.Ordinal)).OffersMarkdown);
        Assert.Equal(MarkdownTables.Count, grids.Count(grid => grid.OffersMarkdown));
    }
}
