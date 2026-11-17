// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-33: the wizard writes the release series into the pipelines that publish a release, the launch dialog shows
/// the version a launch without one takes (greyed, the field stays empty so the run allocates it), and the summary says
/// which of the project's pipelines a newer template version exists for, with the link to its update preview.</summary>
public sealed class PipelineSetupReleaseVersionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineSetupReleaseVersionTests() => _handler = BunitTestHelper.RegisterServices(this);

    private static PipelineSetupYamlInput Input() => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: false, SecurityHistory: false,
        ["ci", "candidate", "deploy-prod", "release-fast"]);

    [Theory]
    [InlineData("application-candidate", true)]
    [InlineData("application-release-fast", true)]
    [InlineData("application-ci", false)]
    [InlineData("application-deploy-prod", false)]
    public void TheWizard_WritesTheVersionSeries_IntoThePipelinesThatPublishARelease(string template, bool written)
    {
        var yaml = PipelineSetupYamlBuilder.Build("atlas-x", template, 5, Input());

        Assert.Equal(written, yaml.Contains("  APPLICATION_VERSION_PREFIX: \"0.1\"\n", StringComparison.Ordinal));
    }

    [Fact]
    public void TheLaunchDialog_ShowsTheProposedVersion_AndLeavesTheFieldEmpty()
    {
        var cut = Render<RunParameterFields>(p => p.Add(x => x.Parameters, new List<PipelineRunParameterDto>
        {
            new() { Name = "applicationVersion", DisplayName = "Release version", Type = "string", Default = "", Placeholder = "0.1.7" }
        }));

        Assert.Equal("0.1.7", cut.Find("input#applicationVersion").GetAttribute("placeholder"));
        Assert.True(cut.Instance.TryCollect(out var values, out _));
        Assert.True(string.IsNullOrEmpty(values.GetValueOrDefault("applicationVersion")));
    }

    [Fact]
    public void TheSummary_LinksEachOutdatedPipelineToItsUpdatePreview()
    {
        _handler.SetPaginatedJsonResponse("api/pipelines/fleet?page=1&pageSize=200&projectId=7&freshness=Outdated",
        [
            new PipelineFleetItemDto
            {
                PipelineId = 42, PipelineName = "atlas-candidate", ProjectId = 7, TemplateName = "application-candidate",
                PinnedVersion = 4, LatestVersion = 5, Freshness = PipelineFleetFreshness.Outdated
            }
        ]);

        var cut = Render<PipelineSetupTemplateUpdates>(p => p.Add(x => x.ProjectId, 7));

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='setup-template-update-link']")));
        var link = cut.Find("[data-testid='setup-template-update-link']");
        Assert.Equal("/pipelines/42/template/update/5", link.GetAttribute("href"));
    }

    [Fact]
    public void TheSummary_SaysNothing_WhenEveryPipelineIsUpToDate()
    {
        var cut = Render<PipelineSetupTemplateUpdates>(p => p.Add(x => x.ProjectId, 7));

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.Contains("api/pipelines/fleet", StringComparison.Ordinal)));
        Assert.Empty(cut.FindAll("[data-testid='setup-template-updates']"));
    }
}
