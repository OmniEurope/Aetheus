// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

public class PipelineModelTests
{
    [Fact]
    public void R12_ASave_KeepsTheRepositoryTheDefinitionIsReadFrom()
    {
        var pipeline = new PipelineDto
        {
            Id = 197,
            Name = "oe1-deploy-prod",
            ProjectId = 21,
            SourceRepositoryId = 33,
            SourceBranch = null,
            YamlDefinition = "name: oe1-deploy-prod\n"
        };

        var model = PipelineModel.From(pipeline, fallbackBranch: null);
        model.SourceBranch = "main";
        var request = model.ToUpdateRequest();

        Assert.Equal((33, "main"), (request.SourceRepositoryId, request.SourceBranch));
    }
}
