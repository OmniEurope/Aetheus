// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Projects;

/// <summary>What lowers a project's grade below A, shown in parentheses beside the grade (user decision 2026-11-10).
/// Shows nothing for an A, for a grade without letter, or when the cause is not known.</summary>
public partial class ProjectGradeCause
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter] public AnalysisGrade? Grade { get; set; }
    [Parameter] public AnalysisGradeCause? Cause { get; set; }
    [Parameter] public int ProjectId { get; set; }
    [Parameter] public string ProjectName { get; set; } = string.Empty;

    /// <summary>The run of the seal that lowers the grade, to open on its Gate tab.</summary>
    [Parameter] public int? RunId { get; set; }

    /// <summary>That run's number within its pipeline, the one a person reads.</summary>
    [Parameter] public int? RunNumber { get; set; }

    /// <summary>The pipeline of that run; null when unknown (the run alone is then named).</summary>
    [Parameter] public string? PipelineName { get; set; }

    private bool Lowered => Grade is { } grade && grade != AnalysisGrade.A;

    private bool Pipeline => Lowered && Cause == AnalysisGradeCause.Pipeline && RunId is not null;

    private bool ProjectCause => Lowered && Cause == AnalysisGradeCause.Project;

    /// <summary>"Candidate #345": the pipeline name and the run number; the run alone when the pipeline is unknown.</summary>
    private string PipelineLabel
    {
        get
        {
            var number = (RunNumber ?? RunId)?.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return string.IsNullOrWhiteSpace(PipelineName) ? $"#{number}" : $"{PipelineName} #{number}";
        }
    }
}
