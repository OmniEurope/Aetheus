// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;
using System.Text;
using Aetheus.Agent.Core.Operations;

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>opengrep-rule-tests</c> function (PLAN-002 lot 8): proves the in-house SAST rules where they
/// are written, by running <c>opengrep scan --test --json</c> with the workspace's rule files on their
/// annotated test corpus (<c>ruleid:</c> must fire, <c>ok:</c> must stay silent).
///
/// It runs exactly the OpenGrep the SAST scan runs, and the same way (<see cref="OpenGrepSandbox"/>):
/// the binary of <c>scanner-manifest.json</c>, downloaded once into the agent's work directory
/// (<c>AETHEUS_AGENT_WORK_DIRECTORY</c>) and trusted only at its pinned SHA-256, started by Docker in
/// the manifest's runtime image pinned by digest, without network, on a read-only root, with the rules
/// and the corpus mounted read-only. The workspace rules are tested, not the ones embedded in the agent,
/// because the step proves the commit being built.
///
/// The step fails on any rule check that did not pass, any rule file no test file covers, any rule
/// file OpenGrep could not load, when no check ran at all, and when OpenGrep stopped without a report
/// (its last error lines are then the failure reason).
/// </summary>
public sealed class OpenGrepRuleTestsFunction : IPipelineFunction
{
    internal const string DefaultRules = ".aetheus/security-rules/opengrep";
    internal const string DefaultTests = "tests/security-rules";
    private const string StderrPrefix = "[stderr] ";
    private const string ExitLinePrefix = "[process] exit-code=";
    private const int ReportedErrorLines = 5;

    private readonly IPipelineFunctionTools _tools;
    private readonly Func<ScannerManifestEntry?> _findScanner;
    private readonly Func<string> _user;

    public OpenGrepRuleTestsFunction()
        : this(
            PipelineFunctionTools.Default,
            () => ScannerManifestCatalog.Find(OpenGrepSandbox.ScannerKey),
            () => $"{AgentUserIdentity.Uid}:{AgentUserIdentity.Gid}")
    {
    }

