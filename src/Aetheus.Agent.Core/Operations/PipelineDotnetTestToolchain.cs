// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Agent.Core.Operations;

/// <summary>
/// The toolchain half of a <c>dotnet-test</c> request: which <c>dotnet</c> runs the suite, which tool
/// directories go first on the test host's PATH, and how often a progress line is written.
///
/// The values usually come from step outputs (<c>$(DOTNET)</c> published by the
/// <c>ensure-dotnet-sdk</c> function, a pinned Node.js directory), substituted by the control plane.
/// A reference that was never published reaches the agent literally, so anything that is not
/// <c>dotnet</c> or an absolute path is refused: running the host's toolchain instead of the pinned
/// one would hide the missing output.
/// </summary>
internal readonly record struct PipelineDotnetTestToolchain(
    string DotnetPath, IReadOnlyList<string> ToolPaths, TimeSpan ProgressInterval)
{
    internal static readonly TimeSpan DefaultProgressInterval = TimeSpan.FromSeconds(30);

    private const string HostDotnet = "dotnet";

    internal static bool TryRead(
        IReadOnlyDictionary<string, string> envVars,
        out PipelineDotnetTestToolchain toolchain,
        out string? rejection)
    {
        toolchain = default;
        if (!TryReadDotnet(envVars, out var dotnetPath, out rejection)
            || !TryReadToolPaths(envVars, out var toolPaths, out rejection)
            || !TryReadProgressInterval(envVars, out var interval, out rejection))
            return false;

        toolchain = new PipelineDotnetTestToolchain(dotnetPath, toolPaths, interval);
        return true;
    }

    private static bool TryReadDotnet(
        IReadOnlyDictionary<string, string> envVars, out string dotnetPath, out string? rejection)
    {
        dotnetPath = envVars.GetValueOrDefault(PipelineDotnetTestVariables.DotnetPath)?.Trim() ?? string.Empty;
        if (dotnetPath.Length == 0) dotnetPath = HostDotnet;
        rejection = dotnetPath == HostDotnet || Path.IsPathFullyQualified(dotnetPath)
            ? null
            : $"{PipelineDotnetTestVariables.DotnetPath} must be 'dotnet' or an absolute path; "
              + $"'{dotnetPath}' is neither (an output that was never published?).";
        return rejection is null;
    }

    private static bool TryReadToolPaths(
        IReadOnlyDictionary<string, string> envVars, out IReadOnlyList<string> toolPaths, out string? rejection)
    {
        toolPaths = [];
        rejection = null;
        var value = envVars.GetValueOrDefault(PipelineDotnetTestVariables.ToolPaths);
        if (string.IsNullOrEmpty(value)) return true;

        var entries = value.Split('\n').Select(entry => entry.Trim()).ToArray();
        var invalid = entries.FirstOrDefault(entry => !Path.IsPathFullyQualified(entry));
        if (invalid is not null)
        {
            rejection = $"{PipelineDotnetTestVariables.ToolPaths} must hold absolute directories, one per line; "
                + $"'{invalid}' is not one (an output that was never published?).";
            return false;
        }
        toolPaths = entries;
        return true;
    }

    private static bool TryReadProgressInterval(
        IReadOnlyDictionary<string, string> envVars, out TimeSpan interval, out string? rejection)
    {
        interval = DefaultProgressInterval;
        rejection = null;
        var value = envVars.GetValueOrDefault(PipelineDotnetTestVariables.ProgressIntervalSeconds);
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0)
        {
            interval = TimeSpan.FromSeconds(seconds);
            return true;
        }
        rejection = $"{PipelineDotnetTestVariables.ProgressIntervalSeconds} must be a positive integer.";
        return false;
    }
}
