// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using Aetheus.Front.Layout;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages;

/// <summary>An error card belongs to the page where the error happened: the layout's error boundary
/// is reset when the user navigates, so it never follows them to the next page.</summary>
public sealed class MainLayoutErrorRecoveryTests : BunitContext
{
    [Fact]
    public void AnErrorCardShownOnOnePage_IsClearedByTheNextNavigation()
    {
        var handler = BunitTestHelper.RegisterServices(this);
        handler.SetResponse(HttpMethod.Post, "api/client-errors", HttpStatusCode.OK);
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo("/pipelines");
        var gate = new FailureGate { RemainingFailures = 1 };

        var cut = Render<MainLayout>(parameters => parameters
            .Add(layout => layout.Body, (RenderFragment)(builder =>
            {
                builder.OpenComponent<FailingOnceBody>(0);
                builder.AddAttribute(1, nameof(FailingOnceBody.Gate), gate);
                builder.CloseComponent();
            })));

        // The layout shows its body only after its session bootstrap: on a loaded run this wait failed
        // before its first check ran (bUnit "Check count: 0"), so it takes the loaded-machine ceiling.
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".error-card")));

        cut.InvokeAsync(() => nav.NavigateTo("/logs"));

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".error-card"));
            Assert.NotEmpty(cut.FindAll(".recovered-body"));
        });
    }

    private sealed class FailureGate
    {
        public int RemainingFailures { get; set; }
    }

    private sealed class FailingOnceBody : ComponentBase
    {
        [Parameter] public FailureGate Gate { get; set; } = default!;

        protected override void OnInitialized()
        {
            if (Gate.RemainingFailures-- > 0)
                throw new InvalidOperationException("failure raised on the first page");
        }

        protected override void BuildRenderTree(Microsoft.AspNetCore.Components.Rendering.RenderTreeBuilder builder)
        {
            builder.OpenElement(0, "div");
            builder.AddAttribute(1, "class", "recovered-body");
            builder.CloseElement();
        }
    }
}
