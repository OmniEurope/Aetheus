// SPDX-License-Identifier: EUPL-1.2
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Shared;

/// <summary>An export is shown before it is kept, with download, copy (in the "More actions" menu) and close.</summary>
public sealed class TextExportDialogTests : BunitContext
{
    public TextExportDialogTests() => BunitTestHelper.RegisterServices(this);

    private IRenderedComponent<TextExportDialog> RenderDialog() => Render<TextExportDialog>(parameters => parameters
        .Add(dialog => dialog.Text, "# Findings\n- First finding")
        .Add(dialog => dialog.FileName, "run-1-findings.md")
        .Add(dialog => dialog.ContentType, "text/markdown"));

    [Fact]
    public void ShowsTheWholeText_ReadOnly()
    {
        var cut = RenderDialog();

        var area = cut.Find("textarea");
        Assert.Contains("First finding", area.GetAttribute("value") ?? area.TextContent, StringComparison.Ordinal);
        Assert.True(area.HasAttribute("readonly"));
    }

    [Fact]
    public void Download_WritesTheSameTextUnderTheGivenName()
    {
        var cut = RenderDialog();

        cut.FindAll("button").Single(button => button.TextContent.Contains("Download", StringComparison.Ordinal)).Click();

        var download = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "downloadFile");
        Assert.Equal("run-1-findings.md", download.Arguments[0]);
        Assert.Equal("# Findings\n- First finding", download.Arguments[1]);
        Assert.Equal("text/markdown", download.Arguments[2]);
    }

    [Fact]
    public void Copy_PutsTheTextOnTheClipboard()
    {
        var cut = RenderDialog();

        cut.FindMenuItem("Copy")!.Click();

        var copy = Assert.Single(JSInterop.Invocations, invocation => invocation.Identifier == "Aetheus.copyToClipboard");
        Assert.Equal("# Findings\n- First finding", copy.Arguments[0]);
    }

    [Theory]
    [InlineData(true, OmniSeverity.Success)]
    [InlineData(false, OmniSeverity.Danger)]
    public void Copy_SaysCopiedOnlyWhenTheBrowserAcceptedTheWrite(bool accepted, OmniSeverity expected)
    {
        JSInterop.Setup<bool>("Aetheus.copyToClipboard", _ => true).SetResult(accepted);
        var cut = RenderDialog();

        cut.FindMenuItem("Copy")!.Click();

        var message = Assert.Single(Services.Toasts());
        Assert.Equal(expected, message.Severity);
    }
}
