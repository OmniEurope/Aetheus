// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;

namespace Aetheus.Front.Tests.Shared;

/// <summary> WizardDialog hands Aetheus' steps to OE's OmniWizard : the steps in a list at the top,
/// only the current step in the body, and OE's navigation at the foot. </summary>
public class WizardDialogTests : BunitContext
{
    public WizardDialogTests()
    {
        BunitTestHelper.RegisterServices(this);
    }

    private static List<WizardStep> CreateSteps(int count)
    {
        var steps = new List<WizardStep>();
        for (var i = 0; i < count; i++)
        {
            var index = i;
            steps.Add(new WizardStep
            {
                Title = $"Step {index + 1}",
                Content = (RenderTreeBuilder b) => b.AddContent(0, $"Content of step {index + 1}")
            });
        }
        return steps;
    }

    private static AngleSharp.Dom.IElement Button(IRenderedComponent<WizardDialog> cut, string cssClass) =>
        cut.Find($".omni-wizard__actions button.{cssClass}");

    [Fact]
    public void Renders_OmniWizard_WithStepListAboveTheBody()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(3)));

        var wizard = cut.Find(".omni-wizard");
        var titles = wizard.QuerySelectorAll(".omni-steps__button").Select(b => b.TextContent).ToList();
        Assert.Equal(3, titles.Count);
        Assert.Contains(titles, t => t.Contains("Step 1"));
        Assert.Contains(titles, t => t.Contains("Step 3"));

        // Current-step content belongs to the wizard body rather than the step navigation.
        var body = cut.Find(".omni-wizard__body");
        Assert.Contains("Content of step 1", body.TextContent);
        Assert.DoesNotContain("Content of step 2", cut.Markup);
        Assert.DoesNotContain(cut.FindAll(".omni-steps__panel"), panel => panel.TextContent.Contains("Content of step"));
    }

    /// <summary>R2-23 / R2-16: the page-height rules of app.css address the wizard through its root class and
    /// OE's own body and foot; the body takes its height from the stylesheet, never from an inline style, and
    /// the foot is OE's single band of actions.</summary>
    [Fact]
    public void Renders_TheClassesThePageHeightRulesAddress_WithNoFixedBodyHeight()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(3)));

        var root = cut.Find(".wizard-dialog");
        Assert.Single(root.QuerySelectorAll(":scope > .omni-wizard"));
        var body = root.QuerySelector(".omni-wizard > .omni-wizard__body");
        Assert.NotNull(body);
        Assert.Null(body.GetAttribute("style"));
        Assert.Single(cut.FindAll(".omni-wizard > .omni-wizard__foot > .omni-wizard__actions"));
    }

    [Fact]
    public void Renders_NoAetheusActionBarOrProgressTrackOverride()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(3)));

        // The wizard uses OE's action footer.
        Assert.Empty(cut.FindAll(".wizard-actions"));
        Assert.Empty(cut.FindAll(".wizard-progress"));
        Assert.NotNull(cut.Find(".omni-wizard__actions"));
    }

    [Fact]
    public async Task Progress_StartsAtZero_AndReachesOneHundredOnLastStep()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(3)));

        Assert.Equal("0", cut.Find(".omni-wizard [role='progressbar']").GetAttribute("aria-valuenow"));

        await cut.InvokeAsync(cut.Instance.NextStep);
        await cut.InvokeAsync(cut.Instance.NextStep);

        Assert.Equal("100", cut.Find(".omni-wizard [role='progressbar']").GetAttribute("aria-valuenow"));
    }

    [Fact]
    public void FirstStep_ShowsNextAndCancel_NoPrevious()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(3)));

        Assert.NotNull(Button(cut, "omni-wizard__next"));
        Assert.NotNull(Button(cut, "omni-wizard__cancel"));
        Assert.Empty(cut.FindAll(".omni-wizard__previous"));
        Assert.Empty(cut.FindAll(".omni-wizard__finish"));
    }

    [Fact]
    public void FinishButton_OnLastStep_UsesCustomText_AndIsPrimary()
    {
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(1))
            .Add(x => x.FinishText, "Done!"));

        var finish = Button(cut, "omni-wizard__finish");
        Assert.Contains("Done!", finish.TextContent);
        // The last step's main action is blue, never green.
        Assert.Contains("omni-button--primary", finish.ClassList);
    }

    [Fact]
    public void FinishButton_OnLastStep_DefaultsToTheAetheusFinishText()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(1)));

        Assert.Contains("WizardFinish", Button(cut, "omni-wizard__finish").TextContent);
    }

    [Fact]
    public void CanAdvance_False_DisablesNext()
    {
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(3))
            .Add(x => x.CanAdvance, false));

        Assert.True(Button(cut, "omni-wizard__next").HasAttribute("disabled"));
    }

    [Fact]
    public void CanAdvance_True_LeavesNextEnabled()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(3)));

        Assert.False(Button(cut, "omni-wizard__next").HasAttribute("disabled"));
    }

    [Fact]
    public async Task ClickingNext_MovesToTheNextStep_AndReportsIt()
    {
        var fired = -1;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(3))
            .Add(x => x.StepChanged, EventCallback.Factory.Create<int>(this, s => fired = s)));

        await cut.InvokeAsync(() => Button(cut, "omni-wizard__next").Click());

        Assert.Equal(1, cut.Instance.CurrentStep);
        Assert.Equal(1, fired);
        Assert.Contains("Content of step 2", cut.Find(".omni-wizard__body").TextContent);
    }

    [Fact]
    public async Task ClickingPrevious_GoesBack_AndReportsIt()
    {
        var fired = -1;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(3))
            .Add(x => x.StepChanged, EventCallback.Factory.Create<int>(this, s => fired = s)));
        await cut.InvokeAsync(cut.Instance.NextStep);

        await cut.InvokeAsync(() => Button(cut, "omni-wizard__previous").Click());

        Assert.Equal(0, cut.Instance.CurrentStep);
        Assert.Equal(0, fired);
        Assert.Contains("Content of step 1", cut.Find(".omni-wizard__body").TextContent);
    }

    [Fact]
    public async Task ValidateBeforeNext_False_KeepsTheStep()
    {
        var asked = -1;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(3))
            .Add(x => x.ValidateBeforeNext, step => { asked = step; return Task.FromResult(false); }));

        await cut.InvokeAsync(() => Button(cut, "omni-wizard__next").Click());

        Assert.Equal(0, asked);
        Assert.Equal(0, cut.Instance.CurrentStep);
        Assert.False(Button(cut, "omni-wizard__next").HasAttribute("disabled"));
    }

    [Fact]
    public async Task NextStep_FromCode_AsksTheValidator()
    {
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(3))
            .Add(x => x.ValidateBeforeNext, _ => Task.FromResult(false)));

        await cut.InvokeAsync(cut.Instance.NextStep);

        Assert.Equal(0, cut.Instance.CurrentStep);
    }

    [Fact]
    public async Task NextStep_FromCode_MovesTheWizard_AndReportsIt()
    {
        var fired = -1;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(3))
            .Add(x => x.StepChanged, EventCallback.Factory.Create<int>(this, s => fired = s)));

        await cut.InvokeAsync(cut.Instance.NextStep);

        Assert.Equal(1, cut.Instance.CurrentStep);
        Assert.Equal(1, fired);
        Assert.Contains("Content of step 2", cut.Find(".omni-wizard__body").TextContent);
        Assert.NotNull(Button(cut, "omni-wizard__previous"));
    }

    [Fact]
    public async Task NextStep_AtLastStep_DoesNotAdvance()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(1)));

        await cut.InvokeAsync(cut.Instance.NextStep);

        Assert.Equal(0, cut.Instance.CurrentStep);
    }

    [Fact]
    public async Task Cancel_InvokesCallback()
    {
        var cancelled = false;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(2))
            .Add(x => x.OnCancelClicked, () => { cancelled = true; }));

        await cut.InvokeAsync(() => Button(cut, "omni-wizard__cancel").Click());

        Assert.True(cancelled);
    }

    [Fact]
    public async Task Finish_InvokesCallback()
    {
        var finished = false;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(1))
            .Add(x => x.OnFinishClicked, () => { finished = true; }));

        await cut.InvokeAsync(() => Button(cut, "omni-wizard__finish").Click());

        Assert.True(finished);
    }

    [Fact]
    public async Task Escape_InvokesCancel()
    {
        var cancelled = false;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(2))
            .Add(x => x.OnCancelClicked, () => { cancelled = true; }));

        await cut.InvokeAsync(() => cut.Find(".wizard-dialog").KeyDown(new KeyboardEventArgs { Key = "Escape" }));

        Assert.True(cancelled);
    }

    [Fact]
    public async Task OtherKey_DoesNotCancel()
    {
        var cancelled = false;
        var cut = Render<WizardDialog>(p => p
            .Add(x => x.Steps, CreateSteps(2))
            .Add(x => x.OnCancelClicked, () => { cancelled = true; }));

        await cut.InvokeAsync(() => cut.Find(".wizard-dialog").KeyDown(new KeyboardEventArgs { Key = "Enter" }));

        Assert.False(cancelled);
    }

    [Fact]
    public void Wrapper_TakesNoFocusStopOfItsOwn()
    {
        var cut = Render<WizardDialog>(p => p.Add(x => x.Steps, CreateSteps(2)));

        // The wrapper is not a tab stop because rendering does not assign focus.
        Assert.False(cut.Find(".wizard-dialog").HasAttribute("tabindex"));
    }
}
