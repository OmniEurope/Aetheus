// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Shared;
using Bunit;

namespace Aetheus.Front.Tests.Shared;

/// <summary>F-E2E-001: the empty-install smoke waits for the projects page's own empty state, so the
/// test id a page puts on EmptyState must reach the rendered markup.</summary>
public sealed class EmptyStateAttributesTests : BunitContext
{
    public EmptyStateAttributesTests() => BunitTestHelper.RegisterServices(this, authenticated: false);

    [Fact]
    public void ATestIdPutOnTheEmptyState_IsRenderedOnItsRoot()
    {
        var cut = Render<EmptyState>(parameters => parameters
            .Add(p => p.Title, "Nothing yet")
            .AddUnmatched("data-testid", "projects-empty"));

        var root = cut.Find("[data-testid='projects-empty']");
        Assert.Contains("empty-state", root.ClassList);
        Assert.Contains("Nothing yet", root.TextContent);
    }
}
