// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;
using Aetheus.Agent.Core.Functions;
using Aetheus.Shared.Components.Pipelines;

namespace Aetheus.Agent.Core.Tests;

/// <summary>
/// The C# port of <c>deploy/scripts/run-affected-dotnet-format.sh</c>, run through the real executor
/// on a temp workspace unique to each test, with Git and dotnet answered by fakes: the fail-closed
/// scope decision, the <c>dotnet restore</c> and <c>dotnet format</c> command lines, the published
/// mode and status, and the refusals of missing or inconsistent evidence.
/// </summary>
public sealed class RunAffectedDotnetFormatFunctionTests : IDisposable
{
    private const string Baseline = "1111111111111111111111111111111111111111";
    private const string Head = "2222222222222222222222222222222222222222";
    private const string OneFinding =
        "[{\"FilePath\":\"src/A.cs\",\"FileChanges\":[{\"LineNumber\":3,\"CharNumber\":5,\"DiagnosticId\":\"WHITESPACE\",\"FormatDescription\":\"Fix whitespace formatting.\"}]}]";

    private readonly FunctionTestHarness _harness = new("aetheus-format");
    private readonly FakeFunctionTools _tools = new();
    private readonly Dictionary<string, string> _env = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _parameters = new(StringComparer.Ordinal) { ["solution"] = "App.slnx" };

    public RunAffectedDotnetFormatFunctionTests()
    {
        _env["PATH"] = Path.Combine(_harness.Root, "bin");
        _env["DELIVERY_BASELINE_SOURCE_SHA"] = Baseline;
        _env["BUILD_SOURCEVERSION"] = Head;
        _tools.OnRun = Answer;
    }

    public void Dispose() => _harness.Dispose();

    private int CatFileExit { get; set; }
    private int MergeBaseExit { get; set; }
    private int DiffExit { get; set; }
    private string[] DiffLines { get; set; } = ["M\tsrc/A.cs", "A\tsrc/Pages/B.razor", "M\tREADME.md"];
    private int RestoreExit { get; set; }
    private int WhitespaceExit { get; set; }
    private string? WhitespaceReport { get; set; } = "[]";
    private int StyleExit { get; set; }
    private string? StyleReport { get; set; } = "[]";

    private int FormatExit
    {
        set => (WhitespaceExit, StyleExit) = (value, value);
    }

    private string? FormatReport
    {
        set => (WhitespaceReport, StyleReport) = (value, value);
    }

    private Task<(int, string[])> Answer(ProcessStartInfo process, CancellationToken _)
    {
        var verb = process.ArgumentList.Count > 0 ? process.ArgumentList[0] : string.Empty;
        return Task.FromResult<(int, string[])>((Path.GetFileName(process.FileName), verb) switch
        {
            ("git", "cat-file") => (CatFileExit, []),
            ("git", "merge-base") => (MergeBaseExit, []),
            ("git", "diff") => (DiffExit, DiffLines),
            (_, "restore") => (RestoreExit, ["restored"]),
            (_, "format") => Format(process),
            _ => (127, [])
        });
    }

    private (int, string[]) Format(ProcessStartInfo process)
    {
        var reportDirectory = process.ArgumentList[process.ArgumentList.IndexOf("--report") + 1];
        var (exit, report) = process.ArgumentList[1] == "whitespace" ? (WhitespaceExit, WhitespaceReport) : (StyleExit, StyleReport);
        if (report is not null)
            File.WriteAllText(Path.Combine(reportDirectory, "format-report.json"), report);
        return (exit, ["formatted"]);
    }

    private Task<(Executors.ExecutorResult Result, Dictionary<string, string> Variables)> RunAsync()
    {
        _env[PipelineFunctionVariables.ParametersJson] = JsonSerializer.Serialize(_parameters);
        return _harness.RunAsync(new RunAffectedDotnetFormatFunction(_tools), _env);
    }

    private static string FailureReason(Executors.ExecutorResult result)
    {
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(PipelineFunctionOperationExecutor.FunctionError, result.FailureCode);
        return result.FailureReason!;
    }

    private IEnumerable<ProcessStartInfo> Runs(string verb) => _tools.Runs.Where(run => run.ArgumentList.Count > 0 && run.ArgumentList[0] == verb);

