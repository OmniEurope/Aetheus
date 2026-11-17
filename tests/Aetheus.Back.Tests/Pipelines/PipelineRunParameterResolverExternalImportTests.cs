// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Exceptions;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-11 security review, finding 5: the caller's run parameters re-resolve the templates after the preparation
/// checked an imported definition, so the import policy runs again on the definition they resolve to. Real parameter
/// and template resolvers; the repository is a double.
/// </summary>
public sealed class PipelineRunParameterResolverExternalImportTests
{
    private const string Yaml = """
        name: oe1-ci
        parameters:
          - name: pool
            type: string
            default: ""
        stages:
          - name: Build
            pool: ${{ parameters.pool }}
            steps:
              - name: Compile
                shell: dotnet build
        """;

    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();
    private readonly PipelineRunParameterResolver _sut;

    public PipelineRunParameterResolverExternalImportTests()
    {
        _repo.GetPipelineOrganizationIdAsync(5, Arg.Any<CancellationToken>()).Returns(1);
        _repo.FindCandidateTargetServerIdsAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<OsType>(),
                Arg.Any<int?>(), Arg.Any<bool>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<int>());
        _sut = new PipelineRunParameterResolver(
            _repo, new PipelineTemplateResolver(_repo), new PipelineDispatchServerResolver(_repo));
    }

    private static PipelineRunPreparation Preparation(bool imported) => new()
    {
        PipelineId = 5,
        Pipeline = new Pipeline
        {
            Id = 5,
            Name = "oe1-ci",
            ProjectId = 7,
            YamlDefinition = Yaml,
            ExternalImportCommit = imported ? "0123456789abcdef0123456789abcdef01234567" : null
        },
        YamlSnapshot = Yaml,
        Definition = YamlParsingHelper.ParseAndValidate(Yaml)!,
        EffectiveProjectId = 7,
        TargetServerIds = []
    };

    [Fact]
    public async Task ImportedPipeline_ParameterThatSelectsAPool_IsRefused()
    {
        var refusal = await Assert.ThrowsAsync<BadRequestException>(() => _sut.ResolveRunParametersAsync(
            Preparation(imported: true), new Dictionary<string, string> { ["pool"] = "prod-pool" },
            TestContext.Current.CancellationToken));

        Assert.Contains("(pool)", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ImportedPipeline_ParameterLeftEmpty_Runs()
    {
        var resolved = await _sut.ResolveRunParametersAsync(
            Preparation(imported: true), null, TestContext.Current.CancellationToken);

        Assert.True(string.IsNullOrEmpty(Assert.Single(resolved.EffectiveStages).Pool));
    }

    [Fact]
    public async Task PipelineDefinedInAetheus_ParameterThatSelectsAPool_IsNotTheImportPolicysBusiness()
    {
        var resolved = await _sut.ResolveRunParametersAsync(
            Preparation(imported: false), new Dictionary<string, string> { ["pool"] = "prod-pool" },
            TestContext.Current.CancellationToken);

        Assert.Equal("prod-pool", Assert.Single(resolved.EffectiveStages).Pool);
    }
}
