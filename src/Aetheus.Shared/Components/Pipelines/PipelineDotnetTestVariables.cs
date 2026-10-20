// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Shared.Components.Pipelines;

/// <summary>
/// The environment-variable names a <c>type: dotnet-test</c> step travels under, from the control
/// plane that writes them to the agent that reads them.
///
/// They live in Shared rather than beside either side, because both sides must agree on them and
/// the control plane deliberately does not reference the agent. A name spelled slightly differently
/// on one side would not fail to compile, it would produce a step that silently ignores its own
/// configuration.
/// </summary>
public static class PipelineDotnetTestVariables
{
    /// <summary>Where the TRX and any coverage land, relative to the workspace.</summary>
    public const string ResultsDirectory = "AETHEUS_DOTNET_TEST_RESULTS_DIRECTORY";

    /// <summary>The run variable the classified status (0 clean, 1 findings) is published under.</summary>
    public const string StatusVariable = "AETHEUS_DOTNET_TEST_STATUS_VARIABLE";

    /// <summary>The TRX file name, inside the results directory.</summary>
    public const string TrxName = "AETHEUS_DOTNET_TEST_TRX_NAME";

    /// <summary><c>true</c> to collect and require Cobertura coverage.</summary>
    public const string CollectCoverage = "AETHEUS_DOTNET_TEST_COLLECT_COVERAGE";

    /// <summary>A runsettings file, relative to the workspace.</summary>
    public const string RunSettings = "AETHEUS_DOTNET_TEST_RUNSETTINGS";

    /// <summary>The build configuration, defaulting to Release.</summary>
    public const string Configuration = "AETHEUS_DOTNET_TEST_CONFIGURATION";

    /// <summary>The dotnet executable to use. The pipelines resolve a pinned SDK and pass its path;
    /// without one the agent falls back to whatever <c>dotnet</c> is on PATH.</summary>
    public const string DotnetPath = "AETHEUS_DOTNET_TEST_DOTNET";

    /// <summary>Absolute directories prepended to the test host's PATH, one per line, so a suite finds
    /// the pinned tools its tests start (Node.js, for instance) instead of whatever the host
    /// provides.</summary>
    public const string ToolPaths = "AETHEUS_DOTNET_TEST_TOOL_PATHS";

    /// <summary>Seconds between two progress lines while the suite runs, defaulting to 30. Same name
    /// as the run variable the legacy run-with-progress.sh read.</summary>
    public const string ProgressIntervalSeconds = "AETHEUS_PROGRESS_INTERVAL_SECONDS";
}
