// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// What the read-only definition peek beside the YAML editor shows: a pipeline of the project (Ctrl+Click on a
/// `pipeline: name` ref) or a template version (R-35, Ctrl+Click on an `extends: template@version` ref), never both.
/// </summary>
internal sealed class PipelineDefinitionPeekTarget
{
    /// <summary>The pipeline peeked, or null.</summary>
    public string? PipelineName { get; private set; }

    /// <summary>The template reference peeked (`name@version` or a bare name), or null.</summary>
    public string? TemplateReference { get; private set; }

    /// <summary>True while the peek is open on either.</summary>
    public bool IsOpen => PipelineName is not null || TemplateReference is not null;

    public void ShowPipeline(string name) => (PipelineName, TemplateReference) = (name, null);

    public void ShowTemplate(string reference) => (PipelineName, TemplateReference) = (null, reference);

    public void Close() => (PipelineName, TemplateReference) = (null, null);
}