    /// <summary>Takes the manifest entry and the container identity from the caller, so tests can run
    /// where neither the agent manifest nor libc applies.</summary>
    internal OpenGrepRuleTestsFunction(IPipelineFunctionTools tools, Func<ScannerManifestEntry?> findScanner, Func<string> user)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _findScanner = findScanner ?? throw new ArgumentNullException(nameof(findScanner));
        _user = user ?? throw new ArgumentNullException(nameof(user));
    }

    public string Name => PipelineFunctionCatalog.OpenGrepRuleTests.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var setup = Prepare(context);
        if (setup.Failure is not null)
            return PipelineFunctionResult.Failure(setup.Failure);
        var (scanner, binaryPath, rules, tests) = (setup.Scanner!, setup.BinaryPath!, setup.Rules!, setup.Tests!);

        try
        {
            await OpenGrepSandbox.EnsureBinaryAsync(scanner, binaryPath, _tools.DownloadAsync, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException)
        {
            return PipelineFunctionResult.Failure($"OpenGrep {scanner.Version} is unavailable: {ex.Message}");
        }

        // The host stages' workspace can live in the agent's private /tmp, which the Docker daemon does
        // not see: the rules and the corpus are copied under the work directory, as the SAST scan
        // projects its sources there, and removed afterwards.
        var stagingRoot = Path.Combine(setup.WorkDirectory!, "scans", $"rule-tests-{Guid.NewGuid():N}");
        try
        {
            var staged = StageCopies(stagingRoot, rules, tests);
            if (staged is not null)
                return PipelineFunctionResult.Failure(staged);
            var process = BuildProcess(
                scanner, binaryPath, Path.Combine(stagingRoot, "rules"), Path.Combine(stagingRoot, "tests"), _user());
            process.FileName = setup.Docker!;
            await context.Log($"OpenGrep {scanner.Version} rule tests: rules {setup.RulesRelative}, tests {setup.TestsRelative}.")
                .ConfigureAwait(false);
            var output = await RunAsync(process, cancellationToken).ConfigureAwait(false);
            return await ConcludeAsync(context, output).ConfigureAwait(false);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
                Directory.Delete(stagingRoot, recursive: true);
        }
    }

    /// <summary>Copies the two directories under <paramref name="stagingRoot"/> (owner-only), regular
    /// files only: a symbolic link would let the corpus read a file outside the workspace. Null when
    /// done, the refusal otherwise.</summary>
    private static string? StageCopies(string stagingRoot, string rules, string tests)
    {
        Directory.CreateDirectory(stagingRoot);
        ScannerFileSecurity.TrySetOwnerOnly(stagingRoot);
        foreach (var (source, name) in new[] { (rules, "rules"), (tests, "tests") })
        {
            var destination = Path.Combine(stagingRoot, name);
            Directory.CreateDirectory(destination);
            foreach (var entry in new DirectoryInfo(source).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
            {
                if (entry.LinkTarget is not null)
                    return $"The {name} directory holds a symbolic link ({Path.GetRelativePath(source, entry.FullName)}); only regular files are tested.";
                var target = Path.Combine(destination, Path.GetRelativePath(source, entry.FullName));
                if (entry is DirectoryInfo)
                    Directory.CreateDirectory(target);
                else
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(entry.FullName, target);
                }
            }
        }
        return null;
    }

    /// <summary>The sandboxed <c>docker run</c>: the rules at <c>/rules</c> and the test corpus at
    /// <c>/tests</c>, both read-only, nothing else of the workspace.</summary>
    internal static System.Diagnostics.ProcessStartInfo BuildProcess(
        ScannerManifestEntry scanner, string binaryPath, string rulesDirectory, string testsDirectory, string user) =>
        OpenGrepSandbox.BuildDockerProcess(
            scanner,
            binaryPath,
            [new SandboxMount(rulesDirectory, "/rules", ReadOnly: true), new SandboxMount(testsDirectory, "/tests", ReadOnly: true)],
            ["scan", "--test", "--json", "--config", "/rules", "/tests"],
            user);

    private Setup Prepare(PipelineFunctionContext context)
    {
        // A build function always has its workspace: the executor refuses the task otherwise.
        var workspace = Path.GetFullPath(context.Workspace!);
        var rulesRelative = context.Parameters.GetValueOrDefault("rules") is { Length: > 0 } rulesValue ? rulesValue : DefaultRules;
        var testsRelative = context.Parameters.GetValueOrDefault("tests") is { Length: > 0 } testsValue ? testsValue : DefaultTests;
        var failure = ResolveInside(workspace, "rules", rulesRelative, out var rules);
        string? tests = null;
        failure ??= ResolveInside(workspace, "tests", testsRelative, out tests);
        if (failure is not null)
            return new Setup(failure);
        if (!_tools.IsLinuxX64)
            return new Setup("OpenGrep rule tests run only on a Linux x86_64 agent: the pinned binary exists for no other platform.");
        if (_findScanner() is not { } scanner)
            return new Setup("OpenGrep is absent from the scanner manifest of this agent.");
        try
        {
            OpenGrepSandbox.RequireVerifiedDownload(scanner);
            OpenGrepSandbox.RequirePinnedRuntime(scanner);
        }
        catch (IOException ex)
        {
            return new Setup($"OpenGrep refused: {ex.Message}");
        }
        var workDirectory = FunctionShell.Variable(context, "AETHEUS_AGENT_WORK_DIRECTORY");
        if (workDirectory is null || !Directory.Exists(workDirectory))
            return new Setup("AETHEUS_AGENT_WORK_DIRECTORY is missing or unavailable; the verified OpenGrep binary is kept there.");
        var docker = FunctionShell.FindOnPath(context, "docker");
        if (docker is null)
            return new Setup("docker is required to run OpenGrep in its sandbox.");
        return new Setup(null, scanner, OpenGrepSandbox.BinaryPath(workDirectory, scanner), rules, tests, docker,
            workDirectory, rulesRelative, testsRelative);
    }

    /// <summary>The directory named by a parameter, refused unless it exists inside the workspace.</summary>
    private static string? ResolveInside(string workspace, string parameter, string relative, out string? directory)
    {
        directory = null;
        var full = Path.GetFullPath(Path.Combine(workspace, relative));
        if (Path.IsPathRooted(relative)
            || !full.StartsWith(workspace.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            return $"The {parameter} directory '{relative}' must stay inside the workspace.";
        if (!Directory.Exists(full))
            return $"The {parameter} directory '{relative}' does not exist in the workspace.";
        directory = full;
        return null;
    }

    private async Task<RunOutput> RunAsync(System.Diagnostics.ProcessStartInfo process, CancellationToken cancellationToken)
    {
        var stdout = new StringBuilder();
        var stderr = new List<string>();
        var result = await _tools.RunAsync(process, (line, _) =>
        {
            lock (stderr)
            {
                if (line.StartsWith(StderrPrefix, StringComparison.Ordinal))
                {
                    var text = line[StderrPrefix.Length..].Trim();
                    if (text.Length > 0)
                        stderr.Add(text);
                }
                else if (!line.StartsWith(ExitLinePrefix, StringComparison.Ordinal))
                {
                    stdout.Append(line).Append('\n');
                }
            }
            return Task.CompletedTask;
        }, cancellationToken).ConfigureAwait(false);
        return new RunOutput(result.ExitCode, result.TimedOut, stdout.ToString(), stderr);
    }

    private static async Task<PipelineFunctionResult> ConcludeAsync(PipelineFunctionContext context, RunOutput output)
    {
        if (output.TimedOut)
            return PipelineFunctionResult.Failure("OpenGrep rule tests timed out.");
        var report = OpenGrepRuleTestReport.TryParse(output.Stdout);
        if (report is null)
        {
            foreach (var line in output.Stderr)
                await context.Log(line, TaskLogLevel.Error).ConfigureAwait(false);
            var cause = output.Stderr.Count > 0
                ? string.Join(" ", output.Stderr.TakeLast(ReportedErrorLines))
                : "no error output";
            return PipelineFunctionResult.Failure($"OpenGrep exited with code {output.ExitCode} without a rule test report: {cause}");
        }

        if (!report.Passed)
        {
            var problems = report.DescribeProblems().ToList();
            foreach (var problem in problems)
                await context.Log(problem, TaskLogLevel.Error).ConfigureAwait(false);
            return PipelineFunctionResult.Failure(
                $"{report.Failed.Count} of {report.Checks.Count} OpenGrep rule tests failed, {report.RuleFilesWithoutTests.Count} rule file(s) without a test, {report.RuleFilesWithErrors.Count} invalid rule file(s).");
        }
        if (output.ExitCode != 0)
            return PipelineFunctionResult.Failure(
                $"OpenGrep reported every rule test passed but exited with code {output.ExitCode}: {string.Join(" ", output.Stderr.TakeLast(ReportedErrorLines))}");

        var ruleFiles = report.Checks.Select(check => check.RuleFile).Distinct(StringComparer.Ordinal).Count();
        await context.Log($">>> {report.Checks.Count} OpenGrep rule tests passed across {ruleFiles} rule files.")
            .ConfigureAwait(false);
        return PipelineFunctionResult.Success();
    }

    private sealed record Setup(
        string? Failure,
        ScannerManifestEntry? Scanner = null,
        string? BinaryPath = null,
        string? Rules = null,
        string? Tests = null,
        string? Docker = null,
        string? WorkDirectory = null,
        string RulesRelative = "",
        string TestsRelative = "");

    private sealed record RunOutput(int ExitCode, bool TimedOut, string Stdout, IReadOnlyList<string> Stderr);
}
