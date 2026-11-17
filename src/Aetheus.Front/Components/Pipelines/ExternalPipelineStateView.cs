// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-11: how the state of an external repository pipeline file reads (badge tone, icon, label key).</summary>
internal static class ExternalPipelineStateView
{
    public static OmniTone Tone(ExternalPipelineImportState state) => state switch
    {
        ExternalPipelineImportState.Imported => OmniTone.Info,
        ExternalPipelineImportState.ChangedSinceImport => OmniTone.Warning,
        ExternalPipelineImportState.Refused => OmniTone.Danger,
        _ => OmniTone.Neutral
    };

    public static OmniIconName Icon(ExternalPipelineImportState state) => state switch
    {
        ExternalPipelineImportState.Imported => OmniIconName.Lock,
        ExternalPipelineImportState.ChangedSinceImport => OmniIconName.Sync,
        ExternalPipelineImportState.Refused => OmniIconName.Close,
        ExternalPipelineImportState.NameTaken => OmniIconName.LockOpen,
        _ => OmniIconName.Download
    };

    public static string LabelKey(ExternalPipelineImportState state) => state switch
    {
        ExternalPipelineImportState.Imported => "ExternalPipelineStateImported",
        ExternalPipelineImportState.ChangedSinceImport => "ExternalPipelineStateChanged",
        ExternalPipelineImportState.Refused => "ExternalPipelineStateRefused",
        ExternalPipelineImportState.NameTaken => "ExternalPipelineStateNameTaken",
        _ => "ExternalPipelineStateAvailable"
    };

    public static bool CanImport(ExternalPipelineImportState state)
        => state is ExternalPipelineImportState.Available or ExternalPipelineImportState.ChangedSinceImport;
}
