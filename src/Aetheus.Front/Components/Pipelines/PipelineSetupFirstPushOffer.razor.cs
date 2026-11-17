// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>The optional first push of the setup wizard (R-20): the commands to copy, the choice to wait for the push or to
/// skip it, and, while the wizard waits, one read of the repository every <see cref="PipelineSetupFirstPush.PollInterval"/>
/// until it holds a branch. The wait stops with the component: leaving the step stops the reads, coming back to the
/// step starts them again while the wizard is still waiting.</summary>
public sealed partial class PipelineSetupFirstPushOffer : IDisposable
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private TimeProvider Clock { get; set; } = default!;

    [Parameter, EditorRequired] public PipelineSetupFirstPush State { get; set; } = default!;

    private CancellationTokenSource? _polling;

    protected override void OnParametersSet()
    {
        if (State.Watching && _polling is null)
        {
            _polling = new CancellationTokenSource();
            _ = PollAsync(_polling);
        }
        else if (!State.Watching)
            StopPolling();
    }

    private void Accept(MouseEventArgs args) => State.Accept();

    private void Skip(MouseEventArgs args) => State.Skip();

    private async Task PollAsync(CancellationTokenSource polling)
    {
        var cancel = polling.Token;
        try
        {
            var received = false;
            while (!received && !cancel.IsCancellationRequested)
            {
                await Task.Delay(PipelineSetupFirstPush.PollInterval, Clock, cancel);
                await InvokeAsync(async () => received = await State.CheckAsync());
            }
        }
        catch (OperationCanceledException)
        {
            // Mandatory: the wait ended (skipped, or the step was left); the loop stops with it.
        }
        finally
        {
            if (ReferenceEquals(_polling, polling)) StopPolling();
            if (!cancel.IsCancellationRequested) await InvokeAsync(StateHasChanged);
        }
    }

    private void StopPolling()
    {
        _polling?.Cancel();
        _polling?.Dispose();
        _polling = null;
    }

    public void Dispose() => StopPolling();
}