    private ProcessStartInfo FormatRun => Runs("format").First();

    private ProcessStartInfo SubcommandRun(string subcommand) => Assert.Single(Runs("format"), run => run.ArgumentList[1] == subcommand);

    private string ReportDirectory => Path.Combine(_harness.Workspace, ".analysis-format");

    private bool Logged(string line) => _harness.Log.Any(entry => entry.Line == line);

    [Fact]
    public async Task ChangedSources_AreTheOnlyFilesChecked()
    {
        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("affected", variables["DOTNET_FORMAT_MODE"]);
        Assert.Equal("0", variables["DOTNET_FORMAT_GATE_STATUS"]);
        Assert.Equal(["restore", "App.slnx", "--verbosity", "minimal"], Assert.Single(Runs("restore")).ArgumentList);
        Assert.Equal(["whitespace", "style"], Runs("format").Select(run => run.ArgumentList[1]));
        foreach (var subcommand in new[] { "whitespace", "style" })
        {
            Assert.Equal(
                ["format", subcommand, "App.slnx", "--no-restore", "--verify-no-changes", "--verbosity", "minimal",
                 "--include", "src/A.cs", "src/Pages/B.razor", "--report", Path.Combine(ReportDirectory, subcommand)],
                SubcommandRun(subcommand).ArgumentList);
            Assert.Equal(_harness.Workspace, SubcommandRun(subcommand).WorkingDirectory);
        }
        Assert.DoesNotContain(_tools.Runs, run => run.ArgumentList.Contains("analyzers"));
        Assert.Equal(["diff", "--name-status", "--find-renames", Baseline + ".." + Head], Assert.Single(Runs("diff")).ArgumentList);
        Assert.True(Logged("Formatting mode: affected (2 files)."));
        Assert.True(Logged("dotnet format findings status: 0 (0 finding(s)); SARIF evidence recorded."));
        Assert.True(File.Exists(Path.Combine(ReportDirectory, "dotnet-format.sarif")));
    }

    [Fact]
    public async Task RestoreFalse_ChecksWithoutRestoringTheSolution()
    {
        _parameters["restore"] = "false";
        RestoreExit = 1;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(Runs("restore"));
        Assert.Equal("affected", variables["DOTNET_FORMAT_MODE"]);
        Assert.Equal("0", variables["DOTNET_FORMAT_GATE_STATUS"]);
        Assert.Contains("--no-restore", FormatRun.ArgumentList);
        Assert.True(Logged("Restore skipped (restore: false): App.slnx was restored by the compilation of this workspace."));
    }

    [Fact]
    public async Task RestoreTrue_RestoresAsByDefault()
    {
        _parameters["restore"] = "true";

        var (result, _) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Single(Runs("restore"));
        Assert.Equal(2, Runs("format").Count());
    }

