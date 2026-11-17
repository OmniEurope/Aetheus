// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>A reader who scrolls a live run's logs up by hand is not dragged back to the newest line.</summary>
public sealed class PipelineRunLogFollowTests : BunitContext
{
    private static readonly ElementReference Terminal = new("terminal-1");

    public PipelineRunLogFollowTests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private int Pins() => JSInterop.Invocations.Count(call => call.Identifier == "Aetheus.scrollToBottom");

    /// <summary>R-21, fresh load: the log is filled before the pane is drawn, so the first render has no terminal
    /// to scroll. That render must not consume the line count, or the log stays at its top and the virtualized
    /// list never renders the last lines (71 of 89 rows, the tail hidden).</summary>
    [Fact]
    public async Task ARenderBeforeTheTerminalIsMounted_DoesNotConsumeThePin()
    {
        using var follow = new PipelineRunLogFollow(JSInterop.JSRuntime, () => Task.CompletedTask);

        await follow.AfterRenderAsync(default, 89);
        Assert.Equal(0, Pins());

        await follow.AfterRenderAsync(Terminal, 89);
        Assert.Equal(1, Pins());
    }

    [Fact]
    public async Task WhenTheListReachesItsFullHeight_FollowingPinsToTheTailAgain_AReaderAwayFromItIsLeftAlone()
    {
        using var follow = new PipelineRunLogFollow(JSInterop.JSRuntime, () => Task.CompletedTask);
        await follow.AfterRenderAsync(Terminal, 89);

        await follow.RepinAsync(Terminal);
        Assert.Equal(2, Pins());

        await follow.OnUserScrolled(atBottom: false);
        await follow.RepinAsync(Terminal);
        Assert.Equal(2, Pins());
    }

    [Fact]
    public async Task ScrollingUpByHand_StopsPinning_AndReachingTheTailAgainResumesIt()
    {
        var changes = 0;
        using var follow = new PipelineRunLogFollow(JSInterop.JSRuntime, () => { changes++; return Task.CompletedTask; });

        await follow.AfterRenderAsync(Terminal, 10);
        Assert.Equal(1, Pins());
        Assert.Single(JSInterop.Invocations, call => call.Identifier == "Aetheus.watchLogFollow");

        await follow.OnUserScrolled(atBottom: false);
        await follow.AfterRenderAsync(Terminal, 20);
        await follow.AfterRenderAsync(Terminal, 30);
        Assert.False(follow.Following);
        Assert.Equal(1, Pins());

        await follow.OnUserScrolled(atBottom: true);
        await follow.AfterRenderAsync(Terminal, 40);
        Assert.True(follow.Following);
        Assert.Equal(2, Pins());
        Assert.Equal(2, changes);
        // The terminal is hooked once, not on every render.
        Assert.Single(JSInterop.Invocations, call => call.Identifier == "Aetheus.watchLogFollow");
    }
}
