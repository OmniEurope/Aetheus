// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;
using Aetheus.Back.Services;
using NSubstitute;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>API partition scanning resolves every definition and sends the complete list, leaving
/// the single URL empty so unsupported agents fail closed.</summary>
public sealed class PipelineScannerApiPartitionsTests
{
    private readonly IPipelineRepository _repo = Substitute.For<IPipelineRepository>();

    [Fact]
    public async Task R2099_OneStepWithSeveralDefinitions_DispatchesOneTaskCarryingTheResolvedList()
    {
        var (step, env) = await DispatchAsync(
            "http://127.0.0.1:$(PORT)/openapi/dast-0.json", "http://127.0.0.1:$(PORT)/openapi/dast-1.json");

        Assert.NotEqual(TaskExecutionStatus.Failed, step.Status);
        Assert.Equal(
            "http://127.0.0.1:10081/openapi/dast-0.json\nhttp://127.0.0.1:10081/openapi/dast-1.json",
            env!["AETHEUS_SCANNER_API_SPECIFICATION_URLS"]);
        Assert.Equal(string.Empty, env["AETHEUS_SCANNER_API_SPECIFICATION_URL"]);
        _repo.Received(1).TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task R2099_ADefinitionOffTheTargetHost_FailsTheStepBeforeDispatch()
    {
        var (step, env) = await DispatchAsync(
            "http://127.0.0.1:$(PORT)/openapi/dast-0.json", "https://elsewhere.example/openapi.json");

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        Assert.Null(env);
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public void R2099_TheYamlValidator_RefusesBothFormsAtOnce()
    {
        var step = new PipelineStepDefinition
        {
            Name = "zap",
            Type = "scanner",
            Scanner = "zap-api",
            TargetUrl = "$(TARGET)",
            ApiSpecificationUrl = "$(ONE)",
            ApiSpecificationUrls = ["$(ONE)", "$(TWO)"],
            ApiSpecificationFormat = "openapi"
        };
        var errors = new List<string>();

        PipelineStepAnalysisValidator.Validate(errors, step, "stage 'ZAP API'", "qa");

        Assert.Contains(errors, error => error.Contains("both api_specification_url and api_specification_urls", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-in-the-manifest")]
    public async Task AScannerAbsentFromTheManifest_FailsTheStepWithoutDispatch(string? scanner)
    {
        var step = new PipelineStepRun { Id = 4, PipelineRunId = 1, StageName = "Scan", StepName = "scan" };

        await new PipelineScannerTaskFactory(_repo, Substitute.For<IEncryptionService>(), TimeProvider.System).CreateScannerTaskAsync(
            1, new Server { Id = 5, Name = "linux-01" }, step, new PipelineStepDefinition { Name = "scan", Type = "scanner", Scanner = scanner },
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), new PipelineStageDefinition { Name = "Scan" },
            stageIsContainer: false, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        await _repo.Received(1).AppendRunWarningsAsync(
            1, Arg.Is<IReadOnlyCollection<string>>(warnings => StoredProblem.English(warnings.Single()) == "Scanner step 'scan': scanner key is absent from scanner-manifest.json."
                && StoredProblem.CodeOf(warnings.Single()) == "StepFailedScanner"),
            Arg.Any<CancellationToken>());
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    [Fact]
    public async Task ACandidateScanOnAnAgentWithoutTheManifest_FailsTheStepNamingWhatItReported()
    {
        var step = new PipelineStepRun { Id = 4, PipelineRunId = 1, StageName = "Scan", StepName = "secrets" };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["UPSTREAM_PIPELINE"] = "aetheus-candidate" };

        await new PipelineScannerTaskFactory(_repo, Substitute.For<IEncryptionService>(), TimeProvider.System).CreateScannerTaskAsync(
            1, new Server { Id = 5, Name = "linux-01" }, step, new PipelineStepDefinition { Name = "secrets", Type = "scanner", Scanner = "gitleaks" },
            variables, new PipelineStageDefinition { Name = "Scan" }, stageIsContainer: false, TestContext.Current.CancellationToken);

        Assert.Equal(TaskExecutionStatus.Failed, step.Status);
        await _repo.Received(1).AppendRunWarningsAsync(
            1, Arg.Is<IReadOnlyCollection<string>>(warnings => warnings.Single().Contains("agent 'linux-01' reported no manifest hash", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        _repo.DidNotReceive().TrackTask(Arg.Any<ServerTask>());
    }

    private async Task<(PipelineStepRun Step, Dictionary<string, string>? Env)> DispatchAsync(params string[] specifications)
    {
        var encryption = Substitute.For<IEncryptionService>();
        encryption.EncryptValue(Arg.Any<string>()).Returns(call => call.Arg<string>());
        var pipeline = new Pipeline { Id = 1, Name = "aetheus-qa-extended", ProjectId = 7 };
        _repo.GetPipelineRunWithPipelineAsync(1, Arg.Any<CancellationToken>())
            .Returns(new PipelineRun { Id = 1, PipelineId = 1, Pipeline = pipeline });
        _repo.GetPipelineProjectIdAsync(pipeline, Arg.Any<CancellationToken>()).Returns(7);
        _repo.FindEnvironmentByNameForProjectAsync("qa", 7, Arg.Any<CancellationToken>())
            .Returns(new Aetheus.Back.Data.Entities.Environment
            {
                Id = 2,
                Name = "qa",
                ProjectId = 7,
                Type = EnvironmentType.Testing,
                DastEnabled = true,
                DastIsEphemeral = true,
                DastAllowedHosts = "127.0.0.1"
            });
        var factory = new PipelineScannerTaskFactory(_repo, encryption, TimeProvider.System);
        var step = new PipelineStepRun { Id = 3, PipelineRunId = 1, StageName = "ZAP API", StepName = "Produce OpenAPI DAST evidence against V" };
        var definition = new PipelineStepDefinition
        {
            Name = step.StepName,
            Type = "scanner",
            Scanner = "zap-api",
            TargetUrl = "http://127.0.0.1:$(PORT)",
            ApiSpecificationUrls = [.. specifications],
            ApiSpecificationFormat = "openapi",
            TimeoutSeconds = 9600
        };
        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["PORT"] = "10081" };

        await factory.CreateScannerTaskAsync(
            1, new Server { Id = 5, Name = "linux-01" }, step, definition, variables,
            new PipelineStageDefinition { Name = "ZAP API", Environment = "qa" }, stageIsContainer: false,
            TestContext.Current.CancellationToken);

        var task = _repo.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IPipelineRepository.TrackTask))
            .Select(call => (ServerTask)call.GetArguments()[0]!)
            .SingleOrDefault();
        return (step, task is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(task.EnvironmentVariables!));
    }
}
