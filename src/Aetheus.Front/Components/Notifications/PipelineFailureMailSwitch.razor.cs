// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

/// <summary>The "e-mail me when this pipeline fails" switch of the signed-in user. Nothing is drawn while the
/// choice is unknown (not loaded, or the user cannot read the pipeline).</summary>
public partial class PipelineFailureMailSwitch
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public int PipelineId { get; set; }

    private PipelineFailureMailPreferenceDto? _preference;
    private bool _saving;
    private int _loadedPipelineId;

    private string SwitchId => $"pipeline-failure-mail-{PipelineId}";

    protected override async Task OnParametersSetAsync()
    {
        if (PipelineId <= 0 || PipelineId == _loadedPipelineId) return;
        _loadedPipelineId = PipelineId;
        try
        {
            _preference = await Api.Notifications.GetPipelineFailureMailAsync(PipelineId);
        }
        catch (HttpRequestException)
        {
            _preference = null;
        }
    }

    private async Task SaveAsync(bool isEnabled)
    {
        if (_saving) return;
        _saving = true;
        try
        {
            var saved = await Api.Notifications.SetPipelineFailureMailAsync(PipelineId, isEnabled);
            if (saved is null)
            {
                Toast.Error("Error", "SaveFailed");
                return;
            }
            _preference = saved;
            Toast.Success("Saved", "PreferencesSaved");
        }
        finally
        {
            _saving = false;
        }
    }
}
