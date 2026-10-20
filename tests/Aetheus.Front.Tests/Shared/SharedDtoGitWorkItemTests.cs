// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests;

public class SharedDtoGitWorkItemTests
{
    [Fact]
    public void CreateGitConnectionRequest_Validation_Valid()
    {
        var req = new CreateGitConnectionRequest
        {
            ProjectId = 1,
            OwnerOrGroup = "org",
            RepositoryName = "repo"
        };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void CreateGitConnectionRequest_Validation_EmptyOwner_Fails()
    {
        var req = new CreateGitConnectionRequest
        {
            ProjectId = 1,
            OwnerOrGroup = "",
            RepositoryName = "repo"
        };
        var results = ValidateModel(req);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void UpdateGitConnectionRequest_DefaultAutoSync()
    {
        var req = new UpdateGitConnectionRequest();
        Assert.True(req.AutoSyncEnabled);
    }

    [Fact]
    public void CreateBranchPolicyRequest_Validation_Valid()
    {
        var req = new CreateBranchPolicyRequest { BranchPattern = "main" };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void UpdateBranchPolicyRequest_Defaults()
    {
        var req = new UpdateBranchPolicyRequest();
        Assert.True(req.IsEnabled);
        Assert.Equal(string.Empty, req.BranchPattern);
    }

    [Fact]
    public void PipelineStatusReport_Defaults()
    {
        var dto = new PipelineStatusReport();
        Assert.Equal("aetheus-ci", dto.Context);
        Assert.Equal(string.Empty, dto.State);
    }

    [Fact]
    public void CreateWorkItemRequest_Validation_Valid()
    {
        var req = new CreateWorkItemRequest
        {
            ProjectId = 1,
            Title = "New task",
            Type = WorkItemType.Task
        };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void CreateWorkItemRequest_Validation_EmptyTitle_Fails()
    {
        var req = new CreateWorkItemRequest { ProjectId = 1, Title = "" };
        var results = ValidateModel(req);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void CreateWorkItemRequest_DefaultStatus_IsNew()
    {
        var req = new CreateWorkItemRequest { Title = "t" };
        Assert.Equal(WorkItemStatus.New, req.Status);
    }

    [Fact]
    public void UpdateWorkItemRequest_Validation_Valid()
    {
        var req = new UpdateWorkItemRequest { Title = "Updated" };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    private static List<ValidationResult> ValidateModel(object model)
    {
        var ctx = new ValidationContext(model);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, ctx, results, true);
        return results;
    }
}
