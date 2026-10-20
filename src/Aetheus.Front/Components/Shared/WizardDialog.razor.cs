// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Components.Shared;

/// <summary> Aetheus' steps in OE's <see cref="OmniWizard"/> : the wizard draws the progress, the
/// step list, the body and the navigation; this component maps <see cref="WizardStep"/> onto <see
/// cref="OmniWizardStep"/>, lets the host move forward in code (<see cref="NextStep"/>) and keeps
/// Escape as Cancel on a page. </summary>
public partial class WizardDialog
{
    [Inject] private OmniDialogService Dialog { get; set; } = default!;
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;

    [Parameter, EditorRequired] public List<WizardStep> Steps { get; set; } = [];

    /// <summary>Whether the current step lets the user go on: false disables Next, or Finish on the last step.</summary>
    [Parameter] public bool CanAdvance { get; set; } = true;

    /// <summary>
    /// Optional veto invoked with the current step index before leaving it forward (Next, Finish, or a
    /// later step of the list). Return false to stay on the step (the validator is responsible for
    /// surfacing why, e.g. an error dialog). Keeps Next clickable so the user gets feedback instead of a
    /// silently-disabled button.
    /// </summary>
    [Parameter] public Func<int, Task<bool>>? ValidateBeforeNext { get; set; }

    /// <summary>Text of the last step's button; the localized "WizardFinish" when null.</summary>
    [Parameter] public string? FinishText { get; set; }
    [Parameter] public EventCallback<int> StepChanged { get; set; }
    [Parameter] public EventCallback OnFinishClicked { get; set; }
    [Parameter] public EventCallback OnCancelClicked { get; set; }

    /// <summary>Read the initial step once when constructing the wizard so callers can reopen
    /// directly on their selected summary.</summary>
    [Parameter] public int InitialStep { get; set; }

    private int _currentStep;

    public int CurrentStep => _currentStep;

    protected override void OnInitialized() =>
        _currentStep = Math.Clamp(InitialStep, 0, Math.Max(Steps.Count - 1, 0));

    /// <summary>
    /// Moves to the next step from code (a platform card chosen, a test), asking
    /// <see cref="ValidateBeforeNext"/> first like the Next button does.
    /// </summary>
    public async Task NextStep()
    {
        if (_currentStep >= Steps.Count - 1)
            return;
        if (ValidateBeforeNext is not null && !await ValidateBeforeNext(_currentStep))
            return;
        await MoveToAsync(_currentStep + 1);
    }

    private Func<Task<bool>>? ValidatorFor(int index) =>
        ValidateBeforeNext is { } validate ? () => validate(index) : null;

    private Task OnWizardStepChanged(int index) => MoveToAsync(index);

    private async Task MoveToAsync(int index)
    {
        if (index == _currentStep)
            return;
        _currentStep = index;
        StateHasChanged();
        await StepChanged.InvokeAsync(_currentStep);
    }

    private async Task OnCancel()
    {
        if (OnCancelClicked.HasDelegate)
            await OnCancelClicked.InvokeAsync();
        else
            Dialog.Close();
    }

    private async Task OnFinish()
    {
        if (OnFinishClicked.HasDelegate)
            await OnFinishClicked.InvokeAsync();
        else
            Dialog.Close(true);
    }

    private async Task OnKeyDown(KeyboardEventArgs e)
    {
        if (e.Key == "Escape")
            await OnCancel();
    }
}

public sealed class WizardStep
{
    public required string Title { get; init; }
    public required RenderFragment Content { get; init; }
}
