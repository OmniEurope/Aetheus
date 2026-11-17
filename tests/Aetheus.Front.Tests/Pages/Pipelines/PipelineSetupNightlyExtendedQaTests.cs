// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>The nightly the wizard writes with its defaults (extended QA unticked) declares extended QA off and names no
/// extended QA pipeline, which does not exist. The launch preflight reads that "false" to skip the stage that triggers it
/// (<c>PipelineRunPreflightServiceTests</c>), so the two ends stay consistent.</summary>
public sealed class PipelineSetupNightlyExtendedQaTests
{
    private static PipelineSetupYamlInput Input(bool qaExtended, params string[] created) => new(
        "atlas", (true, true, false), "package-web-analytics-dotnet",
        Rollback: true, Quality: true, Security: true, Qa: true, QaExtended: qaExtended, SecurityHistory: false,
        created);

    [Fact]
    public void NightlyWithExtendedQaOff_DeclaresItOffAndNamesNoExtendedQaPipeline()
    {
        var yaml = PipelineSetupYamlBuilder.Build(
            "atlas-nightly", "application-nightly", 1, Input(qaExtended: false, "ci", "qa", "nightly"));

        Assert.Contains("APPLICATION_QA_EXTENDED_ENABLED: \"false\"", yaml, StringComparison.Ordinal);
        Assert.DoesNotContain("APPLICATION_QA_EXTENDED_PIPELINE", yaml, StringComparison.Ordinal);
    }

    /// <summary>Wordmorrow's nightly (2026-11-11) never ran: the wizard wrote <c>trigger: manual</c>, and the scheduler reads
    /// the pipeline's own document only, not the template's schedule.</summary>
    [Fact]
    public void Nightly_IsScheduledInItsOwnDocument_OtherPipelinesStayManual()
    {
        var nightly = PipelineSetupYamlBuilder.Build(
            "atlas-nightly", "application-nightly", 1, Input(qaExtended: false, "ci", "qa", "nightly"));
        var ci = PipelineSetupYamlBuilder.Build(
            "atlas-ci", "application-ci", 1, Input(qaExtended: false, "ci", "qa", "nightly"));

        Assert.Contains("trigger: schedule\nschedule: \"0 0 2 * * *\"\n", nightly, StringComparison.Ordinal);
        Assert.DoesNotContain("trigger: manual", nightly, StringComparison.Ordinal);
        Assert.Contains("trigger: manual\n", ci, StringComparison.Ordinal);
    }

    [Fact]
    public void NightlyWithExtendedQaOn_DeclaresItOnAndNamesTheCreatedPipeline()
    {
        var yaml = PipelineSetupYamlBuilder.Build(
            "atlas-nightly", "application-nightly", 1, Input(qaExtended: true, "ci", "qa", "qa-extended", "nightly"));

        Assert.Contains("APPLICATION_QA_EXTENDED_ENABLED: \"true\"", yaml, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_QA_EXTENDED_PIPELINE: \"atlas-qa-extended\"", yaml, StringComparison.Ordinal);
    }
}
