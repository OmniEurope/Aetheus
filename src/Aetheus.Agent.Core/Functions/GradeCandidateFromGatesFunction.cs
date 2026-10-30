// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Agent.Core.Functions;

/// <summary>
/// The <c>grade-candidate-from-gates</c> function (recette R-23, W8). The wizard's candidate delegates its analyses to
/// the project's Quality and Security pipelines, whose gates grade them and publish the letter
/// (<c>ANALYSIS_QUALITY_GRADE</c>, <c>ANALYSIS_SECURITY_GRADE</c>); the trigger steps hand those outputs up to the
/// candidate. This function turns them into the candidate's verdict, the two variables the release step reads
/// (<c>ReleaseAssuranceVerdictReader</c>): <c>CANDIDATE_ASSURANCE_GRADE</c>, the worst grade, and
/// <c>CANDIDATE_DEPLOYABLE</c>, whether it reaches the deployment threshold (<c>AETHEUS_DEPLOY_MINIMUM_GRADE</c>, else E,
/// the platform's own candidate's default). A release graded F or refused still asks for the approval of its
/// deployment (R-24): the verdict is reported, never softened.
/// <para>A gate the project does not run is left out. A gate that ran without a grade (an incomplete evaluation, an agent
/// that does not publish it) is never read as passed: no grade is claimed (<c>none</c>) and the candidate is not
/// deployable. With no gate at all neither is claimed (<c>none</c> for both) and the release stays not qualified, as
/// before this function.</para>
/// </summary>
public sealed class GradeCandidateFromGatesFunction : IPipelineFunction
{
    private const string DefaultMinimumGrade = "E";
    private const string GradeScale = "ABCDEF";
    private const string Unknown = "none";

    public string Name => PipelineFunctionCatalog.GradeCandidateFromGates.Name;

    public async Task<PipelineFunctionResult> ExecuteAsync(PipelineFunctionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var minimum = FunctionShell.Input(context, "minimum_grade", "AETHEUS_DEPLOY_MINIMUM_GRADE") ?? DefaultMinimumGrade;
        if (!IsGrade(minimum))
            return PipelineFunctionResult.Failure($"The deployment threshold '{minimum}' is not an assurance grade (A to F).");

        var gates = new[]
        {
            Gate(context, "quality", "quality_enabled", "APPLICATION_QUALITY_ENABLED", "quality_grade", "ANALYSIS_QUALITY_GRADE"),
            Gate(context, "security", "security_enabled", "APPLICATION_SECURITY_ENABLED", "security_grade", "ANALYSIS_SECURITY_GRADE")
        }.Where(gate => gate.Ran).ToList();

        // Every declared output is published (the executor refuses one left unset); "none" is neither a grade nor a
        // boolean, so the release reads it as unknown.
        if (gates.Count == 0)
        {
            await context.Log("No Quality or Security gate ran for this candidate: its release carries no grade and stays not qualified.")
                .ConfigureAwait(false);
            context.SetOutput("CANDIDATE_ASSURANCE_GRADE", Unknown);
            context.SetOutput("CANDIDATE_DEPLOYABLE", Unknown);
            return PipelineFunctionResult.Success();
        }

        var ungraded = gates.Where(gate => !IsGrade(gate.Grade)).Select(gate => gate.Name).ToList();
        if (ungraded.Count > 0)
        {
            await context.Log(
                $"The {string.Join(" and ", ungraded)} gate ran but published no grade: the candidate is not graded and is not deployable.")
                .ConfigureAwait(false);
            context.SetOutput("CANDIDATE_ASSURANCE_GRADE", Unknown);
            context.SetOutput("CANDIDATE_DEPLOYABLE", "false");
            return PipelineFunctionResult.Success();
        }

        var worst = gates.Select(gate => gate.Grade!).MaxBy(grade => GradeScale.IndexOf(grade, StringComparison.Ordinal))!;
        var deployable = GradeScale.IndexOf(worst, StringComparison.Ordinal) <= GradeScale.IndexOf(minimum, StringComparison.Ordinal);
        var detail = string.Join(", ", gates.Select(gate => $"{gate.Name} {gate.Grade}"));
        await context.Log(deployable
                ? $"Candidate graded {worst} ({detail}): deployable, the deployment threshold is {minimum}."
                : $"Candidate graded {worst} ({detail}): NOT deployable, below the deployment threshold {minimum}.")
            .ConfigureAwait(false);
        context.SetOutput("CANDIDATE_ASSURANCE_GRADE", worst);
        context.SetOutput("CANDIDATE_DEPLOYABLE", deployable ? "true" : "false");
        return PipelineFunctionResult.Success();
    }

    private static (string Name, bool Ran, string? Grade) Gate(
        PipelineFunctionContext context, string name, string enabledParameter, string enabledVariable,
        string gradeParameter, string gradeVariable) =>
        (name,
            FunctionShell.Input(context, enabledParameter, enabledVariable) != "false",
            FunctionShell.Input(context, gradeParameter, gradeVariable));

    /// <summary>Exactly one capital letter A to F, as the deployment gate reads a threshold.</summary>
    private static bool IsGrade(string? value) =>
        value is { Length: 1 } && GradeScale.Contains(value[0], StringComparison.Ordinal);
}
