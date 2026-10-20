// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Resources;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Services;

/// <summary>Verifies the ApiStatus overload of UiActions.RunAsync.</summary>
public class UiActionsApiStatusTests
{
    private readonly OmniOverlayService _overlay = new(new FakeTimeProvider());
    private readonly NotifyHelper _toast;
    private readonly UiActions _sut;

    public UiActionsApiStatusTests()
    {
        var localizer = Substitute.For<IStringLocalizer<AppStrings>>();
        localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString((string)ci[0], (string)ci[0]));
        _toast = new NotifyHelper(_overlay, localizer);
        _sut = new UiActions(_toast);
    }

    // ── Success path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ApiStatus_Success_ReturnsTrue()
    {
        var result = await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(true, false, false)),
            "Saved");

        Assert.True(result);
        var message = Assert.Single(_overlay.Toasts());
        Assert.Equal(OmniSeverity.Success, message.Severity);
        Assert.Equal("Saved", message.Summary);
    }

    [Fact]
    public async Task RunAsync_ApiStatus_Success_InvokesOnSuccess()
    {
        var called = false;
        await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(true, false, false)),
            "Saved",
            onSuccess: () => { called = true; return Task.CompletedTask; });

        Assert.True(called);
    }

    [Fact]
    public async Task RunAsync_ApiStatus_Success_WithNullOnSuccess_LeavesTheFlagOn()
    {
        var result = await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(true, false, false)),
            "Saved",
            onSuccess: null);

        Assert.True(result);
    }

    // ── Failure path ─────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ApiStatus_Failure_ReturnsFalse()
    {
        var result = await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(false, false, false)),
            "Deleted");

        Assert.False(result);
        var message = Assert.Single(_overlay.Toasts());
        Assert.Equal(OmniSeverity.Danger, message.Severity);
        Assert.Equal("SaveFailed", message.Detail);
    }

    [Fact]
    public async Task RunAsync_ApiStatus_Failure_DoesNotInvokeOnSuccess()
    {
        var called = false;
        await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(false, false, false)),
            "Deleted",
            onSuccess: () => { called = true; return Task.CompletedTask; });

        Assert.False(called);
    }

    [Fact]
    public async Task RunAsync_ApiStatus_NotFound_ReturnsFalse()
    {
        var result = await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(false, true, false)),
            "Deleted");

        Assert.False(result);
        Assert.Equal(OmniSeverity.Danger, Assert.Single(_overlay.Toasts()).Severity);
    }

    [Fact]
    public async Task RunAsync_ApiStatus_Forbidden_ReturnsFalse()
    {
        var result = await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(false, false, true)),
            "Saved");

        Assert.False(result);
        Assert.Equal(OmniSeverity.Danger, Assert.Single(_overlay.Toasts()).Severity);
    }

    // ── Custom keys ──────────────────────────────────────────────────────────

    [Fact]
    public async Task RunAsync_ApiStatus_CustomKeys_ReturnsTrue()
    {
        var result = await _sut.RunAsync(
            () => Task.FromResult(new ApiStatus(true, false, false)),
            successKey: "CustomSuccess",
            errorKey: "CustomError",
            errorTitleKey: "CustomErrTitle",
            successTitleKey: "CustomSuccTitle");

        Assert.True(result);
        var message = Assert.Single(_overlay.Toasts());
        Assert.Equal("CustomSuccTitle", message.Summary);
        Assert.Equal("CustomSuccess", message.Detail);
    }
}
