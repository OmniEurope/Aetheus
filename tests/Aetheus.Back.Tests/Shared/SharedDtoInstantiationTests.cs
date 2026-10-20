// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Back.Tests;

public class SharedDtoInstantiationTests
{
    [Fact]
    public void PullRequestDiffDto_DefaultValues()
    {
        var dto = new PullRequestDiffDto();
        Assert.Empty(dto.FileDiffs);
        Assert.NotNull(dto.Stats);
    }

    [Fact]
    public void PipelineTemplateParameterDefinition_DefaultsAndValues()
    {
        var dto = new PipelineTemplateParameterDefinition();
        Assert.Equal("string", dto.Type);
        Assert.Empty(dto.AllowedValues);

        var dto2 = new PipelineTemplateParameterDefinition
        {
            Name = "env",
            Type = "choice",
            Default = "prod",
            AllowedValues = ["dev", "staging", "prod"]
        };
        Assert.Equal("env", dto2.Name);
        Assert.Equal(3, dto2.AllowedValues.Count);
    }

    [Fact]
    public void PipelineDeploymentStrategy_Defaults()
    {
        var dto = new PipelineDeploymentStrategy();
        Assert.Equal("runOnce", dto.Type);
        Assert.Equal(1, dto.MaxParallel);
    }

    [Fact]
    public void DockerShellRequest_Defaults()
    {
        var dto = new DockerShellRequest();
        Assert.Equal("/bin/sh", dto.Shell);
    }
}
