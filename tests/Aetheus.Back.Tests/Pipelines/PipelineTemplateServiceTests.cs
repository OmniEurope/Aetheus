// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Organizations;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Aetheus.Back.Tests;

public class PipelineTemplateServiceTests
{
    private readonly IPipelineRepository _repoMock = Substitute.For<IPipelineRepository>();
    private readonly IAuditService _auditMock = Substitute.For<IAuditService>();
    private readonly ILogger<PipelineTemplateService> _loggerMock = Substitute.For<ILogger<PipelineTemplateService>>();
    private readonly IOrganizationRepository _organizationRepository = Substitute.For<IOrganizationRepository>();
    private readonly IEntityChangeNotifier _notifier = Substitute.For<IEntityChangeNotifier>();
    private readonly PipelineTemplateService _sut;

    public PipelineTemplateServiceTests()
    {
        _organizationRepository.GetDefaultOrganizationIdAsync(Arg.Any<CancellationToken>()).Returns(1);
        _sut = new PipelineTemplateService(
            _repoMock, _auditMock, _loggerMock, TimeProvider.System,
            _organizationRepository, new PipelineTemplateResolver(_repoMock), _notifier);
    }

    [Fact]
    public async Task GetTemplatesAsync_ReturnsMappedSummaries()
    {
        _repoMock.GetTemplateSummariesAsync(Arg.Any<CancellationToken>())
            .Returns([
                new PipelineTemplateSummaryDto
                {
                    Id = 1,
                    Name = "T1",
                    Description = "desc",
                    Category = "CI",
                    Version = 1,
                    PipelineCount = 2,
                    LatestRunAt = new DateTime(2026, 8, 10, 9, 30, 0)
                },
                new PipelineTemplateSummaryDto
                {
                    Id = 2,
                    Name = "T2",
                    Description = "desc2",
                    Category = "CD",
                    Version = 2
                }
            ]);

        var result = await _sut.GetTemplatesAsync(ct: TestContext.Current.CancellationToken);

        Assert.Equal(2, result.Count);
        Assert.Equal("T1", result[0].Name);
        Assert.Equal("CI", result[0].Category);
        Assert.Equal(2, result[0].PipelineCount);
        Assert.Equal(new DateTime(2026, 8, 10, 9, 30, 0), result[0].LatestRunAt);
    }

    [Fact]
    public async Task GetTemplateAsync_Found_ReturnsDto()
    {
        _repoMock.GetTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplate
            {
                Id = 1,
                Name = "T1",
                Description = "d",
                Category = "CI",
                LatestVersion = 1
            });
        _repoMock.GetTemplateVersionAsync(1, 1, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateVersion
            {
                Version = 1,
                YamlContent = "yaml",
                ChangelogEntry = "init"
            });