    [Fact]
    public async Task RestoreOtherThanTrueOrFalse_FailsBeforeAnyCommand()
    {
        _parameters["restore"] = "no";

        var (result, _) = await RunAsync();

        Assert.EndsWith("The restore parameter must be true or false.", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public async Task Findings_PublishStatusOneAndTheSarifLog()
    {
        WhitespaceExit = 2;
        WhitespaceReport = OneFinding;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["DOTNET_FORMAT_GATE_STATUS"]);
        Assert.True(Logged("dotnet format findings status: 1 (1 finding(s)); SARIF evidence recorded."));
        var sarif = await File.ReadAllTextAsync(Path.Combine(ReportDirectory, "dotnet-format.sarif"), TestContext.Current.CancellationToken);
        Assert.Contains("\"ruleId\":\"WHITESPACE\"", sarif, StringComparison.Ordinal);
        Assert.Contains("\"uri\":\"src/A.cs\"", sarif, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsInTheStyleReportOnly_PublishStatusOneAndAreMerged()
    {
        StyleExit = 2;
        StyleReport = OneFinding;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("1", variables["DOTNET_FORMAT_GATE_STATUS"]);
        Assert.Equal(2, Runs("format").Count());
        Assert.True(Logged("dotnet format findings status: 1 (1 finding(s)); SARIF evidence recorded."));
        Assert.Contains("\"FilePath\":\"src/A.cs\"", await File.ReadAllTextAsync(Path.Combine(ReportDirectory, "format-report.json"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindingsInBothReports_AreCountedTogether()
    {
        FormatExit = 2;
        FormatReport = OneFinding;

        var (_, variables) = await RunAsync();

        Assert.Equal("1", variables["DOTNET_FORMAT_GATE_STATUS"]);
        Assert.True(Logged("dotnet format findings status: 1 (2 finding(s)); SARIF evidence recorded."));
    }

    [Fact]
    public async Task MissingStyleReport_FailsEvenWhenWhitespaceIsClean()
    {
        StyleReport = null;

        var (result, variables) = await RunAsync();

        Assert.EndsWith("dotnet format did not produce its required JSON evidence.", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Fact]
    public async Task InconsistentStyleRun_FailsEvenWhenWhitespaceIsClean()
    {
        StyleExit = 2;

        var (result, _) = await RunAsync();

        Assert.EndsWith("dotnet format exit code and evidence are inconsistent.", FailureReason(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, OneFinding)]
    [InlineData(2, "[]")]
    public async Task ExitCodeAndReportDisagreeing_FailLikeTheScript(int exitCode, string report)
    {
        FormatExit = exitCode;
        FormatReport = report;

        var (result, variables) = await RunAsync();

        Assert.EndsWith("dotnet format exit code and evidence are inconsistent.", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(variables);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task MissingJsonReport_FailsLikeTheScript(string? report)
    {
        FormatExit = 2;
        FormatReport = report;

        var (result, _) = await RunAsync();

        Assert.EndsWith("dotnet format did not produce its required JSON evidence.", FailureReason(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidReport_FailsWithTheConvertersCause()
    {
        FormatExit = 2;
        FormatReport = "{}";

        var (result, _) = await RunAsync();

        Assert.EndsWith("The dotnet format report must be a JSON array.", FailureReason(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("BUILD_SOURCEVERSION", "")]
    [InlineData("BUILD_SOURCEVERSION", "abc")]
    [InlineData("BUILD_SOURCEVERSION", "zz22222222222222222222222222222222222222")]
    [InlineData("DELIVERY_BASELINE_SOURCE_SHA", "")]
    [InlineData("DELIVERY_BASELINE_SOURCE_SHA", "11111111111111111111111111111111111111111")]
    public async Task InvalidShas_CheckTheWholeSolutionWithoutAskingGit(string variable, string value)
    {
        _env[variable] = value;

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("full", variables["DOTNET_FORMAT_MODE"]);
        Assert.DoesNotContain("--include", FormatRun.ArgumentList);
        Assert.DoesNotContain(_tools.Runs, run => Path.GetFileName(run.FileName) == "git");
        Assert.True(Logged("Formatting mode: full (fail-closed fallback or explicit nightly mode)."));
    }

    [Fact]
    public async Task UnknownBaseline_ChecksTheWholeSolution()
    {
        CatFileExit = 1;

        var (_, variables) = await RunAsync();

        Assert.Equal("full", variables["DOTNET_FORMAT_MODE"]);
        Assert.Equal(["cat-file", "-e", Baseline + "^{commit}"], Assert.Single(Runs("cat-file")).ArgumentList);
        Assert.Empty(Runs("merge-base"));
    }

    [Fact]
    public async Task BaselineNotAnAncestor_ChecksTheWholeSolution()
    {
        MergeBaseExit = 1;

        var (_, variables) = await RunAsync();

        Assert.Equal("full", variables["DOTNET_FORMAT_MODE"]);
        Assert.Equal(["merge-base", "--is-ancestor", Baseline, Head], Assert.Single(Runs("merge-base")).ArgumentList);
        Assert.Empty(Runs("diff"));
    }

    [Fact]
    public async Task FailedDiff_ChecksTheWholeSolution()
    {
        DiffExit = 91;

        var (_, variables) = await RunAsync();

        Assert.Equal("full", variables["DOTNET_FORMAT_MODE"]);
        Assert.Single(Runs("diff"));
        Assert.DoesNotContain("--include", FormatRun.ArgumentList);
    }

    [Fact]
    public async Task DeletedFile_ChecksTheWholeSolution()
    {
        DiffLines = ["M\tsrc/A.cs", "D\tsrc/Old.cs"];

        var (_, variables) = await RunAsync();

        Assert.Equal("full", variables["DOTNET_FORMAT_MODE"]);
        Assert.DoesNotContain("--include", FormatRun.ArgumentList);
    }

    [Fact]
    public async Task NoChangedSource_PublishesAnEmptyResultWithoutFormatting()
    {
        DiffLines = ["M\tREADME.md"];

        var (result, variables) = await RunAsync();

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("affected-empty", variables["DOTNET_FORMAT_MODE"]);
        Assert.Equal("0", variables["DOTNET_FORMAT_GATE_STATUS"]);
        Assert.Empty(Runs("format"));
        Assert.Single(Runs("restore"));
        Assert.Equal("[]\n", await File.ReadAllTextAsync(Path.Combine(ReportDirectory, "format-report.json"), TestContext.Current.CancellationToken));
        Assert.Contains("\"results\":[]", await File.ReadAllTextAsync(Path.Combine(ReportDirectory, "dotnet-format.sarif"), TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.True(Logged("Formatting mode: affected; no changed C# or Razor file."));
    }

    [Fact]
    public async Task ExplicitFullMode_DoesNotAskGit()
    {
        _env["AETHEUS_FORMAT_MODE"] = "full";

        var (_, variables) = await RunAsync();

        Assert.Equal("full", variables["DOTNET_FORMAT_MODE"]);
        Assert.DoesNotContain(_tools.Runs, run => Path.GetFileName(run.FileName) == "git");
    }

    [Fact]
    public async Task ModeParameter_OverridesTheVariable()
    {
        _env["AETHEUS_FORMAT_MODE"] = "full";
        _parameters["mode"] = "affected";

        var (_, variables) = await RunAsync();

        Assert.Equal("affected", variables["DOTNET_FORMAT_MODE"]);
    }

    [Fact]
    public async Task InvalidMode_FailsLikeTheScriptBeforeRunningAnything()
    {
        _parameters["mode"] = "partial";

        var (result, _) = await RunAsync();

        Assert.EndsWith("AETHEUS_FORMAT_MODE must be affected or full.", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }

    [Fact]
    public async Task ShaParameters_OverrideTheVariables()
    {
        _env.Remove("DELIVERY_BASELINE_SOURCE_SHA");
        _env.Remove("BUILD_SOURCEVERSION");
        _parameters["baseline"] = Head;
        _parameters["head"] = Baseline;

        var (_, variables) = await RunAsync();

        Assert.Equal("affected", variables["DOTNET_FORMAT_MODE"]);
        Assert.Equal(["merge-base", "--is-ancestor", Head, Baseline], Assert.Single(Runs("merge-base")).ArgumentList);
    }

    [Fact]
    public async Task DotnetParameter_IsTheExecutableRun()
    {
        var pinned = Path.Combine(_harness.Root, "sdk", "dotnet");
        _env["DOTNET"] = "/elsewhere/dotnet";
        _parameters["dotnet"] = pinned;

        await RunAsync();

        Assert.All(Runs("restore").Concat(Runs("format")), run => Assert.Equal(pinned, run.FileName));
    }

    [Fact]
    public async Task DotnetVariable_IsUsedWithoutTheParameter()
    {
        _env["DOTNET"] = "/pinned/sdk/dotnet";

        await RunAsync();

        Assert.All(Runs("format"), run => Assert.Equal("/pinned/sdk/dotnet", run.FileName));
    }

    [Fact]
    public async Task FailedRestore_StopsBeforeFormatting()
    {
        RestoreExit = 1;

        var (result, variables) = await RunAsync();

        Assert.EndsWith("dotnet restore App.slnx failed with exit code 1.", FailureReason(result), StringComparison.Ordinal);
        Assert.Empty(Runs("format"));
        Assert.Empty(variables);
    }

    [Fact]
    public async Task StaleEvidence_IsRemovedBeforeTheRun()
    {
        _harness.Write("ws/.analysis-format/stale.txt", "old");

        await RunAsync();

        Assert.False(File.Exists(Path.Combine(ReportDirectory, "stale.txt")));
    }

    [Fact]
    public async Task MissingSolution_IsRefusedByTheCatalogContract()
    {
        _parameters.Remove("solution");

        var (result, _) = await RunAsync();

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("solution", result.FailureReason, StringComparison.Ordinal);
        Assert.Empty(_tools.Runs);
    }
}
