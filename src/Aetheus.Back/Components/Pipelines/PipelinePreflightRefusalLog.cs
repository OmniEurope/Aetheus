// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Components.Pipelines;

/// <summary>Each preflight requirement is logged separately to keep attributes within ingest
/// limits. Launcher and scheduler summaries use Information to avoid duplicate warnings.</summary>
internal static class PipelinePreflightRefusalLog
{
    /// <summary>A problem longer than this is cut in the log (never in the refusal itself), so one
    /// entry stays under the ingest bound.</summary>
    internal const int MaxLoggedProblemLength = 500;

    public static void Write(ILogger logger, string pipelineName, IReadOnlyList<string> problems)
    {
        logger.LogWarning(
            "Preflight refused a launch of pipeline {PipelineName} with {ProblemCount} unmet requirement(s)",
            pipelineName, problems.Count);
        for (var index = 0; index < problems.Count; index++)
        {
            var problem = problems[index];
            if (problem.Length > MaxLoggedProblemLength)
                problem = string.Concat(problem.AsSpan(0, MaxLoggedProblemLength), " [...]");
            logger.LogWarning(
                "Preflight unmet requirement {ProblemNumber}/{ProblemCount} of pipeline {PipelineName}: {Problem}",
                index + 1, problems.Count, pipelineName, problem);
        }
    }
}
