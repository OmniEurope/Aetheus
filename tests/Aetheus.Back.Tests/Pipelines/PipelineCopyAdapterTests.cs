// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// Recette R1-29: what a pipeline copied from another project becomes. The names the copy knows are
/// rewritten (prefix, copied pipelines, libraries, vaults, <c>APPLICATION_PREFIX</c>); what still ties
/// it to the source project (repository, runner, site host name, addresses, pipelines not copied) is
/// reported, never silently kept.
/// </summary>
public sealed class PipelineCopyAdapterTests
{
    [Theory]
    [InlineData("aetheus-ci", "aetheus", "ats", "ats-ci")]
    [InlineData("aetheus-deploy-prod", "aetheus", "ats", "ats-deploy-prod")]
    [InlineData("nightly", "aetheus", "ats", "ats-nightly")]
    [InlineData("aetheus", "aetheus", "ats", "ats-aetheus")]
    public void NewName_ReplacesTheSourcePrefix_OrPutsTheNewOneInFront(string source, string sourcePrefix, string prefix, string expected) =>
        Assert.Equal(expected, PipelineCopyAdapter.NewName(source, sourcePrefix, prefix));

    [Theory]
    [InlineData("aetheus-prod-host", "ats-prod-host")]
    [InlineData("application-prod-host", "application-prod-host")]
    public void NewResourceName_KeepsAGenericName(string source, string expected) =>
        Assert.Equal(expected, PipelineCopyAdapter.NewResourceName(source, "aetheus", "ats"));

    [Fact]
    public void Adapt_RewritesTheKnownNames_AndTheApplicationPrefix()
    {
        const string yaml = """
            name: aetheus-candidate
            extends: application-candidate@3
            trigger: manual
            variable_libraries:
              - aetheus-prod-host
            vaults:
              - aetheus-deploy
            variables:
              APPLICATION_PREFIX: "aetheus"
            on_success:
              - pipeline: aetheus-deploy-prod
            stages: []
            """;
        var renames = new Dictionary<string, string>
        {
            ["aetheus-candidate"] = "ats-candidate",
            ["aetheus-deploy-prod"] = "ats-deploy-prod",
            ["aetheus-prod-host"] = "ats-prod-host",
            ["aetheus-deploy"] = "ats-deploy"
        };

        var (adapted, issues) = PipelineCopyAdapter.Adapt(yaml, renames, "aetheus", "ats",
            new HashSet<string> { "ats-candidate", "ats-deploy-prod" });

        Assert.Contains("name: ats-candidate", adapted, StringComparison.Ordinal);
        Assert.Contains("- ats-prod-host", adapted, StringComparison.Ordinal);
        Assert.Contains("- ats-deploy\n", adapted.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.Contains("pipeline: ats-deploy-prod", adapted, StringComparison.Ordinal);
        Assert.Contains("APPLICATION_PREFIX: \"ats\"", adapted, StringComparison.Ordinal);
        // The longer name was not cut by the shorter one it starts with.
        Assert.DoesNotContain("ats-deploy-ats", adapted, StringComparison.Ordinal);
        Assert.DoesNotContain("aetheus", adapted, StringComparison.Ordinal);
        Assert.Empty(issues);
    }

    [Fact]
    public void Adapt_ReportsWhatStillTiesTheCopyToTheSourceProject()
    {
        const string yaml = """
            name: aetheus-deploy-prod
            trigger: manual
            source:
              repository: aetheus
            on_success:
              - pipeline: aetheus-smoke
            stages:
              - name: deploy
                agent: vps-aetheus-01
                steps:
                  - name: proxy
                    type: apache-proxy
                    server_name: aetheus.example.org
                    upstream: http://10.0.0.12:8090
                  - name: build
                    run: docker build -t registry/aetheus-back .
            """;
        var renames = new Dictionary<string, string> { ["aetheus-deploy-prod"] = "ats-deploy-prod" };

        var (adapted, issues) = PipelineCopyAdapter.Adapt(yaml, renames, "aetheus", "ats",
            new HashSet<string> { "ats-deploy-prod" });

        Assert.Contains("name: ats-deploy-prod", adapted, StringComparison.Ordinal);
        var kinds = issues.Select(issue => issue.Kind).ToHashSet();
        Assert.Contains(PipelineCopyIssueKind.SourceProjectNameRemains, kinds);
        Assert.Contains(PipelineCopyIssueKind.PinnedAgent, kinds);
        Assert.Contains(PipelineCopyIssueKind.VirtualHostName, kinds);
        Assert.Contains(PipelineCopyIssueKind.SourceRepository, kinds);
        Assert.Contains(PipelineCopyIssueKind.UncopiedPipeline, kinds);
        Assert.Contains(PipelineCopyIssueKind.HardCodedHost, kinds);
        Assert.Contains("aetheus-smoke", issues.Single(issue => issue.Kind == PipelineCopyIssueKind.UncopiedPipeline).Detail, StringComparison.Ordinal);
        Assert.Contains("vps-aetheus-01", issues.Single(issue => issue.Kind == PipelineCopyIssueKind.PinnedAgent).Detail, StringComparison.Ordinal);
        Assert.Contains("10.0.0.12", issues.Single(issue => issue.Kind == PipelineCopyIssueKind.HardCodedHost).Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Adapt_ANewPrefixContainingTheOldOne_IsNotReportedAsARemainingMention()
    {
        const string yaml = "name: app-ci\ntrigger: manual\nstages: []\n";
        var (adapted, issues) = PipelineCopyAdapter.Adapt(yaml,
            new Dictionary<string, string> { ["app-ci"] = "app2-ci" }, "app", "app2", new HashSet<string> { "app2-ci" });

        Assert.Contains("name: app2-ci", adapted, StringComparison.Ordinal);
        Assert.Empty(issues);
    }
}
