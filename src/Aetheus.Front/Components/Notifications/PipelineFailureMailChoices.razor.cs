// SPDX-License-Identifier: EUPL-1.2
using System.Net.Http;

namespace Aetheus.Front.Components.Notifications;

/// <summary>The per-pipeline failure-mail choices of the signed-in user, listed in their notification settings.</summary>
public partial class PipelineFailureMailChoices
{
    [Inject] private ApiClient Api { get; set; } = default!;
    [Inject] private NotifyHelper Toast { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    private List<PipelineFailureMailChoiceDto> _choices = [];

    protected override async Task OnInitializedAsync()
    {
        try
        {
            _choices = await Api.Notifications.GetPipelineFailureMailChoicesAsync();
        }
        catch (HttpRequestException)
        {
            // The list is a convenience beside the pipeline's own switch: without it nothing is drawn.
            _choices = [];
        }
    }

    private async Task SaveAsync(PipelineFailureMailChoiceDto choice, bool isEnabled)
    {
        var saved = await Api.Notifications.SetPipelineFailureMailAsync(choice.PipelineId, isEnabled);
        if (saved is null)
        {
            Toast.Error("Error", "SaveFailed");
            return;
        }
        _choices = [.. _choices.Select(item => item.PipelineId == choice.PipelineId ? item with { IsEnabled = saved.IsEnabled } : item)];
        Toast.Success("Saved", "PreferencesSaved");
    }
}
