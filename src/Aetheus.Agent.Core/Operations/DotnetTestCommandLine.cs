// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Text.Json;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The <c>dotnet test</c> invocation of a <c>dotnet-test</c> step, for whichever runner the
/// repository selected.
/// <para>
/// Since .NET 10 a repository chooses its test runner in <c>global.json</c> (<c>"test": { "runner":
/// "Microsoft.Testing.Platform" }</c>), and the two do not share a command line: VSTest takes the
/// project as a bare argument, <c>--logger trx</c> and <c>--collect</c>; Microsoft.Testing.Platform
/// refuses those and takes <c>--project</c>, <c>--report-trx</c> and <c>--coverlet</c>. A step that
/// always spoke VSTest could not run a single suite of this repository, whose own
/// <c>deploy/scripts/run-unit-suite.sh</c> speaks the platform's dialect.
/// </para>
/// </summary>
internal static class DotnetTestCommandLine
{
    internal const string TestingPlatformRunner = "Microsoft.Testing.Platform";

    /// <summary>coverlet.MTP stamps its report (<c>coverage.cobertura.&lt;digits&gt;.xml</c>).</summary>
    private const string StampedCoveragePrefix = "coverage.cobertura.";

    private const string CoverageFileName = "coverage.cobertura.xml";

    /// <summary>
    /// Whether <c>dotnet test</c>, started in <paramref name="workingDirectory"/>, runs on
    /// Microsoft.Testing.Platform. The SDK reads the first <c>global.json</c> found walking up from
    /// its working directory, and so does this. A file without a runner selects VSTest, the SDK's own
    /// default; a file that is not JSON throws, as the SDK would refuse it too.
    /// </summary>
    internal static bool UsesTestingPlatform(string workingDirectory)
    {
        for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
        {
            var globalJson = Path.Combine(directory.FullName, "global.json");
            if (!File.Exists(globalJson)) continue;
            return string.Equals(ReadRunner(globalJson), TestingPlatformRunner, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    private static string? ReadRunner(string globalJson)
    {
        using var document = JsonDocument.Parse(
            File.ReadAllText(globalJson),
            new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("test", out var test)
            && test.ValueKind == JsonValueKind.Object
            && test.TryGetProperty("runner", out var runner)
            && runner.ValueKind == JsonValueKind.String
                ? runner.GetString()
                : null;
    }

    internal static ProcessStartInfo Build(PipelineDotnetTestRequest request, bool testingPlatform)
    {
        ArgumentNullException.ThrowIfNull(request);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.DotnetPath,
            WorkingDirectory = request.Workspace,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        SetDotnetRoot(startInfo, request.DotnetPath);
        PrependToolPaths(startInfo, request.ToolPaths);
        if (testingPlatform) AddTestingPlatformArguments(startInfo.ArgumentList, request);
        else AddVsTestArguments(startInfo.ArgumentList, request);
        return startInfo;
    }

    /// <summary>
    /// A pinned SDK passed by absolute path is not the one the runner host would find on its own: the
    /// test host looks for its runtime through DOTNET_ROOT, then PATH. Pointing both at the SDK's own
    /// directory is what run-unit-suite.sh does, and without it a host with no system-wide .NET cannot
    /// start the test application at all.
    /// </summary>
    private static void SetDotnetRoot(ProcessStartInfo startInfo, string dotnetPath)
    {
        if (!Path.IsPathFullyQualified(dotnetPath)) return;
        var root = Path.GetDirectoryName(dotnetPath);
        if (string.IsNullOrEmpty(root)) return;
        startInfo.Environment["DOTNET_ROOT"] = root;
        var path = startInfo.Environment.TryGetValue("PATH", out var inherited) ? inherited : null;
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(path) ? root : root + Path.PathSeparator + path;
    }

    /// <summary>
    /// The tool directories go before everything else on PATH, the SDK's included: run-unit-suite.sh
    /// put the pinned Node.js first for the same reason, so a test that starts <c>node</c> runs the
    /// delivery toolchain and not whatever the host installed.
    /// </summary>
    private static void PrependToolPaths(ProcessStartInfo startInfo, IReadOnlyList<string> toolPaths)
    {
        if (toolPaths.Count == 0) return;
        var prefix = string.Join(Path.PathSeparator, toolPaths);
        var path = startInfo.Environment.TryGetValue("PATH", out var inherited) ? inherited : null;
        startInfo.Environment["PATH"] = string.IsNullOrEmpty(path) ? prefix : prefix + Path.PathSeparator + path;
    }

    private static void AddTestingPlatformArguments(ICollection<string> arguments, PipelineDotnetTestRequest request)
    {
        arguments.Add("test");
        arguments.Add("--project");
        arguments.Add(request.Project);
        arguments.Add("--configuration");
        arguments.Add(request.Configuration);
        arguments.Add("--no-build");
        arguments.Add("--no-progress");
        arguments.Add("--results-directory");
        arguments.Add(request.ResultsDirectory);
        arguments.Add("--report-trx");
        arguments.Add("--report-trx-filename");
        arguments.Add(request.TrxName);
        // coverlet.MTP takes its report format from the project's testconfig.json; it has no
        // command-line switch for it. A configuration that produces no Cobertura report is caught by
        // the classifier, which requires one.
        if (request.CollectCoverage)
            arguments.Add("--coverlet");
        if (request.RunSettings is { } runSettings)
        {
            arguments.Add("--settings");
            arguments.Add(runSettings);
        }
    }

    private static void AddVsTestArguments(ICollection<string> arguments, PipelineDotnetTestRequest request)
    {
        arguments.Add("test");
        arguments.Add(request.Project);
        arguments.Add("--configuration");
        arguments.Add(request.Configuration);
        arguments.Add("--no-build");
        if (request.CollectCoverage)
            arguments.Add("--collect:XPlat Code Coverage");
        arguments.Add("--results-directory");
        arguments.Add(request.ResultsDirectory);
        if (request.RunSettings is { } runSettings)
        {
            arguments.Add("--settings");
            arguments.Add(runSettings);
        }
        arguments.Add("--logger");
        arguments.Add("console;verbosity=minimal");
        arguments.Add("--logger");
        arguments.Add($"trx;LogFileName={request.TrxName}");
    }

    /// <summary>
    /// Gives each stamped coverlet.MTP report the layout coverlet.collector wrote, exactly as
    /// deploy/scripts/normalize-coverage-report.sh does: <c>coverage.cobertura.&lt;digits&gt;.xml</c>
    /// moves to <c>&lt;digits&gt;/coverage.cobertura.xml</c>. The classifier, and every later reader of
    /// the evidence, look for that exact name. A directory with no stamped report is left alone:
    /// whether coverage is missing is the classifier's verdict to give.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeCoverageReports(string resultsDirectory)
    {
        var moved = new List<string>();
        if (!Directory.Exists(resultsDirectory)) return moved;
        foreach (var report in Directory.GetFiles(resultsDirectory, StampedCoveragePrefix + "*.xml", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(report);
            if (name.Length <= StampedCoveragePrefix.Length + ".xml".Length) continue;
            var stamp = name[StampedCoveragePrefix.Length..^".xml".Length];
            if (stamp.Length == 0 || !stamp.All(char.IsAsciiDigit)) continue;
            var destination = Path.Combine(resultsDirectory, stamp, CoverageFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(report, destination);
            moved.Add(destination);
        }
        return moved;
    }
}
