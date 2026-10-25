// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public abstract class LiveDurationComponentBase : ComponentBase, IDisposable
{
    private IDisposable? _timer;

    protected abstract bool IsLive { get; }
    protected abstract IDisposable CreateTimer(TimerCallback callback);

    protected override void OnParametersSet()
    {
        if (IsLive)
        {
            _timer ??= CreateTimer(_ => _ = InvokeAsync(StateHasChanged));
            return;
        }

        _timer?.Dispose();
        _timer = null;
    }

    public void Dispose() => _timer?.Dispose();
}
