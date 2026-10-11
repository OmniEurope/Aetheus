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
}
