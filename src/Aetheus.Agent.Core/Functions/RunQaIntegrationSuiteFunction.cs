// SPDX-License-Identifier: EUPL-1.2
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>run-qa-integration-suite</c> function, port of
/// <c>deploy/scripts/run-qa-integration-suite.sh</c>: runs the prebuilt integration test project of a
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
/// <item>The TRX is classified by <see cref="DotnetTestResultClassifier"/> (the rule of
/// <c>classify-dotnet-test-result.mjs</c>): unusable evidence fails the step, failures are status 1,
/// published as <c>INTEGRATION_SUITE_STATUS</c> where the script exited 10, with the failed names in
/// <c>failed-tests.txt</c>.</item>
/// </list>
/// Differences, deliberate: the test project and the static web assets manifest are parameters instead
/// of the Aetheus paths, and no Node.js is resolved, since nothing here runs in Node any more.
/// </summary>
public sealed partial class RunQaIntegrationSuiteFunction : IPipelineFunction
{
    private const string Configuration = "Release";
    private const string TrxName = "integration.trx";

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

        var test = Dotnet(context, sdk.Executable, suite.SourceRoot, TestArguments(suite));
        var rawExitCode = await FunctionProgress
            .RunAsync(_tools, context, $"Integration tests ({suite.Label})", interval.Value, test, cancellationToken)
            .ConfigureAwait(false);
        return await GradeAsync(context, suite, rawExitCode, cancellationToken).ConfigureAwait(false);
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
    /// Microsoft.Testing.Platform (<c>grep -q</c>), else VSTest.</summary>
    internal static string[] TestArguments(Suite suite)
    {
        var globalJson = Path.Combine(suite.SourceRoot, "global.json");
        var testingPlatform = File.Exists(globalJson)
            && File.ReadAllText(globalJson).Contains("\"Microsoft.Testing.Platform\"", StringComparison.Ordinal);
        return testingPlatform
            ?
            [
                "test", "--project", suite.TestProject, "--configuration", Configuration, "--no-build", "--no-restore",
                "--no-progress", "--report-trx", "--report-trx-filename", TrxName, "--results-directory", suite.Results
            ]
            :
            [
                "test", suite.TestProject, "--configuration", Configuration, "--no-build", "--no-restore",
                "--logger", "console;verbosity=minimal", "--logger", $"trx;LogFileName={TrxName}",
                "--results-directory", suite.Results
            ];
    }

    private static async Task<PipelineFunctionResult> GradeAsync(
        PipelineFunctionContext context, Suite suite, int rawExitCode, CancellationToken cancellationToken)
    {
        var trx = Path.Combine(suite.Results, TrxName);
        var verdict = DotnetTestResultClassifier.Classify(rawExitCode, trx, null, new PhysicalFileSystemReader());
        if (!verdict.IsValue)
            return PipelineFunctionResult.Failure($"The integration test evidence cannot be graded: {verdict.Failure.Reason}");

        var status = verdict.Value.ToString(CultureInfo.InvariantCulture);
        await context.Log($"Integration suite {suite.Label} produced an executed-test proof with findings status {status}.").ConfigureAwait(false);
        // The failed names, published beside the gate status so the failing checks are named directly.
        if (verdict.Value != 0)
            await FailedTestNames.RecordAsync(context, trx, Path.Combine(suite.Results, "failed-tests.txt"), cancellationToken).ConfigureAwait(false);
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
        string Label, string SourceRoot, string TestProject, string Results, string? StaticWebAssetsManifest, string? StaticWebAssetsProject)
    {
        private string ProjectName => Path.GetFileName(TestProject.Replace('\\', '/').TrimEnd('/'));

        public string ProjectFile => Path.Combine(SourceRoot, TestProject, ProjectName + ".csproj");

        public string NuGetProps => Path.Combine(SourceRoot, TestProject, "obj", ProjectName + ".csproj.nuget.g.props");

        public static Suite From(PipelineFunctionContext context)
        {
            var label = context.Parameters["label"];
            return new Suite(
                label,
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(context.Parameters["source_root"], context.Workspace!)),
                context.Parameters["test_project"],
                Path.Combine(context.Workspace!, ".qa-test-results", label),
                Optional(context, "static_web_assets_manifest"),
                Optional(context, "static_web_assets_project"));
        }

        public string? Refusal()
        {
            if (!RunQaDeployedProbesFunction.Label().IsMatch(Label))
                return "Invalid integration result label.";
            if (!File.Exists(ProjectFile))
                return $"The test project is missing: {ProjectFile}";
            return (StaticWebAssetsManifest is null) == (StaticWebAssetsProject is null)
                ? null
                : "static_web_assets_manifest and static_web_assets_project go together.";
        }

        private static string? Optional(PipelineFunctionContext context, string name) =>
            context.Parameters.GetValueOrDefault(name) is { Length: > 0 } value ? value : null;
    }
}
