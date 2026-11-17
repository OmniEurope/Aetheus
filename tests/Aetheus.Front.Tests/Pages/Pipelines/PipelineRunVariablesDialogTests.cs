// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-31: the run's variables dialog names the clone address as the back's, since the agent that runs a stage
/// replaces its host and port with the address it reaches the back with before cloning.</summary>
public sealed class PipelineRunVariablesDialogTests : BunitContext
{
    public PipelineRunVariablesDialogTests() => BunitTestHelper.RegisterServices(this);

    [Fact]
    public void TheCloneAddressVariables_AreNamedAsTheBacksAddress_WithAnExplanation()
    {
        var cut = Render<PipelineRunVariablesDialog>(p => p.Add(x => x.Variables, new Dictionary<string, string>
        {
            ["BUILD_REPOSITORY_URI"] = "http://host.docker.internal:5300/git/1/app.git",
            ["REPOSITORY_URL"] = "http://host.docker.internal:5300/git/1/app.git",
            ["BUILD_PROJECTNAME"] = "app",
            ["DEFAULT_BRANCH"] = "main"
        }));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='run-variables-clone-address-note']")));
        Assert.Equal(2, cut.FindAll("[data-testid='run-variable-back-address']").Count);
        Assert.Contains("VarSourceBackAddress", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("VarSourceSystem", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutACloneAddress_NoNoteAndNoBackAddressBadge()
    {
        var cut = Render<PipelineRunVariablesDialog>(p => p.Add(x => x.Variables, new Dictionary<string, string>
        {
            ["BUILD_PROJECTNAME"] = "app"
        }));

        Assert.Empty(cut.FindAll("[data-testid='run-variables-clone-address-note']"));
        Assert.Empty(cut.FindAll("[data-testid='run-variable-back-address']"));
    }

    [Theory]
    [InlineData("REPOSITORY_URL", true)]
    [InlineData("BUILD_REPOSITORY_URI", true)]
    [InlineData("repository_url", true)]
    [InlineData("BUILD_SOURCEBRANCH", false)]
    public void OnlyTheTwoCloneAddressVariables_AreTheBacksAddress(string key, bool expected) =>
        Assert.Equal(expected, PipelineRunVariablesDialog.IsBackCloneAddress(key));
}
