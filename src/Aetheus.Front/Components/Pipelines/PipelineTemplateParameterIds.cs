// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>The element ids of a template parameter's field on the pipeline page: its control, its OE field and
/// the description behind its "?", which tie the label and <c>aria-describedby</c> to the control.</summary>
internal static class PipelineTemplateParameterIds
{
    /// <summary>The id of a template parameter's control, named by the label of its field.</summary>
    internal static string Control(string parameterName) => $"tpl-param-{parameterName}";

    /// <summary>The id of a template parameter's OE field; the "?" of its Help carries <c>{id}-help-content</c>.</summary>
    internal static string Field(string parameterName) => $"{Control(parameterName)}-field";

    /// <summary>The id of a template parameter's description behind the "?" of its field, named by the
    /// control's <c>aria-describedby</c>; <c>null</c> without a description.</summary>
    internal static string? Description(string parameterName, string? description) =>
        string.IsNullOrWhiteSpace(description) ? null : $"{Field(parameterName)}-help-content";
}
