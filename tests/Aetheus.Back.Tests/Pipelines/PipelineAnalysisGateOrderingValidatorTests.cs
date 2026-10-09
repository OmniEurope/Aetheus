// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests;

public sealed class PipelineAnalysisGateOrderingValidatorTests
{
    [Fact]
    public void Validate_AcceptsProducerGateAndPackagingChain()
    {
        var stages = new List<PipelineStageDefinition>
        {
            Stage("Coverage", [], Step("Publish", "coverage")),
            Stage("Gate", ["Coverage"], Gate("quality")),
            Stage("Package", ["Gate"], Step("Package", "artifacts"))
        };

        Assert.Empty(PipelineAnalysisGateOrderingValidator.Validate(stages));
    }

    [Fact]
    public void Validate_RejectsGateThatDoesNotDependOnProducer()
    {
        var stages = new List<PipelineStageDefinition>
        {
            Stage("Coverage", [], Step("Publish", "coverage")),
            Stage("Gate", [], Gate("quality"))
        };

        var error = Assert.Single(PipelineAnalysisGateOrderingValidator.Validate(stages));

        Assert.Contains("must run after producer", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsPackagingThatCanRunBeforeGate()
    {
        var stages = new List<PipelineStageDefinition>
        {
            Stage("Coverage", [], Step("Publish", "coverage")),
            Stage("Gate", ["Coverage"], Gate("quality")),
            StageWithArtifacts("Package", ["Coverage"])
        };

        var error = Assert.Single(PipelineAnalysisGateOrderingValidator.Validate(stages));

        Assert.Contains("must depend on analysis gate", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsProducerBeforeGateInSameStage()
    {
        // BACK-016: the steps of a stage run concurrently, so the declaration order is no barrier.
        var stage = Stage("Analysis", [], Step("Publish", "coverage"), Gate("quality"));

        var error = Assert.Single(PipelineAnalysisGateOrderingValidator.Validate([stage]));

        Assert.Contains("cannot share stage 'Analysis' with producer 'Publish'", error, StringComparison.Ordinal);
        Assert.Contains("run concurrently", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsSecurityScannerBesideItsGate()
    {
        var stage = Stage("Security", [], Scanner("Secrets", "gitleaks"), Gate("security"));

        var error = Assert.Single(PipelineAnalysisGateOrderingValidator.Validate([stage]));

        Assert.Contains("cannot share stage 'Security'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsProducerAfterGateInSameStage()
    {
        var stage = Stage("Analysis", [], Gate("quality"), Step("Publish", "coverage"));

        var error = Assert.Single(PipelineAnalysisGateOrderingValidator.Validate([stage]));

        Assert.Contains("cannot share stage 'Analysis'", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_SeparatesSecurityAndQualityProducers()
    {
        var stages = new List<PipelineStageDefinition>
        {
            Stage("Security", [], Scanner("Secrets", "gitleaks")),
            Stage("SecurityGate", ["Security"], Gate("security")),
            Stage("Quality", [], Step("Publish", "coverage")),
            Stage("QualityGate", ["Quality"], Gate("quality"))
        };

        Assert.Empty(PipelineAnalysisGateOrderingValidator.Validate(stages));
    }

    [Fact]
    public void Validate_AcceptsPipelineWithoutGate()
    {
        var stages = new List<PipelineStageDefinition>
        {
            Stage("Coverage", [], Step("Publish", "coverage")),
            Stage("Package", ["Coverage"], Step("Package", "artifacts"))
        };

        Assert.Empty(PipelineAnalysisGateOrderingValidator.Validate(stages));
    }

    [Fact]
    public void Validate_RejectsDuplicateGateScope()
    {
        var stages = new List<PipelineStageDefinition>
        {
            Stage("FirstGate", [], Gate("quality")),
            Stage("SecondGate", [], Gate("quality"))
        };

        var error = Assert.Single(PipelineAnalysisGateOrderingValidator.Validate(stages));

        Assert.Contains("only one quality analysis gate", error, StringComparison.Ordinal);
    }

    private static PipelineStageDefinition Stage(
        string name,
        List<string> dependencies,
        params PipelineStepDefinition[] steps) =>
        new() { Name = name, DependsOn = dependencies, Steps = [.. steps] };

    private static PipelineStageDefinition StageWithArtifacts(string name, List<string> dependencies) =>
        new() { Name = name, DependsOn = dependencies, Artifacts = ["output/**"], Steps = [Step("Package")] };

    private static PipelineStepDefinition Step(string name, string? type = null) =>
        new() { Name = name, Type = type };

    private static PipelineStepDefinition Gate(string scope) =>
        new() { Name = $"{scope} gate", Type = "analysis-gate", AnalysisScope = scope };

    private static PipelineStepDefinition Scanner(string name, string key) =>
        new() { Name = name, Type = "scanner", Scanner = key };
}
