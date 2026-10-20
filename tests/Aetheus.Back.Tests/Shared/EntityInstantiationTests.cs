// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Data.Entities;

namespace Aetheus.Back.Tests;

public class EntityInstantiationTests
{
    [Fact]
    public void EnvironmentCheck_DefaultValues()
    {
        var entity = new EnvironmentCheck();
        Assert.True(entity.IsRequired);
        Assert.Equal(300, entity.TimeoutSeconds);
    }

    [Fact]
    public void PipelineTemplate_Properties()
    {
        var entity = new PipelineTemplate
        {
            Id = 1,
            Name = "CI",
            Description = "desc",
            Category = "Build",
            YamlContent = "stages:",
            Version = 2,
            Changelog = "v2"
        };
        Assert.Equal(2, entity.Version);
    }

    [Fact]
    public void PipelineTemplate_DefaultVersion()
    {
        var entity = new PipelineTemplate();
        Assert.Equal(1, entity.Version);
    }

    [Fact]
    public void PipelineStepRun_DefaultStatus()
    {
        var entity = new PipelineStepRun();
        Assert.Equal(TaskExecutionStatus.Pending, entity.Status);
    }

    [Fact]
    public void PipelineRun_DefaultStatus()
    {
        var entity = new PipelineRun();
        Assert.Equal(PipelineStatus.Pending, entity.Status);
        Assert.Equal("{}", entity.AdditionalVariablesJson);
    }
}
