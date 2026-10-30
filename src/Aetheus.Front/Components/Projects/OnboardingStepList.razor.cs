// SPDX-License-Identifier: EUPL-1.2

using Microsoft.AspNetCore.Components.Web;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Projects;

/// <summary>
/// Row-per-step onboarding checklist. Purely presentational: it renders the states its caller
/// computed and never derives one itself. A row whose step carries a link is clickable as a whole
/// (mouse and keyboard), not only through its action link.
/// </summary>
public partial class OnboardingStepList
{
    [Inject] private IStringLocalizer<AppStrings> L { get; set; } = default!;
    [Inject] private NavigationManager Nav { get; set; } = default!;

    [Parameter, EditorRequired] public string Title { get; set; } = string.Empty;
    [Parameter, EditorRequired] public string Description { get; set; } = string.Empty;
    [Parameter, EditorRequired] public IReadOnlyList<OnboardingStep> Steps { get; set; } = [];

    /// <summary>When set, the heading shows a Done button that raises it (the caller then hides the list).</summary>
    [Parameter] public EventCallback OnDone { get; set; }

    private static string RowCss(OnboardingStep step) => step.State switch
    {
        OnboardingStepState.Done => "onboarding-step-row is-done",
        OnboardingStepState.Blocked => "onboarding-step-row is-blocked",
        _ => "onboarding-step-row"
    };

    private static OmniTone StateBadgeVariant(OnboardingStepState state) => state switch
    {
        OnboardingStepState.Done => OmniTone.Success,
        OnboardingStepState.Blocked => OmniTone.Warning,
        _ => OmniTone.Accent
    };

    private string StateText(OnboardingStepState state) => state switch
    {
        OnboardingStepState.Done => L["Done"],
        OnboardingStepState.Blocked => L["OnboardingStepBlocked"],
        _ => L["OnboardingStepTodo"]
    };

    private static bool HasLink(OnboardingStep step) =>
        step.State != OnboardingStepState.Blocked && !string.IsNullOrWhiteSpace(step.Href);

    private void Open(OnboardingStep step)
    {
        if (HasLink(step))
            Nav.NavigateTo(step.Href!);
    }

    private void OnRowKeyDown(KeyboardEventArgs args, OnboardingStep step)
    {
        if (args.Key is "Enter" or " ")
            Open(step);
    }
}
