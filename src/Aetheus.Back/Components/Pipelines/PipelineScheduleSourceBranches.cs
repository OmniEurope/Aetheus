// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;

namespace Aetheus.Back.Components.Pipelines;

/// <summary>
/// The branch a reader chose, per pipeline, to read the schedule from in the planner (recette R-5): the repeated query
/// <c>sourceBranch=&lt;pipelineId&gt;:&lt;branch&gt;</c> of the plan endpoint. Only <see cref="ScheduleDefaults.SourceBranchChoices"/>
/// are accepted, so that no other ref name reaches git from a query string. The choice changes what the plan shows and
/// nothing else: it is never written to the pipeline.
/// </summary>
internal static class PipelineScheduleSourceBranches
{
    /// <summary>The choices of the query, the last one winning for a pipeline named twice; null when there is none. Anything
    /// that is not <c>id:branch</c> with a branch of the allowed list is a <see cref="BadRequestException"/>.</summary>
    public static IReadOnlyDictionary<int, string>? Parse(IEnumerable<string>? values)
    {
        Dictionary<int, string>? choices = null;
        foreach (var value in values ?? [])
        {
            var separator = value.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0
                || !int.TryParse(value.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var pipelineId)
                || pipelineId <= 0)
            {
                throw new BadRequestException("A source branch choice is '<pipeline id>:<branch>'.");
            }

            var branch = value[(separator + 1)..];
            if (!ScheduleDefaults.SourceBranchChoices.Contains(branch, StringComparer.Ordinal))
            {
                throw new BadRequestException(
                    $"The planner reads a definition on {string.Join(" or ", ScheduleDefaults.SourceBranchChoices)} only.");
            }

            (choices ??= [])[pipelineId] = branch;
        }

        return choices;
    }
}
