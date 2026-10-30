// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-82: the run detail says where a deployment put the application, from the variables the run
/// resolved and a deploy step that really completed, and says nothing otherwise.
/// </summary>
public sealed class PipelineRunDeployedAddressTests
{
    private const string BlueGreenSnapshot = """
        name: deploy
        stages:
          - name: Switch
            steps:
              - name: Switch traffic
                type: bluegreen-switch
          - name: Record release
            steps:
              - name: Mark deployed
                type: release
                deployed: true
        """;

    private static PipelineRun Run(
        PipelineStatus status,
        string? snapshot,
        Dictionary<string, string>? variables,
        TaskExecutionStatus switchStatus = TaskExecutionStatus.Success) => new()
        {
            Id = 1,
            Status = status,
            YamlSnapshot = snapshot,
            ResolvedVariablesJson = variables is null ? null : JsonSerializer.Serialize(variables),
            StepRuns =
        [
            new PipelineStepRun { Id = 1, StageName = "Switch", StepName = "Switch traffic", Status = switchStatus },
            new PipelineStepRun { Id = 2, StageName = "Record release", StepName = "Mark deployed", Status = switchStatus }
        ]
        };

    [Fact]
    public void ASucceededBlueGreenDeploymentReadsTheApplicationAndTheApiAddress()
    {
        var dto = PipelineRunDtoMapper.MapRunToDto(Run(PipelineStatus.Success, BlueGreenSnapshot, new()
        {
            ["BG_PUBLIC_ORIGIN"] = "https://app.example.test",
            ["BG_PUBLIC_API_ORIGIN"] = "https://api.example.test/"
        }));

        Assert.NotNull(dto.DeployedAddress);
        Assert.Equal("https://app.example.test", dto.DeployedAddress.AppUrl);
        Assert.Equal("https://api.example.test", dto.DeployedAddress.ApiUrl);
    }

    [Fact]
    public void TheApiHasNoAddressOfItsOwnWhenItAnswersOnTheApplicationOrigin()
    {
        var dto = PipelineRunDtoMapper.MapRunToDto(Run(PipelineStatus.Success, BlueGreenSnapshot, new()
        {
            ["BG_PUBLIC_ORIGIN"] = "https://app.example.test",
            ["BG_PUBLIC_API_ORIGIN"] = "https://app.example.test"
        }));

        Assert.NotNull(dto.DeployedAddress);
        Assert.Null(dto.DeployedAddress.ApiUrl);
    }

    [Fact]
    public void ThePublicUrlVariablesAnswerWhenThePipelineDefinesNoBlueGreenOrigin()
    {
        var dto = PipelineRunDtoMapper.MapRunToDto(Run(PipelineStatus.Success, BlueGreenSnapshot, new()
        {
            ["PUBLIC_APP_URL"] = "https://app.example.test",
            ["PUBLIC_API_URL"] = "https://api.example.test"
        }));

        Assert.NotNull(dto.DeployedAddress);
        Assert.Equal("https://app.example.test", dto.DeployedAddress.AppUrl);
        Assert.Equal("https://api.example.test", dto.DeployedAddress.ApiUrl);
    }

    [Fact]
    public void AFailedRunHasNoDeployedAddress()
    {
        var dto = PipelineRunDtoMapper.MapRunToDto(Run(PipelineStatus.Failed, BlueGreenSnapshot, new()
        {
            ["BG_PUBLIC_ORIGIN"] = "https://app.example.test"
        }));

        Assert.Null(dto.DeployedAddress);
    }

    [Fact]
    public void ASucceededRunWhoseDeployStepDidNotCompleteHasNoDeployedAddress()
    {
        var dto = PipelineRunDtoMapper.MapRunToDto(Run(
            PipelineStatus.Success, BlueGreenSnapshot,
            new() { ["BG_PUBLIC_ORIGIN"] = "https://app.example.test" },
            switchStatus: TaskExecutionStatus.Pending));

        Assert.Null(dto.DeployedAddress);
    }

    [Fact]
    public void ASucceededRunThatDeploysNothingHasNoDeployedAddress()
    {
        const string buildOnly = """
            name: build
            stages:
              - name: Switch
                steps:
                  - name: Switch traffic
                    shell: make
            """;

        var dto = PipelineRunDtoMapper.MapRunToDto(Run(PipelineStatus.Success, buildOnly, new()
        {
            ["PUBLIC_APP_URL"] = "https://app.example.test"
        }));

        Assert.Null(dto.DeployedAddress);
    }

    [Fact]
    public void ADeploymentWithoutAKnownAddressHasNoDeployedAddress()
    {
        var withoutVariables = PipelineRunDtoMapper.MapRunToDto(Run(PipelineStatus.Success, BlueGreenSnapshot, []));
        var unresolved = PipelineRunDtoMapper.MapRunToDto(Run(PipelineStatus.Success, BlueGreenSnapshot, new()
        {
            ["BG_PUBLIC_ORIGIN"] = "https://$(APP_HOST)",
            ["PUBLIC_APP_URL"] = "javascript:alert(1)"
        }));

        Assert.Null(withoutVariables.DeployedAddress);
        Assert.Null(unresolved.DeployedAddress);
    }
}
