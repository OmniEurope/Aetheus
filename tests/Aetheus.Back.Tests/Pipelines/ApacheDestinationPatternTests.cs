// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>An Apache configuration step writes only a bare <c>.conf</c> file name, possibly built from
/// pipeline variables; anything that could leave the sites directory is refused.</summary>
public sealed class ApacheDestinationPatternTests
{
    [Theory]
    [InlineData("shop.conf")]
    [InlineData("shop-$(ENVIRONMENT).conf")]
    [InlineData("$(APP_NAME:-shop).conf")]
    [InlineData("$(APP_NAME)-$(COLOUR).conf")]
    public void ABareConfFileName_IsAccepted(string value) =>
        Assert.True(PipelineDefinitionValidator.IsSafeApacheDestinationPattern(value));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sites/shop.conf")]
    [InlineData("..\\shop.conf")]
    [InlineData("shop..conf")]
    [InlineData("shop.txt")]
    [InlineData("$(1BAD).conf")]
    [InlineData("-shop.conf")]
    public void AnythingElse_IsRefused(string value) =>
        Assert.False(PipelineDefinitionValidator.IsSafeApacheDestinationPattern(value));

    [Fact]
    public void AnOverlongName_IsRefused() =>
        Assert.False(PipelineDefinitionValidator.IsSafeApacheDestinationPattern(new string('a', 216) + ".conf"));
}
