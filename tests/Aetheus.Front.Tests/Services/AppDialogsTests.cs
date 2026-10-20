// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests;

/// <summary> AppDialogs opens a component with its parameters through the real <see
/// cref="OmniOverlayService"/>, shown by a real <see cref="OmniComponentsHost"/>; the component,
/// injecting the same scoped AppDialogs, reports the outcome with <see cref="AppDialogs.Close"/>.
/// </summary>
public sealed class AppDialogsTests : BunitContext
{
    private readonly IRenderedComponent<OmniComponentsHost> _host;
    private readonly AppDialogs _dialogs;

    public AppDialogsTests()
    {
        BunitTestHelper.RegisterServices(this);
        var overlay = Services.GetRequiredService<OmniOverlayService>();
        _host = Render<OmniComponentsHost>(parameters => parameters.Add(host => host.OverlayService, overlay));
        _dialogs = Services.GetRequiredService<AppDialogs>();
    }

    private static Dictionary<string, object?> ProbeParameters(string text, int count, object? result = null) => new()
    {
        [nameof(Probe.Text)] = text,
        [nameof(Probe.Count)] = count,
        [nameof(Probe.Result)] = result
    };

    [Fact]
    public void OpenAsync_RendersTheComponentWithItsParameters_UnderTheTitle()
    {
        _ = _dialogs.OpenAsync<Probe>("Edit server", ProbeParameters("web-01", 3));

        _host.WaitForAssertion(() => Assert.Equal("web-01/3", _host.Find(".omni-dialog .probe-text").TextContent));
        Assert.Equal("Edit server", _host.Find(".omni-dialog__title").TextContent);
    }

    [Fact]
    public void OpenAsync_PutsTheFocusOnThePanel_NotOnAField()
    {
        _ = _dialogs.OpenAsync<Probe>("Edit server", ProbeParameters("web-01", 3));

        _host.WaitForAssertion(() => Assert.Single(_host.FindComponents<OmniDialog>()));
        Assert.Equal(OmniDialogInitialFocus.Panel, _host.FindComponent<OmniDialog>().Instance.InitialFocus);
    }

    [Fact]
    public void Alert_PutsTheFocusOnThePanel()
    {
        var service = new OmniDialogService(_dialogs, Services.GetRequiredService<OmniOverlayService>());

        _ = service.Alert("The export is ready.", "Export");

        _host.WaitForAssertion(() => Assert.Single(_host.FindComponents<OmniDialog>()));
        Assert.Equal(OmniDialogInitialFocus.Panel, _host.FindComponent<OmniDialog>().Instance.InitialFocus);
    }

    [Theory]
    [InlineData(null, "aetheus-dialog--default")]
    [InlineData(AppDialogWidth.Default, "aetheus-dialog--default")]
    [InlineData(AppDialogWidth.Narrow, "aetheus-dialog--narrow")]
    [InlineData(AppDialogWidth.Wide, "aetheus-dialog--wide")]
    [InlineData(AppDialogWidth.ExtraWide, "aetheus-dialog--extra-wide")]
    public void OpenAsync_WrapsTheContentInTheClassOfItsWidth(AppDialogWidth? width, string expectedClass)
    {
        var options = width is null ? null : new AppDialogOptions { Width = width.Value };

        _ = _dialogs.OpenAsync<Probe>("Title", ProbeParameters("x", 1), options);

        _host.WaitForAssertion(() => Assert.Single(_host.FindAll(".omni-dialog__content > .aetheus-dialog")));
        var wrapper = _host.Find(".omni-dialog__content > .aetheus-dialog");
        Assert.Contains(expectedClass, wrapper.ClassList);
        Assert.Single(wrapper.QuerySelectorAll(".probe-text"));
    }

