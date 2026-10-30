// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Bunit.Rendering;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Sections;

namespace Aetheus.Front.Tests.TestDoubles;

/// <summary>
/// Renders a component next to the section outlets a page layout gives it (the page header's actions,
/// for one), so what the component sends there through <c>SectionContent</c> is rendered and clickable.
/// </summary>
internal static class PageHeaderHost
{
    public static IRenderedComponent<ContainerFragment> RenderWithOutlets<TComponent>(
        this BunitContext context, IReadOnlyDictionary<string, object?> parameters, params string[] outlets)
        where TComponent : IComponent
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Render(builder =>
        {
            var sequence = 0;
            foreach (var outlet in outlets)
            {
                builder.OpenElement(sequence++, "div");
                builder.AddAttribute(sequence++, "class", "test-outlet");
                builder.AddAttribute(sequence++, "data-outlet", outlet);
                builder.OpenComponent<SectionOutlet>(sequence++);
                builder.AddAttribute(sequence++, nameof(SectionOutlet.SectionName), outlet);
                builder.CloseComponent();
                builder.CloseElement();
            }
            builder.OpenComponent<TComponent>(100);
            builder.AddMultipleAttributes(101, parameters!);
            builder.CloseComponent();
        });
    }

    /// <summary>
    /// Recette R-29: renders a server section the way <c>ServerDetailLayout</c> does, with the page header's
    /// actions outlet before it, so its main action and its menu (sent to the header) are found and clicked
    /// through the returned host. The section itself is <c>host.FindComponent&lt;TComponent&gt;()</c>.
    /// </summary>
    public static IRenderedComponent<PageHeaderOutletHost> RenderWithPageHeader<TComponent>(
        this BunitContext context, Action<ComponentParameterCollectionBuilder<TComponent>> parameters)
        where TComponent : IComponent
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Render<PageHeaderOutletHost>(host => host.AddChildContent(parameters));
    }
}

/// <summary>The page header's actions outlet, then the page: what <c>ServerDetailLayout</c> gives a section.</summary>
internal sealed class PageHeaderOutletHost : ComponentBase
{
    [Parameter] public RenderFragment? ChildContent { get; set; }

    protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "div");
        builder.AddAttribute(1, "class", "test-outlet");
        builder.AddAttribute(2, "data-outlet", Aetheus.Front.Components.Shared.PageHeaderActions.SectionName);
        builder.OpenComponent<SectionOutlet>(3);
        builder.AddAttribute(4, nameof(SectionOutlet.SectionName), Aetheus.Front.Components.Shared.PageHeaderActions.SectionName);
        builder.CloseComponent();
        builder.CloseElement();
        builder.OpenElement(6, "div");
        builder.AddAttribute(7, "class", "test-outlet");
        builder.AddAttribute(8, "data-outlet", Aetheus.Front.Components.Shared.PageHeaderActions.BadgesSectionName);
        builder.OpenComponent<SectionOutlet>(9);
        builder.AddAttribute(10, nameof(SectionOutlet.SectionName), Aetheus.Front.Components.Shared.PageHeaderActions.BadgesSectionName);
        builder.CloseComponent();
        builder.CloseElement();
        builder.AddContent(5, ChildContent);
    }
}
