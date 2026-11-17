// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests;

/// <summary>
/// The bUnit context of this project, under the name <c>BunitContext</c> (GlobalUsings.cs). The front is a
/// standalone WebAssembly app, always interactive, and OE's grid reads RendererInfo on every render, which
/// bUnit refuses until it is set. Setting it builds the renderer, which closes the service collection, so it
/// waits for the first render, once the test has registered its services.
/// </summary>
public class InteractiveBunitContext : Bunit.BunitContext
{
    private bool _rendererInfoSet;

    public new void SetRendererInfo(RendererInfo info)
    {
        base.SetRendererInfo(info);
        _rendererInfoSet = true;
    }

    public override IRenderedComponent<TComponent> Render<TComponent>(
        Action<ComponentParameterCollectionBuilder<TComponent>>? parameterBuilder = null)
    {
        EnsureRendererInfo();
        return base.Render(parameterBuilder);
    }

    public override IRenderedComponent<TComponent> Render<TComponent>(RenderFragment renderFragment)
    {
        EnsureRendererInfo();
        return base.Render<TComponent>(renderFragment);
    }

    public override IRenderedComponent<ContainerFragment> Render(RenderFragment renderFragment)
    {
        EnsureRendererInfo();
        return base.Render(renderFragment);
    }

    private void EnsureRendererInfo()
    {
        if (!_rendererInfoSet)
            SetRendererInfo(new RendererInfo("WebAssembly", isInteractive: true));
    }
}