    [Fact]
    public void AppCss_WidensTheDialogForEveryWidthButTheDefault()
    {
        // The wrapper class is only worth something if app.css has the rule that reads it.
        var css = File.ReadAllText(Path.Combine(
            Architecture.RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));

        foreach (var width in new[] { AppDialogWidth.Narrow, AppDialogWidth.Wide, AppDialogWidth.ExtraWide })
            Assert.Contains($".omni-dialog:has(.{AppDialogs.WidthClass(width)})", css, StringComparison.Ordinal);
        Assert.DoesNotContain($".{AppDialogs.WidthClass(AppDialogWidth.Default)}", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Close_FromTheContent_HandsItsResultToTheCaller_AndClosesTheDialog()
    {
        var outcome = _dialogs.OpenAsync<Probe>("Title", ProbeParameters("x", 1, result: "saved"));

        _host.WaitForElement(".omni-dialog .probe-close").Click();

        Assert.Equal("saved", await outcome);
        _host.WaitForAssertion(() => Assert.Empty(_host.FindAll(".omni-dialog")));
    }

    [Fact]
    public async Task OpenAsync_DismissedWithTheCross_AnswersNull()
    {
        var outcome = _dialogs.OpenAsync<Probe>("Title", ProbeParameters("x", 1, result: "saved"));

        _host.WaitForElement(".omni-dialog__close").Click();

        Assert.Null(await outcome);
    }

    [Fact]
    public async Task OpenAsyncTyped_ClosedWithAResultOfThatType_AnswersIt()
    {
        var outcome = _dialogs.OpenAsync<Probe, bool?>("Title", ProbeParameters("x", 1, result: true));

        _host.WaitForElement(".omni-dialog .probe-close").Click();

        Assert.True(await outcome);
    }

    [Fact]
    public async Task OpenAsyncTyped_Dismissed_AnswersNull()
    {
        var outcome = _dialogs.OpenAsync<Probe, bool?>("Title", ProbeParameters("x", 1, result: true));

        _host.WaitForElement(".omni-dialog__close").Click();

        Assert.Null(await outcome);
    }

    [Fact]
    public async Task OpenAsyncTyped_ClosedWithAnotherType_AnswersTheDefault()
    {
        var outcome = _dialogs.OpenAsync<Probe, int>("Title", ProbeParameters("x", 1, result: "not a number"));

        _host.WaitForElement(".omni-dialog .probe-close").Click();

        Assert.Equal(0, await outcome);
    }

    [Fact]
    public async Task ConfirmAsync_Destructive_IsARedVerbActionAndAGreyCancel_AnsweringTrueWhenPressed()
    {
        // STD-BTN (kit 676b9a4): a destructive action stays red on the button that confirms it, which
        // names the verb.
        var answer = _dialogs.ConfirmAsync("Delete server", "Delete web-01?", "Delete", "Cancel", destructive: true);

        var action = _host.WaitForElement("button.omni-confirm__action");
        Assert.Contains("omni-button--danger", action.ClassList);
        Assert.Equal("Delete", action.TextContent.Trim());
        var cancel = _host.Find("button.omni-confirm__cancel");
        Assert.Contains("omni-button--secondary", cancel.ClassList);
        Assert.Equal("Cancel", cancel.TextContent.Trim());
        Assert.Contains(_host.FindComponents<OmniIcon>(), icon => icon.Instance.Name == OmniIconName.Delete);
        Assert.Equal("Delete web-01?", _host.Find(".omni-confirm__message").TextContent);
        action.Click();

        Assert.True(await answer);
    }

    [Fact]
    public void R2029_ConfirmAsync_Destructive_IsANeutralDialog_WhoseOnlyWarningIsTheMarkBeforeTheTitle()
    {
        // Use OE warning marks and destructive button colour without application tinting of dialog
        // headers or footers.
        _ = _dialogs.ConfirmAsync("Uninstall", "Uninstall nginx?", "Uninstall", destructive: true);

        var dialog = _host.WaitForElement(".omni-dialog");
        Assert.Contains("omni-dialog--intent-warning", dialog.ClassList);
        Assert.Single(dialog.QuerySelectorAll(".omni-dialog__header > .omni-dialog__intent"));
        Assert.Contains("omni-button--danger", _host.Find("button.omni-confirm__action").ClassList);
        var css = File.ReadAllText(Path.Combine(
            Architecture.RepositoryScan.Root, "src", "Aetheus.Front", "wwwroot", "css", "app.css"));
        Assert.DoesNotContain("var(--omni-dialog-band)", css, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ConfirmAsync_DestructiveWithoutItsVerb_IsRefused()
    {
        // A destructive confirmation never falls back to "Valider".
        await Assert.ThrowsAsync<ArgumentException>(() => _dialogs.ConfirmAsync("Delete server", "Delete web-01?", destructive: true));
    }

    [Fact]
    public async Task ConfirmAsync_WithoutItsVerb_IsRefused()
    {
        // No confirmation falls back to a generic "Valider".
        await Assert.ThrowsAsync<ArgumentException>(() => _dialogs.ConfirmAsync("Promote", "Promote v1.2.3?"));
    }

    [Fact]
    public async Task ConfirmAsync_WithItsVerb_IsABlueVerbAndRevenir_AnsweringFalseOnDismiss()
    {
        var answer = _dialogs.ConfirmAsync("Promote", "Promote v1.2.3?", "Promote");

        var action = _host.WaitForElement("button.omni-confirm__action");
        Assert.Contains("omni-button--primary", action.ClassList);
        Assert.Equal("Promote", action.TextContent.Trim());
        // The test localizer answers the key: "GoBack" is the localized "Revenir".
        Assert.Equal("GoBack", _host.Find("button.omni-confirm__cancel").TextContent.Trim());
        _host.Find("button.omni-confirm__cancel").Click();

        Assert.False(await answer);
    }

    [Fact]
    public void ByDefault_ABackdropClick_LeavesTheDialogOpen()
    {
        // The historical default, kept so a click beside a form does not throw its input away.
        var outcome = _dialogs.OpenAsync<Probe>("Title", ProbeParameters("x", 1, result: "saved"));

        _host.WaitForElement(".omni-overlay").Click();

        Assert.False(outcome.IsCompleted);
        Assert.Single(_host.FindAll(".omni-dialog"));
        Assert.Equal("dialog", _host.Find(".omni-dialog").GetAttribute("role"));
    }

    [Fact]
    public async Task ByDefault_Escape_ClosesTheDialog_AnsweringNull()
    {
        var outcome = _dialogs.OpenAsync<Probe>("Title", ProbeParameters("x", 1, result: "saved"));

        _host.WaitForElement(".omni-dialog").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });

        Assert.Null(await outcome);
    }

    [Fact]
    public async Task CloseOnBackdropClick_True_ABackdropClickClosesTheDialog_AnsweringNull()
    {
        var outcome = _dialogs.OpenAsync<Probe>(
            "Title",
            ProbeParameters("x", 1, result: "saved"),
            new AppDialogOptions { CloseOnBackdropClick = true });

        _host.WaitForElement(".omni-overlay").Click();

        Assert.Null(await outcome);
        _host.WaitForAssertion(() => Assert.Empty(_host.FindAll(".omni-dialog")));
    }

    [Fact]
    public async Task NotDismissible_HasNoCross_IgnoresEscapeAndBackdrop_AndClosesOnlyFromItsContent()
    {
        var outcome = _dialogs.OpenAsync<Probe>(
            "Title",
            ProbeParameters("x", 1, result: "saved"),
            new AppDialogOptions { Dismissible = false, CloseOnBackdropClick = true });

        var dialog = _host.WaitForElement(".omni-dialog");
        Assert.Equal("alertdialog", dialog.GetAttribute("role"));
        Assert.Empty(_host.FindAll(".omni-dialog__close"));

        dialog.KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "Escape" });
        _host.Find(".omni-overlay").Click();
        Assert.False(outcome.IsCompleted);
        Assert.Single(_host.FindAll(".omni-dialog"));

        _host.Find(".omni-dialog .probe-close").Click();
        Assert.Equal("saved", await outcome);
    }

