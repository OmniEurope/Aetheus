// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>run-qa-integration-suite</c> function, port of
/// the former <c>run-qa-integration-suite.sh</c> script: runs the prebuilt integration test project of a
/// restored source tree (V, or V-1 under <c>.nminus1-source</c>) and grades it.
/// <list type="bullet">
/// <item>The SDK is the one the workspace's <c>global.json</c> pins (<see cref="PinnedDotnetSdk"/>, the
/// script's <c>ensure-dotnet-sdk.sh</c>), with <c>DOTNET_ROOT</c> and <c>PATH</c> pointing at it.</item>
/// <item>The static web assets manifest, when given, is rebased onto the source tree
/// (<see cref="StaticWebAssetsManifest"/>).</item>
/// <item>The locked graph is restored into this host's cache only when the package folder the restored
/// <c>obj/</c> names is absent here (a <c>$(...)</c> MSBuild reference is left alone); the binaries are
/// never rebuilt.</item>
/// <item>The tested source picks the runner: Microsoft.Testing.Platform when its <c>global.json</c>
/// names it, VSTest otherwise; both write <c>.qa-test-results/&lt;label&gt;/integration.trx</c>.</item>
/// <item>The run may select its tests: <c>test_filter</c> <c>Category=RollbackRelevant</c> (xunit's
/// <c>--filter-trait</c>, or VSTest's <c>--filter</c>), or <c>test_methods</c>, fully qualified methods
/// for a source tree that predates the category (the rerun's method filter). Each parameter falls back
/// to a run variable named after the label (<see cref="Suite.FilterVariable"/>), which a control plane whose
/// catalog predates the parameters still hands on.</item>
/// <item>The TRX is classified by <see cref="DotnetTestResultClassifier"/> (the rule of
/// <c>classify-dotnet-test-result.mjs</c>): unusable evidence fails the step, failures are status 1,
/// published as <c>INTEGRATION_SUITE_STATUS</c> where the script exited 10, with the failed names in
/// <c>failed-tests.txt</c>.</item>
/// <item>Failures are rerun once, alone (<see cref="FailedTestRerun"/>), into
/// <c>integration-rerun.trx</c> beside the first TRX: a test that passes on the rerun is flaky, named
/// in the log and in <c>flaky-tests.txt</c>, and does not fail the suite on its own (owner decision of 2026-11-05,
/// PLAN-006; the assurance contract grades the candidate on how many there are): the status and
/// <c>failed-tests.txt</c> keep only the tests that failed on both attempts.</item>
/// </list>
/// Differences, deliberate: the test project and the static web assets manifest are parameters instead
/// of the Aetheus paths, and no Node.js is resolved, since nothing here runs in Node any more.
/// </summary>
public sealed partial class RunQaIntegrationSuiteFunction : IPipelineFunction
{
    private const string Configuration = "Release";
    private const string TrxName = "integration.trx";
    private const string RerunTrxName = "integration-rerun.trx";

    /// <summary>The MSBuild property, read from the environment, whose arguments <c>dotnet test</c> hands
    /// to a Microsoft.Testing.Platform test application.</summary>
    internal const string TransitionSelectionProperty = "TestingPlatformCommandLineArguments";

    private readonly IPipelineFunctionTools _tools;
    private readonly PinnedDotnetSdk _sdk;

    public RunQaIntegrationSuiteFunction()
        : this(PipelineFunctionTools.Default)
    {
    }

    internal RunQaIntegrationSuiteFunction(IPipelineFunctionTools tools, PinnedDownload? installer = null)
    {
        _tools = tools;
        _sdk = new PinnedDotnetSdk(tools, installer ?? PinnedDotnetSdk.OfficialInstaller);
    }

    [GeneratedRegex("^.*<NuGetPackageRoot[^>]*>(.*)</NuGetPackageRoot>.*$")]
    private static partial Regex PackageRootLine();

