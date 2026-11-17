// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>R-8: "Update all pipelines" of a project, or of the fleet when <see cref="ProjectId"/> is null.</summary>
public partial class PipelineTemplatesBulkUpdate
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    /// <summary>The project whose pipelines move; null moves the caller's whole fleet.</summary>
    [Parameter] public int? ProjectId { get; set; }

    /// <summary>Whether the button shows: the host knows a pipeline has a newer version and the caller may write.</summary>
    [Parameter] public bool Visible { get; set; }

    /// <summary>Raised once pipelines moved, so the host reads them again.</summary>
    [Parameter] public EventCallback Updated { get; set; }

    private PipelineFleetBulkUpdateResultDto? _result;
    private bool _running;

    private List<PipelineFleetBulkUpdateItemDto> NotUpdated =>
        _result?.Items.Where(item => item.Outcome != PipelineFleetBulkUpdateOutcome.Updated).ToList() ?? [];

    private int UpdatedCount =>
        _result?.Items.Count(item => item.Outcome == PipelineFleetBulkUpdateOutcome.Updated) ?? 0;

    private OmniSeverity ResultSeverity => NotUpdated.Count > 0
        ? OmniSeverity.Warning
        : UpdatedCount > 0 ? OmniSeverity.Success : OmniSeverity.Info;

    private string? ResultTitle => _result is { Items.Count: > 0 }
        ? L["UpdateAllPipelinesResult", UpdatedCount, NotUpdated.Count].Value
        : null;

    internal async Task RunAsync()
    {
        if (_running) return;
        // The single update confirms on its preview page: the batch asks once, for every pipeline.
        var confirmed = await Dialog.Confirm(L["UpdateAllPipelinesConfirm"].Value, L["UpdateAllPipelines"].Value,
            new OmniConfirmOptions
            {
                OkButtonText = L["UpdateAllPipelinesShort"].Value,
                CancelButtonText = L["GoBack"].Value,
                ConfirmIcon = OmniIconName.Download
            });
        if (confirmed != true) return;
        _running = true;
        try
        {
            _result = await Api.Pipelines.UpdateAllGenericTemplatePipelinesAsync(ProjectId);
            if (_result is null)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            if (UpdatedCount > 0)
            {
                Toast.Success("Updated", "UpdateAllPipelinesResult", UpdatedCount, NotUpdated.Count);
                await Updated.InvokeAsync();
            }
        }
        catch (HttpRequestException)
        {
            Toast.Error("Error", "SaveFailed");
        }
        finally
        {
            _running = false;
        }
    }

    private void DismissResult() => _result = null;

    private string ItemText(PipelineFleetBulkUpdateItemDto item)
    {
        var outcome = L.Localize(item.Outcome);
        var detail = string.IsNullOrWhiteSpace(item.Reason) ? outcome : $"{outcome}, {item.Reason}";
        return L["UpdateAllPipelinesItem", item.PipelineName, item.TemplateName, item.FromVersion, item.ToVersion, detail].Value;
    }
}