    [Fact]
    public void CompatibilityFacade_PreservesDialogHeightDismissalDragAndResizeOptions()
    {
        var service = Services.GetRequiredService<OmniDialogService>();

        _ = service.OpenAsync<Probe>(
            "Terminal",
            ProbeParameters("x", 1),
            new OmniDialogOptions
            {
                Width = "90vw",
                Height = "85vh",
                ShowClose = false,
                CloseDialogOnEsc = false,
                Draggable = true,
                Resizable = true
            });

        var dialog = _host.WaitForElement(".omni-dialog");
        Assert.Contains("omni-dialog--draggable", dialog.ClassList);
        Assert.Contains("omni-dialog--resizable", dialog.ClassList);
        Assert.Empty(_host.FindAll(".omni-dialog__close"));
        var wrapper = _host.Find(".aetheus-dialog");
        Assert.Contains("aetheus-dialog--extra-wide", wrapper.ClassList);
        Assert.Contains("aetheus-dialog--height-85vh", wrapper.ClassList);
        Assert.Contains("aetheus-dialog--resizable", wrapper.ClassList);
    }

    /// <summary>Dialog content: shows its parameters and closes with the result it was given.</summary>
    private sealed class Probe : ComponentBase
    {
        [Inject] private AppDialogs Dialogs { get; set; } = default!;

        [Parameter] public string Text { get; set; } = string.Empty;

        [Parameter] public int Count { get; set; }

        [Parameter] public object? Result { get; set; }

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "p");
            builder.AddAttribute(1, "class", "probe-text");
            builder.AddContent(2, $"{Text}/{Count}");
            builder.CloseElement();
            builder.OpenElement(3, "button");
            builder.AddAttribute(4, "type", "button");
            builder.AddAttribute(5, "class", "probe-close");
            builder.AddAttribute(6, "onclick", EventCallback.Factory.Create(this, () => Dialogs.Close(Result)));
            builder.AddContent(7, "Close");
            builder.CloseElement();
        }
    }
}
