// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Audit;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Exceptions;
using Aetheus.Shared.Components.Pipelines;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-24: what the declaration asks of its collaborators, on doubles. The real commit, authorization,
/// readiness re-read and refusal are proved over HTTP in <c>PipelineHolderDeclarationHttpIntegrationTests</c>.</summary>
public sealed class PipelineHolderDeclarationServiceTests
{
    private const string Yaml = "name: n\nstages: []\n";

    private readonly IPipelineService _service = Substitute.For<IPipelineService>();
    private readonly IPipelineRepository _repository = Substitute.For<IPipelineRepository>();
    private readonly IPipelineTemplateResolver _resolver = Substitute.For<IPipelineTemplateResolver>();
    private readonly IPipelineRequirementsChecker _requirements = Substitute.For<IPipelineRequirementsChecker>();
    private readonly IAuditService _audit = Substitute.For<IAuditService>();
    private readonly PipelineHolderDeclarationService _declarations;
    private readonly PipelineDto _pipeline = new()
    {
        Id = 7,
        Name = "n",
        Description = "d",
        YamlDefinition = Yaml,
        ProjectId = 3,
        SourceRepositoryId = 4,
        SourceBranch = "develop",
        AiRunnerProfileId = 5
    };

    public PipelineHolderDeclarationServiceTests()
    {
        _service.ValidateYamlStrictAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new YamlValidationResultDto { IsValid = true });
        _service.UpdatePipelineAsync(7, Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>()).Returns(_pipeline);
        _requirements.FindVaultIdsAsync(Arg.Any<IReadOnlyCollection<string>>(), 3, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int> { ["r24-proof-vault"] = 44 });
        _requirements.FindLibraryIdsAsync(Arg.Any<IReadOnlyCollection<string>>(), 3, Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, int>());
        _declarations = new PipelineHolderDeclarationService(
            _service, _repository, _resolver, _requirements, _audit, NullLogger<PipelineHolderDeclarationService>.Instance);
    }

    private static DeclarePipelineHolderRequest Vault(string name = "r24-proof-vault") =>
        new() { Kind = PipelineSetupReadinessContainerKind.Vault, Name = name };

    [Fact]
    public async Task Prepare_NamesTheHolderTheProjectHolds_AndTheYamlWithItDeclared()
    {
        var declaration = await _declarations.PrepareAsync(_pipeline, Vault("R24-Proof-Vault"), TestContext.Current.CancellationToken);

        Assert.Equal(44, declaration.HolderId);
        Assert.True(declaration.Changed);
        Assert.Equal("name: n\nvaults:\n  - r24-proof-vault\nstages: []\n", declaration.Yaml);
    }

    [Fact]
    public async Task Prepare_RefusesAHolderTheProjectDoesNotHold_WithTheReason()
    {
        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            _declarations.PrepareAsync(_pipeline, Vault("someone-elses"), TestContext.Current.CancellationToken));

        Assert.Contains("no vault named 'someone-elses'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_RefusesAPipelineWithoutAProject()
    {
        await Assert.ThrowsAsync<BadRequestException>(() => _declarations.PrepareAsync(
            _pipeline with { ProjectId = null }, Vault(), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Prepare_RefusesAYamlLayoutItCannotKeep_WithTheReason()
    {
        var odd = _pipeline with { YamlDefinition = "name: n\nvaults: single\n" };

        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            _declarations.PrepareAsync(odd, Vault(), TestContext.Current.CancellationToken));

        Assert.Contains("vaults", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Prepare_ForAChildOfATemplate_CarriesTheTemplatesNames_BecauseTheChildListReplacesThem()
    {
        var child = _pipeline with { YamlDefinition = "name: n\nextends: tpl@1\n" };
        _repository.GetPipelineOrganizationIdAsync(7, Arg.Any<CancellationToken>()).Returns(9);
        _resolver.ResolveAsync(child.YamlDefinition, 9, null, Arg.Any<CancellationToken>()).Returns(new PipelineTemplateResolution(
            new PipelineYamlDefinition { Name = "n", Vaults = ["from-template"] }, string.Empty, "tpl", 1, 1, false));

        var declaration = await _declarations.PrepareAsync(child, Vault(), TestContext.Current.CancellationToken);

        Assert.Equal("name: n\nextends: tpl@1\nvaults:\n  - from-template\n  - r24-proof-vault\n", declaration.Yaml);
    }

    [Fact]
    public async Task Apply_GoesThroughTheUpdate_WithEverySettingOfThePipelineKept_AndIsAudited()
    {
        var declaration = await _declarations.PrepareAsync(_pipeline, Vault(), TestContext.Current.CancellationToken);

        await _declarations.ApplyAsync(_pipeline, declaration, TestContext.Current.CancellationToken);

        await _service.Received(1).UpdatePipelineAsync(7, Arg.Is<UpdatePipelineRequest>(request =>
            request.YamlDefinition == declaration.Yaml && request.Name == "n" && request.Description == "d"
            && request.ProjectId == 3 && request.SourceRepositoryId == 4 && request.SourceBranch == "develop"
            && request.AiRunnerProfileId == 5), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            "HolderDeclared", "Pipeline", 7, "vaults: + 'r24-proof-vault'", Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_OfAHolderAlreadyDeclared_WritesNothing()
    {
        var declared = _pipeline with { YamlDefinition = "name: n\nvaults: [r24-proof-vault]\n" };
        var declaration = await _declarations.PrepareAsync(declared, Vault(), TestContext.Current.CancellationToken);

        var result = await _declarations.ApplyAsync(declared, declaration, TestContext.Current.CancellationToken);

        Assert.False(declaration.Changed);
        Assert.Same(declared, result);
        await _service.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Apply_RefusedByTheSaveValidation_SaysWhy_AndWritesNothing()
    {
        _service.ValidateYamlStrictAsync(
                Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new YamlValidationResultDto { IsValid = false, Errors = ["stage 'x' has no steps", "second reason"] });
        var declaration = await _declarations.PrepareAsync(_pipeline, Vault(), TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<BadRequestException>(() =>
            _declarations.ApplyAsync(_pipeline, declaration, TestContext.Current.CancellationToken));

        Assert.Equal("stage 'x' has no steps; second reason", exception.Message);
        await _service.DidNotReceive().UpdatePipelineAsync(
            Arg.Any<int>(), Arg.Any<UpdatePipelineRequest>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default!, default!, default, default, TestContext.Current.CancellationToken);
    }
}