        var result = await _sut.GetTemplateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("T1", result.Name);
        Assert.Equal("yaml", result.YamlContent);
        Assert.Empty(result.Versions);
    }

    [Fact]
    public async Task GetTemplateVersionsAsync_ReturnsMetadataPageWithoutYamlPayload()
    {
        _repoMock.GetTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplate { Id = 1, Name = "T1", LatestVersion = 30 });
        _repoMock.GetTemplateVersionsPagedAsync(
                1, 2, 10, "Version", true, Arg.Any<CancellationToken>())
            .Returns((new List<PipelineTemplateVersionSummaryDto>
            {
                new() { Version = 20, ChangelogEntry = "change", CreatedByUsername = "alice" }
            }, 30));

        var result = await _sut.GetTemplateVersionsAsync(1, new PaginationRequest
        {
            Page = 2,
            PageSize = 10,
            SortBy = "Version",
            SortDescending = true
        }, ct: TestContext.Current.CancellationToken);

        Assert.Equal(30, result.TotalCount);
        Assert.Equal(20, result.Items[0].Version);
    }

    [Fact]
    public async Task GetTemplateVersionAsync_ReturnsYamlOnlyForRequestedVersion()
    {
        _repoMock.GetTemplateVersionAsync(1, 7, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplateVersion
            {
                Version = 7,
                YamlContent = "name: requested",
                ChangelogEntry = "requested"
            });

        var result = await _sut.GetTemplateVersionAsync(1, 7, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("name: requested", result.YamlContent);
    }

    [Fact]
    public async Task GetTemplateAsync_NotFound_ReturnsNull()
    {
        _repoMock.GetTemplateAsync(999, Arg.Any<CancellationToken>())
            .Returns((PipelineTemplate?)null);

        var result = await _sut.GetTemplateAsync(999, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task CreateTemplateAsync_CreatesWithVersion1()
    {
        var request = new CreatePipelineTemplateRequest { Name = "New", Description = "d", Category = "CI", YamlContent = "name: new\nstages: []" };

        var result = await _sut.CreateTemplateAsync(request, ct: TestContext.Current.CancellationToken);

        Assert.Equal("New", result.Name);
        Assert.Equal(1, result.Version);
        Assert.Contains("v1:", result.Changelog);
        await _repoMock.Received(1).AddTemplateAsync(Arg.Any<PipelineTemplate>(), Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Created", "PipelineTemplate", Arg.Any<int>(), "New", Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(
            ResourceType.PipelineTemplate,
            Arg.Any<int>(),
            EntityChangeOps.Created,
            Arg.Any<CancellationToken>(),
            1);
    }

    [Fact]
    public async Task UpdateTemplateAsync_Found_IncrementsVersion()
    {
        var template = new PipelineTemplate
        {
            Id = 1,
            Name = "Updated",
            Description = "d",
            Category = "CI",
            LatestVersion = 2,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 2,
                    YamlContent = "name: old\nstages: []",
                    ChangelogEntry = "change"
                }
            ]
        };
        _repoMock.FindTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(template);

        var request = new UpdatePipelineTemplateRequest { Name = "Updated", Description = "d2", Category = "CD", YamlContent = "name: updated\nstages: []", ChangelogEntry = "big change" };

        var result = await _sut.UpdateTemplateAsync(1, request, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("Updated", result.Name);
        Assert.Equal(3, result.Version);
        Assert.Contains("v3: big change", result.Changelog);
        Assert.Equal("name: old\nstages: []", template.Versions.Single(version => version.Version == 2).YamlContent);
        Assert.Equal("name: updated\nstages: []", template.Versions.Single(version => version.Version == 3).YamlContent);
        await _repoMock.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(
            ResourceType.PipelineTemplate,
            template.Id,
            EntityChangeOps.Updated,
            Arg.Any<CancellationToken>(),
            template.OrganizationId);
    }

    [Fact]
    public async Task UpdateTemplateAsync_Rename_IsRejectedToProtectPinnedReferences()
    {
        _repoMock.FindTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplate { Id = 1, Name = "ci", LatestVersion = 1 });

        var action = () => _sut.UpdateTemplateAsync(1, new UpdatePipelineTemplateRequest
        {
            Name = "renamed-ci",
            Category = "CI",
            YamlContent = "name: ci\nstages: []",
            ChangelogEntry = "rename"
        });

        await Assert.ThrowsAsync<BadRequestException>(action);
        await _repoMock.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTemplateAsync_ConcurrentPublication_ReturnsConflict()
    {
        _repoMock.FindTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineTemplate
            {
                Id = 1,
                Name = "ci",
                OrganizationId = 1,
                LatestVersion = 1
            });
        _repoMock.SaveChangesAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new DbUpdateConcurrencyException()));

        var action = () => _sut.UpdateTemplateAsync(1, new UpdatePipelineTemplateRequest
        {
            Name = "ci",
            Category = "CI",
            YamlContent = "name: ci\nstages: []",
            ChangelogEntry = "concurrent"
        });

        await Assert.ThrowsAsync<ConflictException>(action);
    }

    [Fact]
    public async Task CreateTemplateAsync_InvalidYaml_IsRejectedBeforePersisting()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.CreateTemplateAsync(
            new CreatePipelineTemplateRequest
            {
                Name = "Broken",
                Category = "CI",
                YamlContent = "name: [unclosed"
            }, ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().AddTemplateAsync(
            Arg.Any<PipelineTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateTemplateAsync_NotFound_ReturnsNull()
    {
        _repoMock.FindTemplateAsync(999, Arg.Any<CancellationToken>())
            .Returns((PipelineTemplate?)null);

        var result = await _sut.UpdateTemplateAsync(999, new UpdatePipelineTemplateRequest { Name = "X", Description = "d", Category = "CI", YamlContent = "y" }, ct: TestContext.Current.CancellationToken);

        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateTemplateAsync_NoChangelogEntry_IsRejected()
    {
        var template = new PipelineTemplate { Id = 1, Name = "T", Description = "d", Category = "CI", YamlContent = "y", Version = 1, Changelog = "v1: init" };
        _repoMock.FindTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(template);

        var action = () => _sut.UpdateTemplateAsync(1,
            new UpdatePipelineTemplateRequest { Name = "T", Description = "d", Category = "CI", YamlContent = "y2" });

        await Assert.ThrowsAsync<BadRequestException>(action);
        Assert.Equal(1, template.LatestVersion);
    }

    [Fact]
    public async Task DeleteTemplateAsync_Found_ReturnsTrue()
    {
        var template = new PipelineTemplate { Id = 1, Name = "T1" };
        _repoMock.FindTemplateAsync(1, Arg.Any<CancellationToken>())
            .Returns(template);

        var result = await _sut.DeleteTemplateAsync(1, ct: TestContext.Current.CancellationToken);

        Assert.True(result);
        await _repoMock.Received(1).RemoveTemplateAsync(template, Arg.Any<CancellationToken>());
        await _auditMock.Received(1).LogAsync("Deleted", "PipelineTemplate", 1, "T1", Arg.Any<CancellationToken>());
        await _notifier.Received(1).BroadcastAsync(
            ResourceType.PipelineTemplate,
            template.Id,
            EntityChangeOps.Deleted,
            Arg.Any<CancellationToken>(),
            template.OrganizationId);
    }

    [Fact]
    public async Task DeleteTemplateAsync_NotFound_ReturnsFalse()
    {
        _repoMock.FindTemplateAsync(999, Arg.Any<CancellationToken>())
            .Returns((PipelineTemplate?)null);

        var result = await _sut.DeleteTemplateAsync(999, ct: TestContext.Current.CancellationToken);

        Assert.False(result);
    }

    [Fact]
    public async Task DeleteTemplateAsync_ReferencedByPinnedPipeline_IsRejected()
    {
        var template = new PipelineTemplate { Id = 1, Name = "ci", OrganizationId = 4 };
        _repoMock.FindTemplateAsync(1, Arg.Any<CancellationToken>()).Returns(template);
        _repoMock.GetPipelineYamlDefinitionsByOrganizationAsync(4, Arg.Any<CancellationToken>())
            .Returns(["name: toto\nextends: ci@1\nstages: []"]);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.DeleteTemplateAsync(1, ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().RemoveTemplateAsync(
            Arg.Any<PipelineTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task DeleteTemplateAsync_ReferencedByTemplateVersion_IsRejected()
    {
        var template = new PipelineTemplate { Id = 1, Name = "base", OrganizationId = 4 };
        _repoMock.FindTemplateAsync(1, Arg.Any<CancellationToken>()).Returns(template);
        _repoMock.GetTemplateVersionYamlDefinitionsByOrganizationAsync(
                4, 1, Arg.Any<CancellationToken>())
            .Returns(["name: child\nextends: base@1\nstages: []"]);

        await Assert.ThrowsAsync<ConflictException>(() => _sut.DeleteTemplateAsync(1, ct: TestContext.Current.CancellationToken));

        await _repoMock.DidNotReceive().RemoveTemplateAsync(
            Arg.Any<PipelineTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportTemplateAsync_ValidYaml_ReturnsTemplate()
    {
        var yaml = "name: My Pipeline\ntrigger: manual\nstages:\n  - name: build\n    steps:\n      - name: step1\n        shell: echo hi";

        var result = await _sut.ImportTemplateAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal("My Pipeline", result.Name);
        Assert.Equal("Imported", result.Category);
        Assert.Equal(1, result.Version);
        await _repoMock.Received(1).AddTemplateAsync(Arg.Any<PipelineTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ImportTemplateAsync_InvalidYaml_ReturnsNull_AndDoesNotPersist()
    {
        // Unbalanced flow sequence: reliably a YamlException, not a tolerantly-parsed scalar.
        var yaml = "name: [unclosed\nstages: { : :";

        var result = await _sut.ImportTemplateAsync(yaml, ct: TestContext.Current.CancellationToken);

        // Firm contract: reject (null) AND never persist a half-parsed template.
        Assert.Null(result);
        await _repoMock.DidNotReceive().AddTemplateAsync(Arg.Any<PipelineTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResolveTemplateAsync_WithParameters_SubstitutesValues()
    {
        var yaml = "name: test\ntrigger: manual\nparameters:\n  - name: env\n    default: dev\nstages:\n  - name: deploy\n    steps:\n      - name: s1\n        shell: deploy to ${{ parameters.env }}";

        var result = await _sut.ResolveTemplateAsync(yaml, new Dictionary<string, string> { ["env"] = "prod" }, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("prod", result);
        Assert.DoesNotContain("${{ parameters.env }}", result);
    }

    [Fact]
    public async Task ResolveTemplateAsync_InvalidYaml_IsRejected()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ResolveTemplateAsync("{{{{invalid", ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveTemplateAsync_WithExtends_MergesBase()
    {
        var baseTemplate = new PipelineTemplate
        {
            Name = "base",
            YamlContent = "name: base\ntrigger: manual\nvariables:\n  BASE_VAR: hello\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: make build"
        };
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>())
            .Returns(baseTemplate);

        var yaml = "name: child\nextends: base\nvariables:\n  CHILD_VAR: world\nstages:\n  - name: deploy\n    steps:\n      - name: push\n        shell: make deploy";

        var result = await _sut.ResolveTemplateAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        // The result should contain merged content from both base and child
        Assert.Contains("build", result);
        Assert.Contains("deploy", result);
    }

    [Fact]
    public async Task ResolveTemplateAsync_Extends_MergesParametersAndChildCollisionWins()
    {
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Name = "base",
            YamlContent = """
                name: base
                parameters:
                  - name: shared
                    default: base-value
                  - name: base_only
                    default: base-only-value
                stages:
                  - name: build
                    steps:
                      - name: compile
                        shell: echo ${{ parameters.shared }} ${{ parameters.base_only }}
                """
        });
        var childYaml = """
            name: child
            extends: base
            parameters:
              - name: shared
                default: child-value
              - name: child_only
                default: child-only-value
            stages:
              - name: deploy
                steps:
                  - name: push
                    shell: echo ${{ parameters.child_only }}
            """;

        var result = await _sut.ResolveTemplateAsync(childYaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("echo child-value base-only-value", result, StringComparison.Ordinal);
        Assert.Contains("echo child-only-value", result, StringComparison.Ordinal);
        var resolved = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(result);
        Assert.Equal(3, resolved.Parameters.Count);
        Assert.Equal("child-value", resolved.Parameters.Single(p => p.Name == "shared").Default);
    }

    /// <summary>
    /// PLAN-003 lot 30: the template's <c>requires:</c> reaches the resolved pipeline, joined with the
    /// pipeline's own, so the launch preflight checks what the template needs. A wizard pipeline
    /// (<c>extends:</c> only) used to resolve with no requirement at all.
    /// </summary>
    [Fact]
    public async Task ResolveTemplateAsync_Extends_JoinsTheTemplateRequiresWithThePipelines()
    {
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Name = "base",
            YamlContent = """
                name: base
                requires:
                  libraries: [app-host]
                  vaults: [app-secrets]
                stages: []
                """
        });

        var onlyExtends = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            await _sut.ResolveTemplateAsync("name: app\nextends: base\n", ct: TestContext.Current.CancellationToken) ?? string.Empty);
        Assert.Equal(["app-host"], onlyExtends.Requires!.Libraries);
        Assert.Equal(["app-secrets"], onlyExtends.Requires.Vaults);

        var withOwn = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(
            await _sut.ResolveTemplateAsync(
                "name: app\nextends: base\nrequires:\n  libraries: [APP-HOST, app-extra]\n",
                ct: TestContext.Current.CancellationToken) ?? string.Empty);
        Assert.Equal(["app-host", "app-extra"], withOwn.Requires!.Libraries);
        Assert.Equal(["app-secrets"], withOwn.Requires.Vaults);
    }

    [Fact]
    public async Task ResolveTemplateAsync_ExtendsNotFound_IsRejected()
    {
        _repoMock.FindTemplateByNameAsync("missing", 1, Arg.Any<CancellationToken>())
            .Returns((PipelineTemplate?)null);

        var yaml = "name: child\nextends: missing\nstages: []";

        await Assert.ThrowsAsync<BadRequestException>(() => _sut.ResolveTemplateAsync(yaml, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveTemplateAsync_ParameterDefault_UsedWhenNotProvided()
    {
        var yaml = "name: test\ntrigger: manual\nparameters:\n  - name: env\n    default: staging\nstages:\n  - name: deploy\n    steps:\n      - name: s1\n        shell: deploy to ${{ parameters.env }}";

        var result = await _sut.ResolveTemplateAsync(yaml, ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Contains("staging", result);
    }

    [Fact]
    public async Task ResolveTemplateAsync_OptionalParameterWithoutValue_SubstitutesEmptyString()
    {
        var yaml = """
            name: test
            trigger: manual
            parameters:
              - name: digest
                type: string
                default: ""
            variables:
              PACKAGE_DIGEST: "${{ parameters.digest }}"
            stages:
              - name: build
                steps:
                  - name: verify
                    shell: test -z "$PACKAGE_DIGEST"
            """;

        var result = await _sut.ResolveTemplateAsync(
            yaml,
            new Dictionary<string, string>(),
            ct: TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.DoesNotContain("${{", result, StringComparison.Ordinal);
        var resolved = YamlParsingHelper.Deserializer.Deserialize<PipelineYamlDefinition>(result);
        Assert.Equal(string.Empty, resolved.Variables["PACKAGE_DIGEST"]);
    }

    [Fact]
    public async Task ResolveTemplateAsync_PinnedVersion_RemainsStableAfterNewVersion()
    {
        var template = new PipelineTemplate
        {
            Id = 7,
            Name = "base",
            OrganizationId = 1,
            LatestVersion = 2,
            Versions =
            [
                new PipelineTemplateVersion { TemplateId = 7, Version = 1, YamlContent = "name: base\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo v1" },
                new PipelineTemplateVersion { TemplateId = 7, Version = 2, YamlContent = "name: base\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo v2" }
            ]
        };
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(template);

        var result = await _sut.ResolveTemplateAsync("name: child\nextends: base@1\nstages: []", ct: TestContext.Current.CancellationToken);

        Assert.Contains("echo v1", result, StringComparison.Ordinal);
        Assert.DoesNotContain("echo v2", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveTemplateAsync_ExplicitMissingVersion_IsRejectedWithoutLatestFallback()
    {
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Id = 7,
            Name = "base",
            OrganizationId = 1,
            LatestVersion = 1,
            Versions = [new PipelineTemplateVersion { Version = 1, YamlContent = "name: base\nstages: []" }]
        });

        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ResolveTemplateAsync("name: child\nextends: base@2\nstages: []", ct: TestContext.Current.CancellationToken));

        Assert.Contains("base@2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveTemplateAsync_CyclicPinnedInheritance_IsRejected()
    {
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Id = 7,
            Name = "base",
            OrganizationId = 1,
            LatestVersion = 1,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1,
                    YamlContent = "name: base\nextends: base@1\nstages: []"
                }
            ]
        });

        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ResolveTemplateAsync("name: child\nextends: base@1\nstages: []", ct: TestContext.Current.CancellationToken));

        Assert.Contains("Cyclic", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Recette R2-121: a project of an organization that holds no copy of the catalog extends
    /// the shipped template the platform shares; an organization's own template still wins.</summary>
    [Fact]
    public async Task Resolver_AnotherOrganization_ExtendsTheSharedShippedTemplate_UnlessItHasItsOwn()
    {
        var shared = new PipelineTemplate
        {
            Id = 7,
            Name = "application-ci",
            OrganizationId = 1,
            LatestVersion = 2,
            Versions = [new PipelineTemplateVersion { Version = 2, YamlContent = "name: shared\nvariables:\n  FROM: shared\nstages: []" }]
        };
        _repoMock.FindSharedTemplateByNameAsync("application-ci", Arg.Any<CancellationToken>()).Returns(shared);
        var resolver = new PipelineTemplateResolver(_repoMock);

        var fromShared = await resolver.ResolveAsync(
            "name: atlas-ci\nextends: application-ci@2\nstages: []", 4, ct: TestContext.Current.CancellationToken);

        Assert.Equal("shared", fromShared.Definition.Variables["FROM"]);
        Assert.Equal(2, fromShared.ResolvedVersion);

        _repoMock.FindTemplateByNameAsync("application-ci", 4, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Id = 9,
            Name = "application-ci",
            OrganizationId = 4,
            LatestVersion = 2,
            Versions = [new PipelineTemplateVersion { Version = 2, YamlContent = "name: own\nvariables:\n  FROM: own\nstages: []" }]
        });

        var fromOwn = await resolver.ResolveAsync(
            "name: atlas-ci\nextends: application-ci@2\nstages: []", 4, ct: TestContext.Current.CancellationToken);

        Assert.Equal("own", fromOwn.Definition.Variables["FROM"]);
    }

    [Fact]
    public async Task Resolver_LegacyReferenceReportsConcreteLatestVersion()
    {
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Id = 7,
            Name = "base",
            OrganizationId = 1,
            LatestVersion = 3,
            Versions = [new PipelineTemplateVersion { Version = 3, YamlContent = "name: base\nstages: []" }]
        });
        var resolver = new PipelineTemplateResolver(_repoMock);

        var result = await resolver.ResolveAsync("name: child\nextends: base\nstages: []", 1, ct: TestContext.Current.CancellationToken);

        Assert.True(result.UsesLegacyReference);
        Assert.Null(result.RequestedVersion);
        Assert.Equal(3, result.ResolvedVersion);
    }

    [Fact]
    public async Task ResolveTemplateAsync_RequiredParameterWithoutValue_IsRejected()
    {
        var yaml = "name: test\nparameters:\n  - name: target\n    required: true\nstages:\n  - name: deploy\n    steps:\n      - name: run\n        shell: echo ${{ parameters.target }}";

        await Assert.ThrowsAsync<BadRequestException>(() =>
            _sut.ResolveTemplateAsync(yaml, new Dictionary<string, string>(), ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ResolveTemplateAsync_NameOverridesReplaceAppendAndRemove()
    {
        var template = new PipelineTemplate
        {
            Id = 8,
            Name = "base",
            OrganizationId = 1,
            LatestVersion = 1,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    TemplateId = 8,
                    Version = 1,
                    YamlContent = "name: base\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo old\n      - name: obsolete\n        shell: echo obsolete"
                }
            ]
        };
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(template);
        var child = "name: child\nextends: base@1\nstages:\n  - name: build\n    steps:\n      - name: compile\n        shell: echo new\n      - name: obsolete\n        remove: true\n      - name: added\n        shell: echo added";

        var result = await _sut.ResolveTemplateAsync(child, ct: TestContext.Current.CancellationToken);

        Assert.Contains("echo new", result, StringComparison.Ordinal);
        Assert.Contains("echo added", result, StringComparison.Ordinal);
        Assert.DoesNotContain("echo old", result, StringComparison.Ordinal);
        Assert.DoesNotContain("echo obsolete", result, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveTemplateAsync_JobAndStepOverridesReplaceAppendAndRemove()
    {
        _repoMock.FindTemplateByNameAsync("base", 1, Arg.Any<CancellationToken>()).Returns(new PipelineTemplate
        {
            Id = 9,
            Name = "base",
            OrganizationId = 1,
            LatestVersion = 1,
            Versions =
            [
                new PipelineTemplateVersion
                {
                    Version = 1,
                    YamlContent = "name: base\nstages:\n  - name: build\n    jobs:\n      - name: app\n        steps:\n          - name: compile\n            shell: echo old\n          - name: obsolete\n            shell: echo obsolete\n      - name: removed-job\n        steps:\n          - name: old\n            shell: echo old-job"
                }
            ]
        });
        var child = "name: child\nextends: base@1\nstages:\n  - name: build\n    jobs:\n      - name: app\n        steps:\n          - name: compile\n            shell: echo new\n          - name: obsolete\n            remove: true\n          - name: added\n            shell: echo added\n      - name: removed-job\n        remove: true";

        var result = await _sut.ResolveTemplateAsync(child, ct: TestContext.Current.CancellationToken);

        Assert.Contains("echo new", result, StringComparison.Ordinal);
        Assert.Contains("echo added", result, StringComparison.Ordinal);
        Assert.DoesNotContain("echo obsolete", result, StringComparison.Ordinal);
        Assert.DoesNotContain("echo old-job", result, StringComparison.Ordinal);
    }
}