    public string Name => PipelineFunctionCatalog.RunQaIntegrationSuite.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrWhiteSpace(context.Workspace))
            return PipelineFunctionResult.Failure("The workspace is not set.");
        var suite = Suite.From(context);
        if (suite.Refusal() is { } refusal)
            return PipelineFunctionResult.Failure(refusal);
        var interval = FunctionProgress.Interval(context);
        if (interval is null)
            return PipelineFunctionResult.Failure($"{FunctionProgress.IntervalVariable} must be a positive integer.");

        var sdk = await _sdk.ResolveAsync(context, context.Workspace, "global.json", cancellationToken).ConfigureAwait(false);
        if (sdk.Executable is null)
            return PipelineFunctionResult.Failure(sdk.Error!);

        if (Directory.Exists(suite.Results))
            Directory.Delete(suite.Results, recursive: true);
        Directory.CreateDirectory(suite.Results);
        if (await RestoreWhenAbsentAsync(context, suite, sdk.Executable, cancellationToken).ConfigureAwait(false) is { } restoreError)
            return PipelineFunctionResult.Failure(restoreError);
        // After the restore guard: the package content roots are rebased onto the cache it filled.
        if (await RebaseStaticWebAssetsAsync(context, suite).ConfigureAwait(false) is { } rebaseError)
            return PipelineFunctionResult.Failure(rebaseError);

        // Ryuk (privileged, Docker socket) raises a Critical container alert on every run: it is disabled for
        // this step and the scope removes the containers of this run, and the stale ones of a killed run.
        var testcontainers = new TestcontainersRunScope(_tools, context);
        await testcontainers.RemoveStaleAsync(cancellationToken).ConfigureAwait(false);
        var run = new TestRun(context, sdk.Executable, suite, testcontainers, interval.Value);
        await context.Log($"Integration suite {suite.Label} test filter: {suite.Selection}.").ConfigureAwait(false);
        try
        {
            var rawExitCode = await RunTestsAsync(run, $"Integration tests ({suite.Label})", TestArguments(suite), cancellationToken)
                .ConfigureAwait(false);
            var trx = Path.Combine(suite.Results, TrxName);
            var verdict = DotnetTestResultClassifier.Classify(rawExitCode, trx, null, new PhysicalFileSystemReader());
            if (!verdict.IsValue)
                return PipelineFunctionResult.Failure($"The integration test evidence cannot be graded: {verdict.Failure.Reason}");
            // The rerun shares the Testcontainers scope of the first attempt: its containers are removed with them.
            var rerun = verdict.Value == 0 ? null : await RerunAsync(run, trx, cancellationToken).ConfigureAwait(false);
            return await GradeAsync(context, suite, trx, verdict.Value, rerun, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await testcontainers.RemoveOwnAsync().ConfigureAwait(false);
        }
    }

    private sealed record TestRun(
        PipelineFunctionContext Context, string Dotnet, Suite Suite, TestcontainersRunScope Testcontainers, TimeSpan Interval);

    private Task<int> RunTestsAsync(TestRun run, string title, string[] arguments, CancellationToken cancellationToken)
    {
        var test = Dotnet(run.Context, run.Dotnet, run.Suite.SourceRoot, arguments);
        foreach (var (name, value) in run.Testcontainers.Variables)
            test.Environment[name] = value;
        // The suite's own selection is the only one: the MSBuild property through which a pipeline hands
        // the same selection to an agent that predates test_filter would add to it (the rerun's methods
        // then widened back to the whole selection).
        if (run.Suite.HasSelection)
            test.Environment.Remove(TransitionSelectionProperty);
        return FunctionProgress.RunAsync(_tools, run.Context, title, run.Interval, test, cancellationToken);
    }

    /// <summary>The failed tests of the first attempt run once more into <c>integration-rerun.trx</c>, or
    /// null when nothing is rerun or the rerun left no gradable evidence (the first verdict stands).</summary>
    private async Task<FailedTestRerun.Verdict?> RerunAsync(TestRun run, string trx, CancellationToken cancellationToken)
    {
        var description = $"Integration suite {run.Suite.Label}";
        if (await FailedTestRerun.PlanAsync(run.Context.Log, description, trx).ConfigureAwait(false) is not { } plan)
            return null;
        var rerunTrx = Path.Combine(run.Suite.Results, RerunTrxName);
        var rawExitCode = await RunTestsAsync(run, $"Integration tests rerun ({run.Suite.Label})",
            TestArguments(suite: run.Suite, trxName: RerunTrxName, rerunMethods: plan.Methods), cancellationToken).ConfigureAwait(false);
        var verdict = DotnetTestResultClassifier.Classify(rawExitCode, rerunTrx, null, new PhysicalFileSystemReader());
        if (verdict.IsValue)
            return await FailedTestRerun.ReconcileAsync(run.Context.Log, description, plan, rerunTrx).ConfigureAwait(false);
        await run.Context.Log($"{description}: the rerun evidence cannot be graded ({verdict.Failure.Reason}); the first attempt's failures stand.",
            TaskLogLevel.Warning).ConfigureAwait(false);
        return null;
    }

    private static async Task<string?> RebaseStaticWebAssetsAsync(PipelineFunctionContext context, Suite suite)
    {
        if (suite.StaticWebAssetsManifest is null)
            return null;
        var manifest = Path.GetFullPath(suite.StaticWebAssetsManifest, suite.SourceRoot);
        var (count, error) = StaticWebAssetsManifest.Rebase(
            suite.SourceRoot, manifest, suite.StaticWebAssetsProject!, HostPackageRoot(context, suite));
        if (error is null)
            await context.Log(string.Create(CultureInfo.InvariantCulture, $"Rebased {count} static web assets content root(s) in {manifest}."))
                .ConfigureAwait(false);
        return error;
    }

    /// <summary>This host's NuGet package folder, where the manifest's package content roots are rebased:
    /// the one the restored <c>obj/</c> names once the restore guard ran, when it is a real folder here;
    /// else <c>NUGET_PACKAGES</c>, else <c>$HOME/.nuget/packages</c> (the SDK's own default).</summary>
    internal static string? HostPackageRoot(PipelineFunctionContext context, Suite suite)
    {
        var restored = RestoredPackageRoot(suite.NuGetProps);
        if (restored.Length > 0 && !restored.Contains("$(", StringComparison.Ordinal) && Directory.Exists(restored))
            return restored;
        if (FunctionShell.Variable(context, "NUGET_PACKAGES") is { } configured)
            return configured;
        return FunctionShell.Variable(context, "HOME") is { } home ? Path.Combine(home, ".nuget", "packages") : null;
    }

    /// <summary>The script's restore guard: restored <c>obj/</c> files can name another runner's package
    /// cache and hide test-platform imports; restoring the locked graph here keeps the binaries
    /// unchanged.</summary>
    private async Task<string?> RestoreWhenAbsentAsync(
        PipelineFunctionContext context, Suite suite, string dotnet, CancellationToken cancellationToken)
    {
        var restoredRoot = RestoredPackageRoot(suite.NuGetProps);
        if (restoredRoot.Contains("$(", StringComparison.Ordinal)
            || (restoredRoot.Length > 0 && Directory.Exists(restoredRoot)))
            return null;
        await context.Log($"The package folder the restored obj/ names ('{restoredRoot}') is absent here; restoring the locked graph into this host's cache.")
            .ConfigureAwait(false);
        var restore = Dotnet(context, dotnet, suite.SourceRoot,
            "restore", suite.TestProject, $"-p:Configuration={Configuration}", "--locked-mode", "--verbosity", "minimal");
        var result = await _tools.RunAsync(restore, (line, level) => context.Log(line, level), cancellationToken).ConfigureAwait(false);
        return result.ExitCode == 0 && !result.TimedOut
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"dotnet restore of {suite.TestProject} failed with exit code {result.ExitCode}.");
    }

    /// <summary><c>sed -n 's:.*&lt;NuGetPackageRoot[^&gt;]*&gt;\(.*\)&lt;/NuGetPackageRoot&gt;.*:\1:p' | head -n 1</c>;
    /// empty when the file or the element is absent.</summary>
    internal static string RestoredPackageRoot(string nugetProps) =>
        File.Exists(nugetProps)
            ? File.ReadLines(nugetProps).Select(line => PackageRootLine().Match(line)).FirstOrDefault(match => match.Success)?.Groups[1].Value
              ?? string.Empty
            : string.Empty;

    /// <summary>The runner the tested source selects: its <c>global.json</c> naming
    /// Microsoft.Testing.Platform (<c>grep -q</c>), else VSTest. A rerun selects its test methods with
    /// the runner's own filter: xunit's <c>--filter-method</c> on the testing platform, a
    /// <c>FullyQualifiedName</c> expression on VSTest (<see cref="FailedTestRerun"/>).</summary>
    internal static string[] TestArguments(Suite suite, string trxName = TrxName, IReadOnlyList<string>? rerunMethods = null)
    {
        var globalJson = Path.Combine(suite.SourceRoot, "global.json");
        var testingPlatform = File.Exists(globalJson)
            && File.ReadAllText(globalJson).Contains("\"Microsoft.Testing.Platform\"", StringComparison.Ordinal);
        string[] arguments = testingPlatform
            ?
            [
                "test", "--project", suite.TestProject, "--configuration", Configuration, "--no-build", "--no-restore",
                "--no-progress", "--report-trx", "--report-trx-filename", trxName, "--results-directory", suite.Results
            ]
            :
            [
                "test", suite.TestProject, "--configuration", Configuration, "--no-build", "--no-restore",
                "--logger", "console;verbosity=minimal", "--logger", $"trx;LogFileName={trxName}",
                "--results-directory", suite.Results
            ];
        // The rerun's methods already lie inside the selection, so it carries only them.
        if ((rerunMethods ?? suite.TestMethods) is { } methods)
            return testingPlatform
                ? [.. arguments, .. FailedTestRerun.TestingPlatformFilter(methods)]
                : [.. arguments, "--filter", FailedTestRerun.VsTestFilter(methods)];
        if (suite.TestFilter is null)
            return arguments;
        // xunit's trait filter on the testing platform, the Category expression on VSTest (xunit and
        // NUnit both expose their category there).
        return testingPlatform
            ? [.. arguments, "--filter-trait", suite.TestFilter]
            : [.. arguments, "--filter", suite.TestFilter];
    }

    /// <summary>The status over both attempts when the failed tests were rerun, else the first one's;
    /// the failed names, and the flaky ones, recorded beside it so they are named directly.</summary>
    private static async Task<PipelineFunctionResult> GradeAsync(
        PipelineFunctionContext context, Suite suite, string trx, int firstStatus, FailedTestRerun.Verdict? rerun,
        CancellationToken cancellationToken)
    {
        var status = (rerun?.Status ?? firstStatus).ToString(CultureInfo.InvariantCulture);
        await context.Log($"Integration suite {suite.Label} produced an executed-test proof with findings status {status}.").ConfigureAwait(false);
        if (rerun is not null)
            await FailedTestRerun.RecordAsync(context.Log, $"Integration suite {suite.Label}", suite.Results, rerun, cancellationToken).ConfigureAwait(false);
        else if (firstStatus != 0)
            await FailedTestNames.RecordAsync(context.Log, trx, Path.Combine(suite.Results, FailedTestRerun.FailedList), cancellationToken).ConfigureAwait(false);
        context.SetOutput("INTEGRATION_SUITE_STATUS", status);
        return PipelineFunctionResult.Success();
    }

    /// <summary><c>"$DOTNET" args</c> with the script's <c>DOTNET_ROOT</c> and <c>PATH</c>.</summary>
    private static ProcessStartInfo Dotnet(PipelineFunctionContext context, string dotnet, string workingDirectory, params string[] arguments)
    {
        var startInfo = FunctionShell.StartInfo(context, dotnet, workingDirectory, arguments);
        var root = Path.GetDirectoryName(dotnet)!;
        startInfo.Environment["DOTNET_ROOT"] = root;
        startInfo.Environment["PATH"] = FunctionShell.Variable(context, "PATH") is { } path ? root + Path.PathSeparator + path : root;
        return startInfo;
    }

    internal sealed record Suite(
        string Label, string SourceRoot, string TestProject, string Results, string? StaticWebAssetsManifest, string? StaticWebAssetsProject,
        string? TestFilter = null, IReadOnlyList<string>? TestMethods = null)
    {
        /// <summary>The only test filter the suite takes, as the E2E suite's <c>test_filter</c>.</summary>
        internal const string RollbackRelevantFilter = E2eRun.RollbackRelevantFilter;

        private string ProjectName => Path.GetFileName(TestProject.Replace('\\', '/').TrimEnd('/'));

        public string ProjectFile => Path.Combine(SourceRoot, TestProject, ProjectName + ".csproj");

        public string NuGetProps => Path.Combine(SourceRoot, TestProject, "obj", ProjectName + ".csproj.nuget.g.props");

        /// <summary>The run variable that stands in for the <c>test_filter</c> parameter of one result label,
        /// for instance <c>QA_INTEGRATION_TEST_FILTER_PREVIOUS</c>: a control plane whose catalog predates the
        /// parameter refuses it in <c>with:</c>, while a variable reaches the function through any of them,
        /// and its label keeps it from selecting the tests of another suite of the same run.</summary>
        internal static string FilterVariable(string label) => "QA_INTEGRATION_TEST_FILTER_" + VariableSuffix(label);

        /// <summary>The run variable that stands in for the <c>test_methods</c> parameter of one result label
        /// (<see cref="FilterVariable"/>).</summary>
        internal static string MethodsVariable(string label) => "QA_INTEGRATION_TEST_METHODS_" + VariableSuffix(label);

        private static string VariableSuffix(string label) => label.ToUpperInvariant().Replace('-', '_');

        public static Suite From(PipelineFunctionContext context)
        {
            var label = context.Parameters["label"];
            var methods = FunctionShell.Input(context, "test_methods", MethodsVariable(label))
                ?.Split([' ', '\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries);
            return new Suite(
                label,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.Parameters["source_root"], context.Workspace!)),
                context.Parameters["test_project"],
                Path.Combine(context.Workspace!, ".qa-test-results", label),
                Optional(context, "static_web_assets_manifest"),
                Optional(context, "static_web_assets_project"),
                FunctionShell.Input(context, "test_filter", FilterVariable(label)),
                methods is { Length: > 0 } ? methods : null);
        }

        public string? Refusal()
        {
            if (!RunQaDeployedProbesFunction.Label().IsMatch(Label))
                return "Invalid integration result label.";
            if (SelectionRefusal() is { } selection)
                return selection;
            if (!File.Exists(ProjectFile))
                return $"The test project is missing: {ProjectFile}";
            return (StaticWebAssetsManifest is null) == (StaticWebAssetsProject is null)
                ? null
                : "static_web_assets_manifest and static_web_assets_project go together.";
        }

        /// <summary>The tests to run: all of them, the RollbackRelevant category, or named methods for a
        /// source tree that predates the category; never both, and never a method a filter cannot carry
        /// verbatim.</summary>
        private string? SelectionRefusal()
        {
            if (TestFilter is not null && TestMethods is not null)
                return "test_filter and test_methods are exclusive.";
            if (TestFilter is not null && TestFilter != RollbackRelevantFilter)
                return $"test_filter must be empty or {RollbackRelevantFilter}.";
            return TestMethods?.FirstOrDefault(method => !FailedTestRerun.IsFilterableMethod(method)) is { } invalid
                ? $"test_methods names a method a filter cannot carry: '{invalid}'."
                : null;
        }

        public bool HasSelection => TestFilter is not null || TestMethods is not null;

        /// <summary>The selection, said in the log before the run.</summary>
        public string Selection =>
            TestFilter ?? (TestMethods is { } methods
                ? string.Create(CultureInfo.InvariantCulture, $"{methods.Count} named test method(s): {string.Join(", ", methods)}")
                : "none, every test runs");

        private static string? Optional(PipelineFunctionContext context, string name) =>
            context.Parameters.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
    }
}
