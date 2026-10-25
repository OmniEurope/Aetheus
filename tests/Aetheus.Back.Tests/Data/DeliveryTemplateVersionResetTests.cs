// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Seeding;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Aetheus.Back.Tests.DataSeed;

/// <summary>Normalize shipped-template versions and dependent pipeline references once per
/// database.</summary>
public sealed class DeliveryTemplateVersionResetTests
{
    private const string DeployProdYaml =
        "name: aetheus-deploy-prod\nextends: host-bluegreen-deploy@6 # pinned\nstages: []\n";

    [Fact]
    public async Task SeedAsync_RenumbersAnInflatedCatalogToOneV1AndRepinsItsPipelines()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        AddTemplate(db, organizationId, "host-bluegreen-deploy", 6, Shipped("host-bluegreen-deploy"));
        AddTemplate(db, organizationId, "application-candidate", 7, null, firstVersionYaml: Shipped("application-candidate"));
        AddTemplate(db, organizationId, "application-promotion", 2, Shipped("application-promotion"));
        AddTemplate(db, organizationId, "application-ci", 3, null);
        var deployProd = AddPipeline(db, projectId, "aetheus-deploy-prod", DeployProdYaml, "host-bluegreen-deploy", 6);
        var candidateYaml = "name: aetheus-candidate\nextends: application-candidate@1\nstages: []\n";
        var candidate = AddPipeline(db, projectId, "aetheus-candidate", candidateYaml, "application-candidate", 1);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var audit = Substitute.For<IAuditService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit)
            .SeedAsync(TestContext.Current.CancellationToken);

        foreach (var name in new[] { "host-bluegreen-deploy", "application-candidate", "application-promotion", "application-ci" })
        {
            var template = await LoadTemplateAsync(db, organizationId, name);
            // Reset to the first shipped file as v1; normal seeding adds any later shipped files
            // afterward.
            var shippedFiles = DeliveryTemplateCatalog.Definitions[name].Resources;
            Assert.Equal(shippedFiles.Length, template.LatestVersion);
            Assert.Equal(Enumerable.Range(1, shippedFiles.Length), template.Versions.Select(item => item.Version).Order());
            var version = template.Versions.Single(item => item.Version == 1);
            Assert.Equal(Shipped(name), version.YamlContent);
            Assert.Equal(DeliveryTemplateVersionReset.ChangelogEntry, version.ChangelogEntry);
        }
        var repinned = await db.Pipelines.SingleAsync(pipeline => pipeline.Id == deployProd.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, repinned.TemplateReferenceVersion);
        Assert.Equal(DeployProdYaml.Replace("@6", "@1", StringComparison.Ordinal), repinned.YamlDefinition);
        var unchanged = await db.Pipelines.SingleAsync(pipeline => pipeline.Id == candidate.Id, TestContext.Current.CancellationToken);
        Assert.Equal(candidateYaml, unchanged.YamlDefinition);
        await audit.Received(1).LogAsync("TemplateVersionsReset", "PipelineTemplate", Arg.Any<int?>(),
            "host-bluegreen-deploy: v6 -> v1", Arg.Any<CancellationToken>());
        await audit.Received(4).LogAsync("TemplateVersionsReset", "PipelineTemplate", Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await audit.Received(1).LogAsync("TemplateVersionRepinned", "Pipeline", deployProd.Id,
            "host-bluegreen-deploy: v6 -> v1", Arg.Any<CancellationToken>());
        await audit.Received(1).LogAsync("TemplateVersionRepinned", "Pipeline", Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        // A database without either reset marker receives both markers in one run.
        Assert.Equal(1, await db.AppSettings.CountAsync(setting => setting.Key == DeliveryTemplateVersionReset.MarkerKey,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AppSettings.CountAsync(setting => setting.Key == DeliveryTemplateVersionReset.FirstResetMarkerKey,
            TestContext.Current.CancellationToken));
    }

    /// <summary>The current reset runs once under its own marker even when the compatibility reset
    /// marker exists.</summary>
    [Fact]
    public async Task SeedAsync_DatabaseThatRanOnlyTheFirstReset_IsResetAgain()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        db.AppSettings.Add(new AppSetting { Key = DeliveryTemplateVersionReset.FirstResetMarkerKey, Value = "2026-10-05" });
        AddTemplate(db, organizationId, "application-ci", 6, Shipped("application-ci"));
        var ciYaml = "name: ats-ci\nextends: application-ci@6\nstages: []\n";
        var ci = AddPipeline(db, projectId, "ats-ci", ciYaml, "application-ci", 6);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var audit = Substitute.For<IAuditService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit)
            .SeedAsync(TestContext.Current.CancellationToken);

        var template = await LoadTemplateAsync(db, organizationId, "application-ci");
        Assert.Equal(1, template.LatestVersion);
        var version = Assert.Single(template.Versions);
        Assert.Equal(Shipped("application-ci"), version.YamlContent);
        Assert.Contains("R1-20", version.ChangelogEntry, StringComparison.Ordinal);
        var repinned = await db.Pipelines.SingleAsync(pipeline => pipeline.Id == ci.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, repinned.TemplateReferenceVersion);
        Assert.Equal(ciYaml.Replace("@6", "@1", StringComparison.Ordinal), repinned.YamlDefinition);
        await audit.Received(1).LogAsync("TemplateVersionsReset", "PipelineTemplate", template.Id,
            "application-ci: v6 -> v1", Arg.Any<CancellationToken>());
        await audit.Received(1).LogAsync("TemplateVersionRepinned", "Pipeline", ci.Id,
            "application-ci: v6 -> v1", Arg.Any<CancellationToken>());
        Assert.Equal(1, await db.AppSettings.CountAsync(setting => setting.Key == DeliveryTemplateVersionReset.MarkerKey,
            TestContext.Current.CancellationToken));
        Assert.Equal(1, await db.AppSettings.CountAsync(setting => setting.Key == DeliveryTemplateVersionReset.FirstResetMarkerKey,
            TestContext.Current.CancellationToken));
    }

    /// <summary>The completion marker prevents renumbering regardless of template version
    /// count.</summary>
    [Fact]
    public async Task SeedAsync_WithTheMarkerPresent_RenumbersNothing()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        db.AppSettings.Add(new AppSetting { Key = DeliveryTemplateVersionReset.MarkerKey, Value = "2026-10-11" });
        AddTemplate(db, organizationId, "application-ci", 6, Shipped("application-ci"));
        var ci = AddPipeline(db, projectId, "ats-ci", "name: ats-ci\nextends: application-ci@6\nstages: []\n", "application-ci", 6);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var audit = Substitute.For<IAuditService>();
        var git = Substitute.For<IPipelineGitService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit, pipelineGit: git)
            .SeedAsync(TestContext.Current.CancellationToken);

        var template = await LoadTemplateAsync(db, organizationId, "application-ci");
        Assert.Equal(6, template.LatestVersion);
        Assert.Equal(Enumerable.Range(1, 6), template.Versions.Select(version => version.Version).Order());
        var pipeline = await db.Pipelines.SingleAsync(item => item.Id == ci.Id, TestContext.Current.CancellationToken);
        Assert.Equal(6, pipeline.TemplateReferenceVersion);
        Assert.Empty(audit.ReceivedCalls());
        Assert.Empty(git.ReceivedCalls());
    }

    /// <summary>Leave a template already matching all shipped versions unchanged so its references
    /// retain the correct content.</summary>
    [Fact]
    public async Task SeedAsync_TemplateHoldingExactlyItsShippedFiles_IsLeftAlone()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        var files = DeliveryTemplateCatalog.Definitions["application-candidate"].Resources
            .Select(DeliveryTemplateCatalog.ReadResource).ToArray();
        var shipped = files.Length;
        AddTemplate(db, organizationId, "application-candidate", shipped, files[^1], firstVersionYaml: files[0]);
        foreach (var version in db.PipelineTemplates.Local.Single().Versions)
            version.YamlContent = files[version.Version - 1];
        var candidateYaml = $"name: ats-candidate\nextends: application-candidate@{shipped}\nstages: []\n";
        var candidate = AddPipeline(db, projectId, "ats-candidate", candidateYaml, "application-candidate", shipped);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var versionIds = await db.PipelineTemplateVersions.Select(version => version.Id)
            .ToListAsync(TestContext.Current.CancellationToken);

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System).SeedAsync(TestContext.Current.CancellationToken);

        var template = await LoadTemplateAsync(db, organizationId, "application-candidate");
        Assert.Equal(shipped, template.LatestVersion);
        Assert.Equal(versionIds.Order(), template.Versions.Select(version => version.Id).Order());
        var pipeline = await db.Pipelines.SingleAsync(item => item.Id == candidate.Id, TestContext.Current.CancellationToken);
        Assert.Equal(shipped, pipeline.TemplateReferenceVersion);
        Assert.Equal(candidateYaml, pipeline.YamlDefinition);
    }

    /// <summary>
    /// A run reads a project pipeline's <c>.pipeline/</c> file before its database row: the file is
    /// repinned too, read on its own, and only when it extends a renumbered template. A failed write
    /// never stops the start.
    /// </summary>
    [Fact]
    public async Task SeedAsync_RepinsTheGitDefinitionsOfRenumberedTemplates()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        AddTemplate(db, organizationId, "application-ci", 6, Shipped("application-ci"));
        AddTemplate(db, organizationId, "application-quality", 6, Shipped("application-quality"));
        var ci = AddPipeline(db, projectId, "ats-ci", "name: ats-ci\nextends: application-ci@6\nstages: []\n", "application-ci", 6);
        ci.SourceBranch = "develop";
        var drifted = AddPipeline(db, projectId, "ats-drifted",
            "name: ats-drifted\nextends: application-ci@6\nstages: []\n", "application-ci", 6);
        var quality = AddPipeline(db, projectId, "ats-quality",
            "name: ats-quality\nextends: application-quality@6\nstages: []\n", "application-quality", 6);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var gitCi = "# edited in Git\nname: ats-ci\nextends: 'application-ci@6' # pinned\ntrigger: manual\n";
        var git = Substitute.For<IPipelineGitService>();
        git.ReadProjectPipelineYamlAsync(projectId, "ats-ci", Arg.Any<CancellationToken>(), "develop", Arg.Any<int?>())
            .Returns(gitCi);
        git.ReadProjectPipelineYamlAsync(projectId, "ats-drifted", Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns("name: ats-drifted\nextends: my-own-template@4\n");
        git.ReadProjectPipelineYamlAsync(projectId, "ats-quality", Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns("name: ats-quality\nextends: application-quality@6\n");
        git.WriteProjectPipelineYamlAsync(projectId, "ats-ci", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns((GitWriteOutcome.Committed, (string?)null));
        git.WriteProjectPipelineYamlAsync(projectId, "ats-quality", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns((GitWriteOutcome.Failed, "repository not initialized on disk"));
        var audit = Substitute.For<IAuditService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit, pipelineGit: git)
            .SeedAsync(TestContext.Current.CancellationToken);

        await git.Received(1).WriteProjectPipelineYamlAsync(projectId, "ats-ci",
            "# edited in Git\nname: ats-ci\nextends: 'application-ci@1' # pinned\ntrigger: manual\n", "system",
            Arg.Any<CancellationToken>(), "develop", Arg.Any<int?>());
        await git.DidNotReceive().WriteProjectPipelineYamlAsync(projectId, "ats-drifted", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>());
        await git.Received(1).WriteProjectPipelineYamlAsync(projectId, "ats-quality",
            "name: ats-quality\nextends: application-quality@1\n", "system",
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>());
        await audit.Received(1).LogAsync("TemplateVersionRepinned", "Pipeline", ci.Id,
            "application-ci: Git definition -> v1", Arg.Any<CancellationToken>());
        await audit.DidNotReceive().LogAsync("TemplateVersionRepinned", "Pipeline", quality.Id,
            "application-quality: Git definition -> v1", Arg.Any<CancellationToken>());
        // The failed write left the database reset in place.
        var reloaded = await db.Pipelines.SingleAsync(item => item.Id == quality.Id, TestContext.Current.CancellationToken);
        Assert.Equal(1, reloaded.TemplateReferenceVersion);
        Assert.Equal(1, (await LoadTemplateAsync(db, organizationId, "application-quality")).LatestVersion);
        Assert.Equal(1, (await db.Pipelines.SingleAsync(item => item.Id == drifted.Id, TestContext.Current.CancellationToken))
            .TemplateReferenceVersion);
    }

    /// <summary>
    /// The Git repin is outside the database transaction: a failed write leaves
    /// <see cref="DeliveryTemplateVersionReset.GitRepinDoneKey"/> absent, and the next start repins that
    /// file, without auditing again the file already at @1, then writes the marker.
    /// </summary>
    [Fact]
    public async Task SeedAsync_FailedGitWrite_IsRetriedOnTheNextStartUntilDone()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        AddTemplate(db, organizationId, "application-ci", 6, Shipped("application-ci"));
        AddTemplate(db, organizationId, "application-quality", 6, Shipped("application-quality"));
        var ci = AddPipeline(db, projectId, "ats-ci", "name: ats-ci\nextends: application-ci@6\n", "application-ci", 6);
        var quality = AddPipeline(db, projectId, "ats-quality",
            "name: ats-quality\nextends: application-quality@6\n", "application-quality", 6);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var gitFiles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ats-ci"] = "name: ats-ci\nextends: application-ci@6\n",
            ["ats-quality"] = "name: ats-quality\nextends: application-quality@6\n"
        };
        var failQuality = true;
        var git = Substitute.For<IPipelineGitService>();
        git.ReadProjectPipelineYamlAsync(projectId, Arg.Any<string>(), Arg.Any<CancellationToken>(),
                Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(call => gitFiles[call.ArgAt<string>(1)]);
        git.WriteProjectPipelineYamlAsync(projectId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns(call =>
            {
                var name = call.ArgAt<string>(1);
                if (name == "ats-quality" && failQuality)
                    return (GitWriteOutcome.Failed, (string?)"repository locked");
                gitFiles[name] = call.ArgAt<string>(2);
                return (GitWriteOutcome.Committed, (string?)null);
            });
        var audit = Substitute.For<IAuditService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit, pipelineGit: git)
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("name: ats-ci\nextends: application-ci@1\n", gitFiles["ats-ci"]);
        Assert.Equal("name: ats-quality\nextends: application-quality@6\n", gitFiles["ats-quality"]);
        Assert.False(await HasSettingAsync(db, DeliveryTemplateVersionReset.GitRepinDoneKey));
        var recorded = await db.AppSettings.SingleAsync(
            setting => setting.Key == DeliveryTemplateVersionReset.GitRepinTemplatesKey, TestContext.Current.CancellationToken);
        Assert.Equal(["application-ci", "application-quality"], recorded.Value.Split(',').Order());

        failQuality = false;
        git.ClearReceivedCalls();
        audit.ClearReceivedCalls();
        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit, pipelineGit: git)
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.Equal("name: ats-quality\nextends: application-quality@1\n", gitFiles["ats-quality"]);
        await git.Received(1).WriteProjectPipelineYamlAsync(projectId, "ats-quality",
            "name: ats-quality\nextends: application-quality@1\n", "system",
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>());
        await git.DidNotReceive().WriteProjectPipelineYamlAsync(projectId, "ats-ci", Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>());
        await audit.Received(1).LogAsync("TemplateVersionRepinned", "Pipeline", quality.Id,
            "application-quality: Git definition -> v1", Arg.Any<CancellationToken>());
        await audit.DidNotReceive().LogAsync(Arg.Any<string>(), Arg.Any<string>(), ci.Id,
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await audit.DidNotReceive().LogAsync("TemplateVersionsReset", Arg.Any<string>(), Arg.Any<int?>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        Assert.True(await HasSettingAsync(db, DeliveryTemplateVersionReset.GitRepinDoneKey));
        Assert.Equal(1, (await LoadTemplateAsync(db, organizationId, "application-quality")).LatestVersion);
    }

    /// <summary>A Git pass with every file repinned writes the done marker; the next start makes no Git call.</summary>
    [Fact]
    public async Task SeedAsync_CompleteGitRepin_WritesTheDoneMarkerAndTheNextStartSkipsGit()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        AddTemplate(db, organizationId, "application-ci", 6, Shipped("application-ci"));
        AddPipeline(db, projectId, "ats-ci", "name: ats-ci\nextends: application-ci@6\n", "application-ci", 6);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var git = Substitute.For<IPipelineGitService>();
        git.ReadProjectPipelineYamlAsync(projectId, "ats-ci", Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns("name: ats-ci\nextends: application-ci@6\n");
        git.WriteProjectPipelineYamlAsync(projectId, "ats-ci", Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>())
            .Returns((GitWriteOutcome.Committed, (string?)null));

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, pipelineGit: git)
            .SeedAsync(TestContext.Current.CancellationToken);

        await git.Received(1).WriteProjectPipelineYamlAsync(projectId, "ats-ci",
            "name: ats-ci\nextends: application-ci@1\n", "system",
            Arg.Any<CancellationToken>(), Arg.Any<string?>(), Arg.Any<int?>());
        Assert.True(await HasSettingAsync(db, DeliveryTemplateVersionReset.GitRepinDoneKey));

        git.ClearReceivedCalls();
        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, pipelineGit: git)
            .SeedAsync(TestContext.Current.CancellationToken);

        Assert.Empty(git.ReceivedCalls());
        Assert.Equal(1, await db.AppSettings.CountAsync(setting => setting.Key == DeliveryTemplateVersionReset.GitRepinDoneKey,
            TestContext.Current.CancellationToken));
    }

    /// <summary>
    /// Once done, the reset never runs again: a generic template that later grows a v2 through the
    /// normal synchronization keeps it, and nothing is rewritten or audited on the next start.
    /// </summary>
    [Fact]
    public async Task SeedAsync_SecondStartIsANoOpAndLaterVersionsAreKept()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        AddTemplate(db, organizationId, "host-bluegreen-deploy", 6, Shipped("host-bluegreen-deploy"));
        AddPipeline(db, projectId, "aetheus-deploy-prod", DeployProdYaml, "host-bluegreen-deploy", 6);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System).SeedAsync(TestContext.Current.CancellationToken);
        var versionIds = await db.PipelineTemplateVersions.OrderBy(version => version.Id)
            .Select(version => version.Id).ToListAsync(TestContext.Current.CancellationToken);
        var ci = await LoadTemplateAsync(db, organizationId, "application-ci");
        ci.Versions.Add(new PipelineTemplateVersion
        {
            Version = 2,
            YamlContent = Shipped("application-ci"),
            ChangelogEntry = "Synchronized from the current reference pipeline.",
            CreatedAt = DateTime.UnixEpoch,
            CreatedByUsername = "system"
        });
        ci.LatestVersion = 2;
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var audit = Substitute.For<IAuditService>();

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System, audit: audit)
            .SeedAsync(TestContext.Current.CancellationToken);

        var after = await db.PipelineTemplateVersions.OrderBy(version => version.Id)
            .Select(version => version.Id).ToListAsync(TestContext.Current.CancellationToken);
        Assert.Equal(versionIds.Count + 1, after.Count);
        Assert.Equal(versionIds, after.Take(versionIds.Count));
        var reloaded = await LoadTemplateAsync(db, organizationId, "application-ci");
        Assert.Equal(2, reloaded.LatestVersion);
        Assert.Equal([1, 2], reloaded.Versions.Select(version => version.Version).Order());
        Assert.Empty(audit.ReceivedCalls());
    }

    [Fact]
    public async Task SeedAsync_LeavesUserTemplatesAndOtherOrganizationsUntouched()
    {
        await using var db = NewDatabase();
        var (organizationId, projectId) = await SeedOrganizationAsync(db, "aetheus");
        var (otherOrganizationId, otherProjectId) = await SeedOrganizationAsync(db, "acme");
        AddTemplate(db, organizationId, "my-ci", 3, null);
        var userPipeline = AddPipeline(db, projectId, "uses-my-ci",
            "name: uses-my-ci\nextends: my-ci@3\nstages: []\n", "my-ci", 3);
        AddTemplate(db, otherOrganizationId, "host-bluegreen-deploy", 3, null);
        var otherPipeline = AddPipeline(db, otherProjectId, "acme-deploy",
            "name: acme-deploy\nextends: host-bluegreen-deploy@3\nstages: []\n", "host-bluegreen-deploy", 3);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await new DeliveryPipelineTemplateSeeder(db, TimeProvider.System).SeedAsync(TestContext.Current.CancellationToken);

        var userTemplate = await LoadTemplateAsync(db, organizationId, "my-ci");
        Assert.Equal(3, userTemplate.LatestVersion);
        Assert.Equal([1, 2, 3], userTemplate.Versions.Select(version => version.Version).Order());
        var otherTemplate = await LoadTemplateAsync(db, otherOrganizationId, "host-bluegreen-deploy");
        Assert.Equal(3, otherTemplate.LatestVersion);
        Assert.Equal([1, 2, 3], otherTemplate.Versions.Select(version => version.Version).Order());
        foreach (var pipeline in new[] { userPipeline, otherPipeline })
        {
            var reloaded = await db.Pipelines.SingleAsync(item => item.Id == pipeline.Id, TestContext.Current.CancellationToken);
            Assert.Equal(3, reloaded.TemplateReferenceVersion);
            Assert.Contains("@3", reloaded.YamlDefinition, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("name: a\nextends: 'host-bluegreen-deploy@6' # keep\nstages: []", "name: a\nextends: 'host-bluegreen-deploy@1' # keep\nstages: []")]
    [InlineData("extends: \"application-promotion@12\"\r\nstages: []", "extends: \"application-promotion@1\"\r\nstages: []")]
    [InlineData("  extends :  host-bluegreen-deploy@2", "  extends :  host-bluegreen-deploy@1")]
    [InlineData("name: plain\nstages: []", "name: plain\nstages: []")]
    public void RepinExtends_RewritesOnlyTheVersion(string yaml, string expected) =>
        Assert.Equal(expected, DeliveryTemplateVersionReset.RepinExtends(yaml));

    [Theory]
    [InlineData("name: a\nextends: 'application-ci@6' # keep\n", "name: a\nextends: 'application-ci@1' # keep\n")]
    [InlineData("extends: Application-CI @3\n", "extends: Application-CI @1\n")]
    [InlineData("extends: my-own-template@4\n", "extends: my-own-template@4\n")]
    [InlineData("name: plain\nstages: []", "name: plain\nstages: []")]
    public void RepinExtends_WithNames_RewritesOnlyARenumberedTemplate(string yaml, string expected) =>
        Assert.Equal(expected, DeliveryTemplateVersionReset.RepinExtends(
            yaml, new HashSet<string>(["application-ci"], StringComparer.OrdinalIgnoreCase)));

    private static string Shipped(string name) =>
        DeliveryTemplateCatalog.ReadResource(DeliveryTemplateCatalog.Definitions[name].Resources[0]);

    private static AppDbContext NewDatabase() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static async Task<(int OrganizationId, int ProjectId)> SeedOrganizationAsync(AppDbContext db, string slug)
    {
        var organization = new Organization
        {
            Name = slug,
            Slug = slug,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch
        };
        var project = new Project { Name = $"{slug}-project", Organization = organization };
        db.Projects.Add(project);
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return (organization.Id, project.Id);
    }

    /// <summary>Versions 1..<paramref name="latest"/>; the last holds <paramref name="latestYaml"/> when given.</summary>
    private static void AddTemplate(
        AppDbContext db, int organizationId, string name, int latest, string? latestYaml, string? firstVersionYaml = null) =>
        db.PipelineTemplates.Add(new PipelineTemplate
        {
            Name = name,
            Description = "Before the reset",
            Category = "Application Delivery",
            OrganizationId = organizationId,
            LatestVersion = latest,
            CreatedAt = DateTime.UnixEpoch,
            UpdatedAt = DateTime.UnixEpoch,
            Versions = Enumerable.Range(1, latest).Select(version => new PipelineTemplateVersion
            {
                Version = version,
                YamlContent = version == latest && latestYaml is not null ? latestYaml
                    : version == 1 && firstVersionYaml is not null ? firstVersionYaml
                    : $"name: {name}-old-v{version}\nstages: []",
                ChangelogEntry = $"v{version}",
                CreatedAt = DateTime.UnixEpoch,
                CreatedByUsername = "system"
            }).ToList()
        });

    private static Pipeline AddPipeline(
        AppDbContext db, int projectId, string name, string yaml, string templateName, int templateVersion)
    {
        var pipeline = new Pipeline
        {
            Name = name,
            ProjectId = projectId,
            YamlDefinition = yaml,
            TemplateReferenceName = templateName,
            TemplateReferenceVersion = templateVersion
        };
        db.Pipelines.Add(pipeline);
        return pipeline;
    }

    private static Task<bool> HasSettingAsync(AppDbContext db, string key) =>
        db.AppSettings.AnyAsync(setting => setting.Key == key, TestContext.Current.CancellationToken);

    private static Task<PipelineTemplate> LoadTemplateAsync(AppDbContext db, int organizationId, string name) =>
        db.PipelineTemplates.Include(template => template.Versions)
            .SingleAsync(template => template.OrganizationId == organizationId && template.Name == name,
                TestContext.Current.CancellationToken);
}
