// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Front.Components.Pipelines;

/// <summary>The warnings the end of the wizard raises once the pipelines exist: what the production setup left to fill or could
/// not prepare, a QA pipeline without Compose file, and a deployment pipeline that an external source blocked. None of them
/// blocks the creation; each says what the user still has to do.</summary>
internal static class PipelineSetupFinishNotices
{
    public static void Warn(NotifyHelper toast, ProductionPreparation prepared, PipelineSetupProduction setup, bool qa, bool externalBlocked)
    {
        if (prepared.Pending.Length > 0)
            toast.Warning("ProductionValuesPending", "ProductionValuesPendingDetail", prepared.Pending);
        if (prepared.ConfigFilesAdded.Length > 0)
            toast.Info("ProductionConfigFilesAdded", "ProductionConfigFilesAddedDetail", prepared.ConfigFilesAdded);
        if (prepared.NotPrepared)
            toast.Warning("ProductionNotPrepared", setup.RepositoryRead ? "ProductionNoComposeDetail" : "ProductionNotPreparedDetail");
        if (qa && string.IsNullOrEmpty(prepared.Input?.ComposeFile))
            toast.Warning("QaNoComposeFile", "QaNoComposeFileDetail");
        if (externalBlocked)
            toast.Warning("ExternalDeployNotCreated", setup.ExternalCauseKey);
    }

    /// <summary>R-38: why a pipeline was not created, by name and in the server's own words (a name already taken, a
    /// refused definition); the status when the server gave none. Never the bare "Operation failed".</summary>
    public static string CreateFailure(IStringLocalizer<AppStrings> l, string name, ApiOutcome<PipelineDto, YamlValidationResultDto> outcome) =>
        string.Format(l["PipelineCreateFailed"], name,
            outcome.ErrorMessage ?? (outcome.StatusCode is { } status ? $"HTTP {(int)status}" : l["OperationFailed"].Value));
}
