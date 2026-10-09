// SPDX-License-Identifier: EUPL-1.2
using System.Collections.Concurrent;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace Aetheus.Front.Tests.Layout;

/// <summary>
/// Recette R2-087: every navigation is reported to the shell's scroll helper, before it (the page left
/// keeps its position) and after it (a return gets it back), in that order.
/// </summary>
public sealed class PageScrollMemoryTests : BunitContext
{
    [Fact]
    public void ANavigation_IsReportedBeforeAndAfter()
    {
        var js = new RecordingJsRuntime();
        var nav = Services.GetRequiredService<NavigationManager>();
        using var memory = new PageScrollMemory(nav, js);

        nav.NavigateTo("/pipelines/5");
        Assert.True(SpinWait.SpinUntil(() => js.Calls.Count >= 2, TimeSpan.FromSeconds(2)));
        nav.NavigateTo("/pipelines/runs/77");
        Assert.True(SpinWait.SpinUntil(() => js.Calls.Count >= 4, TimeSpan.FromSeconds(2)));

        Assert.Equal(
            ["Aetheus.pageScroll.leaving", "Aetheus.pageScroll.arrived", "Aetheus.pageScroll.leaving", "Aetheus.pageScroll.arrived"],
            js.Calls);
    }

    [Fact]
    public void Disposed_ItReportsNothingMore()
    {
        var js = new RecordingJsRuntime();
        var nav = Services.GetRequiredService<NavigationManager>();
        new PageScrollMemory(nav, js).Dispose();

        nav.NavigateTo("/servers/9");

        Assert.False(SpinWait.SpinUntil(() => !js.Calls.IsEmpty, TimeSpan.FromMilliseconds(200)));
    }

    /// <summary>Keeps the order of the calls, which bUnit's invocation record groups by identifier.</summary>
    private sealed class RecordingJsRuntime : IJSRuntime
    {
        public ConcurrentQueue<string> Calls { get; } = new();

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Enqueue(identifier);
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
