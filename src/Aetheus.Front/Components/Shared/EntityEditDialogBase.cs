// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

public abstract class EntityEditDialogBase : ComponentBase
{
    [Inject] protected ApiClient Api { get; set; } = default!;
    [Inject] protected UiActions Ui { get; set; } = default!;
    [Inject] protected NotifyHelper Toast { get; set; } = default!;
    [Inject] protected OmniDialogService Dialog { get; set; } = default!;
    [Inject] protected IStringLocalizer<AppStrings> L { get; set; } = default!;

    protected bool _busy;

    protected bool ValidateRequiredName(string? name)
    {
        if (!string.IsNullOrWhiteSpace(name))
            return true;

        Toast.Warning("ValidationError", "RequiredFields");
        return false;
    }

    protected async Task RunBusyAsync(Func<Task> action)
    {
        _busy = true;
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            _busy = false;
        }
    }

    protected Task CloseAfterSuccessAsync()
    {
        Dialog.Close(true);
        return Task.CompletedTask;
    }

    /// <summary>Closes the dialog with the saved entity the server answered, so the list that opened it can
    /// draw that row at once instead of reading everything again (R-10).</summary>
    protected Task CloseAfterSuccessAsync<T>(T saved)
    {
        Dialog.Close(saved);
        return Task.CompletedTask;
    }

    protected void Cancel() => Dialog.Close(false);
}
