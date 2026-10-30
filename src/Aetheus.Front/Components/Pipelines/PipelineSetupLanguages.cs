// SPDX-License-Identifier: EUPL-1.2
using System.Text;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// The languages the quality pipeline the wizard creates analyses (recette R-105, user decision 2026-10-25). The pipeline
/// has a producer for each of .NET (complexity), Python (Ruff) and Java (PMD); a project that does not use one of them must
/// not see its steps skipped at every run, so the pipeline is written without them. The wizard pre-ticks the languages the
/// project's repository holds a file of, and the user changes them.
/// </summary>
/// <param name="DotNet">The C# complexity measure and the .NET SDK resolution stages.</param>
/// <param name="Python">The Ruff lint step.</param>
/// <param name="Java">The PMD step.</param>
internal sealed record PipelineSetupLanguages(bool DotNet, bool Python, bool Java)
{
    /// <summary>The names the three languages carry among the options of the summary's address.</summary>
    public const string DotNetOption = "lang-dotnet";
    public const string PythonOption = "lang-python";
    public const string JavaOption = "lang-java";

    /// <summary>Written in the address when the quality pipeline is chosen and no language is: an address that carries no
    /// language option at all is one written before R-105, which has no choice to take back.</summary>
    public const string NoneOption = "lang-none";

    private const string LintSourceStage = "Lint source";
    private const string LintPythonStep = "Lint Python";
    private const string LintJavaStep = "Check Java quality";

    /// <summary>What the Capabilities step starts with. A language the repository holds a file of is ticked, one it was read
    /// without finding is not. A language the reading could not tell (a null answer: a search the budget stopped, a repository
    /// that could not be read) is not ticked either (R-42): Python and Java are never assumed, and <see cref="IsUncertain"/>
    /// tells the user on the step to confirm. .NET falls back on the stack, the one the wizard generates for.</summary>
    public static PipelineSetupLanguages Proposal(PipelineSetupDetectionDto? detection, PipelineSetupStack stack) => new(
        detection?.HasDotNetProject ?? stack == PipelineSetupStack.DotNet,
        detection?.HasPython ?? false,
        detection?.HasJava ?? false);

    /// <summary>Whether the repository could not be read to the end for one of the languages, so the user must confirm which
    /// ones to keep.</summary>
    public static bool IsUncertain(PipelineSetupDetectionDto? detection) =>
        detection is null || detection.HasDotNetProject is null || detection.HasPython is null || detection.HasJava is null;

    /// <summary>The step of "Lint source" the project has no use for, when it uses exactly one of Python and Java.</summary>
    public string? UnwantedLintStep => Python == Java ? null : Python ? LintJavaStep : LintPythonStep;

    /// <summary>
    /// The stage "Lint source" of the pinned template version, rewritten to remove the step the project has no use for
    /// (null when the template has no such stage; empty when it already has no such step). A stage of the child with the same name
    /// replaces the template's stage (only its steps are merged by name), so it is the template's own stage, read at
    /// creation from the version the pipeline is pinned to, with its steps replaced by the removal: a property the template
    /// adds later (a condition, a timeout) is carried along, never frozen in the wizard.
    /// </summary>
    public static string? RestatedLintSource(string templateYaml, string unwantedStep)
    {
        var yaml = new YamlSerializationService();
        var stage = yaml.Parse(templateYaml)?.Stages.FirstOrDefault(
            item => string.Equals(item.Name, LintSourceStage, StringComparison.OrdinalIgnoreCase));
        if (stage is null) return null;
        return stage.Steps.Exists(step => string.Equals(step.Name, unwantedStep, StringComparison.OrdinalIgnoreCase))
            ? yaml.SerializeStage(stage with { Steps = [new PipelineStepDefinition { Name = unwantedStep, Remove = true }] })
            : string.Empty;
    }

    /// <summary>Reads, at creation, the pinned version of the quality template and rewrites its "Lint source" stage
    /// (<see cref="RestatedLintSource"/>). Null for any other template and when no single step is unwanted; a template that
    /// cannot be read, or has no such stage, stops the creation rather than writing a copy of the stage the wizard would freeze.</summary>
    public async Task<string?> ReadLintSourceAsync(ApiClient api, IStringLocalizer<AppStrings> localizer, PipelineTemplateSummaryDto template)
    {
        if (!DeliveryTemplateNames.Is(template.Name, DeliveryTemplateNames.Quality) || UnwantedLintStep is not { } step) return null;
        var yaml = (await api.PipelineTemplates.GetPipelineTemplateVersionAsync(template.Id, template.Version))?.YamlContent;
        return (yaml is null ? null : RestatedLintSource(yaml, step))
            ?? throw new InvalidOperationException(string.Format(localizer["PipelineSetupQualityTemplateUnreadable"].Value, template.Name, template.Version));
    }

    /// <summary>
    /// The <c>stages:</c> block of the quality pipeline: the stages of the template this project has no use for, removed at
    /// creation (<c>remove: true</c>, ADR-012) rather than skipped at every run. The stages that waited for a removed one wait
    /// for what it waited for (the resolver splices them). Names are those of the <c>application-quality</c> template.
    /// "Publish lint" always goes: the wizard writes no quality command that produces a lint report (R-105).
    /// </summary>
    /// <param name="restatedLintSource">The result of <see cref="RestatedLintSource"/>, needed (not null) when exactly one of
    /// Python and Java is used.</param>
    public string QualityStages(string? restatedLintSource = null)
    {
        var yaml = new StringBuilder("stages:\n");
        Remove(yaml, "Publish lint");
        if (!DotNet)
        {
            Remove(yaml, "Find pinned SDK");
            Remove(yaml, "Pinned SDK");
            Remove(yaml, "Measure complexity");
        }
        if (!Python && !Java)
            Remove(yaml, LintSourceStage);
        else if (UnwantedLintStep is not null)
            Restate(yaml, restatedLintSource ?? throw new InvalidOperationException(
                $"The stage '{LintSourceStage}' of the quality template was not read, so it cannot be written without its unused step."));
        return yaml.ToString().TrimEnd('\n');
    }

    private static void Remove(StringBuilder yaml, string stage) =>
        yaml.Append("  - name: ").Append(stage).Append("\n    remove: true\n");

    private static void Restate(StringBuilder yaml, string stage)
    {
        if (stage.Length == 0) return;
        foreach (var (line, index) in stage.TrimEnd().Split('\n').Select((line, index) => (line.TrimEnd('\r'), index)))
            yaml.Append(index == 0 ? "  - " : "    ").Append(line).Append('\n');
    }
}
