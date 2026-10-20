// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>After page disposal, pending action continuations must receive a cancelled token
/// without accessing a disposed source.</summary>
public sealed class PipelineRunLoadSessionTests
{
    [Fact]
    public void AfterTheRunPageIsLeft_TheTokenIsCancelled_AndReadingItDoesNotThrow()
    {
        var session = new PipelineRunLoadSession();
        var (_, firstLoad) = session.Begin();

        session.Dispose();

        Assert.True(firstLoad.IsCancellationRequested);
        var token = session.Token;
        Assert.True(token.IsCancellationRequested);
        // A second dispose (the page's DisposeAsync running twice) is harmless too.
        session.Dispose();
        Assert.True(session.Begin().Token.IsCancellationRequested);
    }

    [Fact]
    public void WhileThePageIsOpen_EachLoadCancelsThePreviousOne()
    {
        using var session = new PipelineRunLoadSession();
        var (firstGeneration, first) = session.Begin();
        var (secondGeneration, second) = session.Begin();

        Assert.True(first.IsCancellationRequested);
        Assert.False(second.IsCancellationRequested);
        Assert.False(session.IsCurrent(firstGeneration));
        Assert.True(session.IsCurrent(secondGeneration));
        Assert.Equal(second, session.Token);
    }
}
