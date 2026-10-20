// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>AetheusLoader supplies the plane, wording and layout to OE's logo loader, which owns
/// sizing and reduced-motion behaviour.</summary>
public sealed class AetheusLoaderTests : BunitContext
{
    public AetheusLoaderTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void Default_RendersTheOeLogoLoaderWithTheAnimatedPlaneAndTheLoadingWords()
    {
        var cut = Render<AetheusLoader>();

        var loader = cut.FindComponent<OmniLogoLoader>();
        Assert.Equal(OmniControlSize.Medium, loader.Instance.Size);
        Assert.Equal("Loading", loader.Instance.Label);
        Assert.True(loader.Instance.AnimatedMark);

        var root = cut.Find(".omni-logo-loader");
        Assert.Equal("status", root.GetAttribute("role"));
        Assert.Contains("aetheus-loader", root.ClassList);
        Assert.Contains("aetheus-loader-centered", root.ClassList);
        Assert.Contains("omni-logo-loader--medium", root.ClassList);
        Assert.Contains("omni-logo-loader--animated", root.ClassList);

        // The plane is decorative inside OE's hidden mark; the state is said by the visually hidden words.
        // Its motion is the splash's CSS keyframes (app.css), never SMIL, which reduced motion cannot stop.
        var logo = cut.Find(".omni-logo-loader__mark > svg.aetheus-loader-logo");
        foreach (var part in new[] { ".p-ship", ".p-drift", ".p-roll", ".p-jet" })
            Assert.NotEmpty(logo.QuerySelectorAll(part));
        Assert.Empty(logo.QuerySelectorAll("animate, animateTransform, animateMotion, set"));
        Assert.Equal("true", cut.Find(".omni-logo-loader__mark").GetAttribute("aria-hidden"));
        Assert.Equal("Loading", cut.Find(".omni-logo-loader .omni-visually-hidden").TextContent);
    }

    [Theory]
    [InlineData(OmniControlSize.Small, "omni-logo-loader--small")]
    [InlineData(OmniControlSize.Large, "omni-logo-loader--large")]
    public void Size_IsOesSize(OmniControlSize size, string expectedClass)
    {
        var cut = Render<AetheusLoader>(parameters => parameters.Add(p => p.Size, size));

        Assert.Contains(expectedClass, cut.Find(".omni-logo-loader").ClassList);
    }

    [Fact]
    public void NotCentered_StaysInlineAndKeepsTheCallersClass()
    {
        var cut = Render<AetheusLoader>(parameters => parameters
            .Add(p => p.Centered, false)
            .Add(p => p.Class, "omni-u-mb-sm"));

        var root = cut.Find(".omni-logo-loader");
        Assert.DoesNotContain("aetheus-loader-centered", root.ClassList);
        Assert.Contains("omni-u-mb-sm", root.ClassList);
    }
}
