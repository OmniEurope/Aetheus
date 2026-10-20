// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// Everything a <c>dotnet-test</c> step needs, validated before a process is started.
///
/// The validation is not ceremony. Every field below ends up either in an argv the agent runs or in
/// a path it writes to, and the values arrive from pipeline YAML that a project author edits. A
/// results directory that escapes the workspace, or a status variable name carrying anything but
/// an identifier, is a step definition reaching outside what it was given.
/// </summary>
public sealed record PipelineDotnetTestRequest
{
    private PipelineDotnetTestRequest() { }

    /// <summary>The run's checkout. Everything else is resolved under it.</summary>
    public required string Workspace { get; init; }

    /// <summary>The test project, relative to the workspace.</summary>
    public required string Project { get; init; }

    /// <summary>Absolute results directory, under the workspace.</summary>
    public required string ResultsDirectory { get; init; }

    public required string TrxName { get; init; }

    /// <summary>Absolute path of the TRX the run is expected to produce.</summary>
    public string TrxPath => Path.Combine(ResultsDirectory, TrxName);

    public required string StatusVariable { get; init; }

    public required bool CollectCoverage { get; init; }

    /// <summary>Absolute runsettings path, or null.</summary>
    public string? RunSettings { get; init; }

    public required string Configuration { get; init; }

    public required string DotnetPath { get; init; }

    /// <summary>Absolute directories prepended to the test host's PATH, in order.</summary>
    public IReadOnlyList<string> ToolPaths { get; init; } = [];

    /// <summary>Time between two progress lines while the suite runs.</summary>
    public TimeSpan ProgressInterval { get; init; } = PipelineDotnetTestToolchain.DefaultProgressInterval;

    public static bool TryCreate(
        string target,
        IReadOnlyDictionary<string, string> envVars,
        out PipelineDotnetTestRequest? request,
        out string? rejection)
    {
        ArgumentNullException.ThrowIfNull(envVars);
        request = null;

        var workspace = envVars.GetValueOrDefault("WORKSPACE");
        if (string.IsNullOrWhiteSpace(workspace))
        {
            rejection = "WORKSPACE is required.";
            return false;
        }

        if (!IsSafeRelativePath(target))
        {
            rejection = $"The test project path '{target}' must be relative and stay in the workspace.";
            return false;
        }

        var resultsName = envVars.GetValueOrDefault(
            PipelineDotnetTestVariables.ResultsDirectory);
        if (string.IsNullOrWhiteSpace(resultsName) || !IsSafeRelativePath(resultsName))
        {
            rejection =
                $"{PipelineDotnetTestVariables.ResultsDirectory} must be a relative "
                + "path inside the workspace.";
            return false;
        }

        var statusVariable = envVars.GetValueOrDefault(
            PipelineDotnetTestVariables.StatusVariable);
        if (string.IsNullOrWhiteSpace(statusVariable) || !PipelineRunVariableName.IsValid(statusVariable))
        {
            rejection =
                $"{PipelineDotnetTestVariables.StatusVariable} must be an upper-case "
                + "identifier; it names the run variable the gate reads.";
            return false;
        }

        var trxName = envVars.GetValueOrDefault(PipelineDotnetTestVariables.TrxName);
        if (string.IsNullOrWhiteSpace(trxName)) trxName = "results.trx";
        if (!IsSimpleFileName(trxName))
        {
            rejection =
                $"{PipelineDotnetTestVariables.TrxName} must be a plain file name.";
            return false;
        }

        var configuration = envVars.GetValueOrDefault(
            PipelineDotnetTestVariables.Configuration);
        if (string.IsNullOrWhiteSpace(configuration)) configuration = "Release";
        if (!IsIdentifier(configuration))
        {
            rejection =
                $"{PipelineDotnetTestVariables.Configuration} must be a plain "
                + "configuration name such as Release.";
            return false;
        }

        string? runSettings = null;
        var runSettingsValue = envVars.GetValueOrDefault(
            PipelineDotnetTestVariables.RunSettings);
        if (!string.IsNullOrWhiteSpace(runSettingsValue))
        {
            if (!IsSafeRelativePath(runSettingsValue))
            {
                rejection =
                    $"{PipelineDotnetTestVariables.RunSettings} must be a relative "
                    + "path inside the workspace.";
                return false;
            }
            runSettings = Path.Combine(workspace, runSettingsValue);
        }

        if (!PipelineDotnetTestToolchain.TryRead(envVars, out var toolchain, out rejection))
            return false;

        request = new PipelineDotnetTestRequest
        {
            Workspace = workspace,
            Project = target,
            ResultsDirectory = Path.Combine(workspace, resultsName),
            TrxName = trxName,
            StatusVariable = statusVariable,
            CollectCoverage = string.Equals(
                envVars.GetValueOrDefault(PipelineDotnetTestVariables.CollectCoverage),
                "true",
                StringComparison.OrdinalIgnoreCase),
            RunSettings = runSettings,
            Configuration = configuration,
            DotnetPath = toolchain.DotnetPath,
            ToolPaths = toolchain.ToolPaths,
            ProgressInterval = toolchain.ProgressInterval
        };
        rejection = null;
        return true;
    }

    /// <summary>
    /// A relative path with no traversal and no root. Rooted paths and <c>..</c> are refused rather
    /// than normalised: a step that names a directory outside its workspace is a definition error,
    /// and silently rewriting it would hide that from whoever wrote it.
    /// </summary>
    private static bool IsSafeRelativePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (Path.IsPathRooted(value)) return false;
        if (value.Contains(':', StringComparison.Ordinal)) return false;
        foreach (var segment in value.Split('/', '\\'))
        {
            if (segment.Length == 0) return false;
            if (segment is "." or "..") return false;
        }
        return true;
    }

    private static bool IsSimpleFileName(string value) =>
        value.Length > 0
        && value.IndexOfAny(['/', '\\', ':']) < 0
        && value is not ("." or "..");

    private static bool IsIdentifier(string value)
    {
        foreach (var character in value)
            if (!char.IsLetterOrDigit(character) && character is not ('_' or '-'))
                return false;
        return value.Length > 0;
    }
}
