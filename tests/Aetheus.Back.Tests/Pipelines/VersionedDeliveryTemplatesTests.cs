// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-33 and R-32: the shipped delivery templates a wizard pipeline extends, resolved by the real
/// <see cref="PipelineTemplateResolver"/> as the run resolves them: the candidate v5 and the fast release are versioned
/// (they allocate and declare the prefix, which the project's pipeline overrides), name their release with the run's
/// APPLICATION_VERSION, and the fast release records it and advances main on both strategies. The candidate v4 a pinned
/// pipeline (Aetheus, older projects) still extends is left as published. What these tests do not prove is a real run:
/// the release, the tag and the stage scheduling on a server are proved by the candidate and fast-release runs.
/// </summary>
public sealed class VersionedDeliveryTemplatesTests
{
    private const int OrganizationId = 3;
    private const string VersionReference = "$(APPLICATION_VERSION)";

    private static IPipelineTemplateRepository Repository()
    {
        var repository = Substitute.For<IPipelineTemplateRepository>();
        var id = 40;
        foreach (var (name, definition) in DeliveryTemplateCatalog.Definitions)
        {
            var versions = definition.Resources
                .Select((resource, index) => new PipelineTemplateVersion { Version = index + 1, YamlContent = DeliveryTemplateCatalog.ReadResource(resource) })
                .ToList();
            repository.FindTemplateByNameAsync(name, OrganizationId, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
            {
                Id = ++id,
                Name = name,
                OrganizationId = OrganizationId,
                LatestVersion = versions.Count,
                Versions = versions
            });
        }
        return repository;
    }

    private static async Task<PipelineYamlDefinition> ResolveAsync(string extends, string variables = "") =>
        (await new PipelineTemplateResolver(Repository()).ResolveAsync($"""
            name: demo
            extends: {extends}
            trigger: manual
            stages: []
            variables:
              APPLICATION_PREFIX: "demo"
            {variables}
            """, OrganizationId, ct: TestContext.Current.CancellationToken)).Definition;

    private static PipelineStageDefinition Stage(PipelineYamlDefinition definition, string name) =>
        Assert.Single(YamlParsingHelper.FlattenJobs(definition), stage => stage.Name == name);

    [Fact]
    public async Task CandidateV5_IsVersionedAndNamesItsReleaseWithTheRunVersion()
    {
        var definition = await ResolveAsync("application-candidate@5");

        Assert.True(PipelineApplicationVersion.Applies(definition));
        Assert.Equal("0.1", PipelineApplicationVersion.DeclaredSeries(definition));
        Assert.Contains(definition.Parameters, parameter => parameter.Name == PipelineApplicationVersion.ParameterName
            && !parameter.Required && string.IsNullOrEmpty(parameter.Default));
        var release = Assert.Single(Stage(definition, "Candidate").Steps);
        Assert.Equal("release", release.Type);
        Assert.Equal("$(CANDIDATE_VERSION)", release.Version);
        Assert.Equal(VersionReference, definition.Variables["CANDIDATE_VERSION"]);
        Assert.Equal(VersionReference, definition.Variables["APP_VERSION"]);
        // The CI the candidate triggers builds and seals the same version.
        var ci = Assert.Single(Stage(definition, "CI").Steps);
        Assert.Equal(VersionReference, ci.Variables["APPLICATION_VERSION"]);
        Assert.Equal(VersionReference, ci.Variables["APP_VERSION"]);
    }

    [Fact]
    public async Task TheProjectPipeline_RaisesThePrefix()
    {
        var definition = await ResolveAsync("application-candidate@5", "  APPLICATION_VERSION_PREFIX: \"1.0\"");

        Assert.Equal("1.0", PipelineApplicationVersion.DeclaredSeries(definition));
    }

    [Fact]
    public async Task AnEmptyProjectPrefix_KeepsTheCandidateVersionedInTheRepositorySeries()
    {
        // R-18: a pipeline extending v5 that declares the prefix empty overrides the template's "0.1" and declares no
        // series, so the run counts in its VersionPrefix series and APPLICATION_VERSION is still written.
        var definition = await ResolveAsync("application-candidate@5", "  APPLICATION_VERSION_PREFIX: \"\"");

        Assert.True(PipelineApplicationVersion.Applies(definition));
        Assert.Null(PipelineApplicationVersion.DeclaredSeries(definition));
    }

    [Fact]
    public async Task CandidateV4_StaysUnversioned()
    {
        var definition = await ResolveAsync("application-candidate@4");

        Assert.False(PipelineApplicationVersion.Applies(definition));
        Assert.Equal("c-$(BUILD_SOURCEVERSION)", definition.Variables["CANDIDATE_VERSION"]);
    }

    [Fact]
    public async Task ReleaseFast_IsVersioned_RecordsItsReleaseAndAdvancesMainOnBothStrategies()
    {
        var definition = await ResolveAsync("application-release-fast@1");

        Assert.True(PipelineApplicationVersion.Applies(definition));
        Assert.Equal(VersionReference, definition.Variables["APP_VERSION"]);
        Assert.Equal(VersionReference, definition.Variables[PipelineBranchAdvanceStep.CandidateVersionVariable]);

        // The release is recorded before the transaction is closed, as in application-deploy-prod-core: a failure to
        // record it still reaches the rollback, and a rollback demotes the release this run recorded.
        var record = Stage(definition, "Record release");
        Assert.Equal(["Evidence"], record.DependsOn);
        Assert.Null(record.Condition);
        Assert.Equal(["Record release"], Stage(definition, "Release recorded").DependsOn);
        Assert.Equal(["Release recorded"], Stage(definition, "Commit").DependsOn);
        var demote = Stage(definition, "Demote release");
        Assert.Equal(["Rollback"], demote.DependsOn);
        Assert.Equal("failed()", demote.Condition);
        var demoteStep = Assert.Single(demote.Steps);
        Assert.Equal(VersionReference, demoteStep.Version);
        Assert.False(demoteStep.Deployed);
        // Advance main hangs off the stage both strategies pass (it runs always), never off a strategy's own stage.
        var erase = Stage(definition, "Erase environment file");
        Assert.Equal("always()", erase.Condition);
        var releaseStep = Assert.Single(record.Steps);
        Assert.Equal("release", releaseStep.Type);
        Assert.Equal(VersionReference, releaseStep.Version);
        Assert.True(releaseStep.Deployed);

        var advance = Stage(definition, "Advance main");
        Assert.Equal(["Erase environment file"], advance.DependsOn);
        Assert.Equal("succeeded()", advance.Condition);
        var advanceStep = Assert.Single(advance.Steps);
        Assert.Equal("advance-branch", advanceStep.Type);
        Assert.Equal("main", advanceStep.Branch);
        Assert.Equal("v" + VersionReference, advanceStep.Tag);

        // Neither runs only for one strategy.
        foreach (var stage in new[] { record, advance })
            Assert.DoesNotContain("DEPLOY_STRATEGY", stage.Condition ?? string.Empty, StringComparison.Ordinal);
        foreach (var strategy in new[] { "replace", "bluegreen" })
        {
            var variables = new Dictionary<string, string> { ["DEPLOY_STRATEGY"] = strategy };
            Assert.True(PipelineConditionEvaluator.Evaluate(advance.Condition, variables, previousStageFailed: false).Met);
            Assert.False(PipelineConditionEvaluator.Evaluate(advance.Condition, variables, previousStageFailed: true).Met);
        }
    }
}
