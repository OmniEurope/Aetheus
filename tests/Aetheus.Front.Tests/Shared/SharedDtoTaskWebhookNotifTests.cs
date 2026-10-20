// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests;

public class SharedDtoTaskWebhookNotifTests
{
    // --- Tasks ---

    [Fact]
    public void CreateTaskRequest_Valid()
    {
        var req = new CreateTaskRequest { Name = "Task", Command = "echo hi" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateTaskRequest_Defaults()
    {
        var req = new CreateTaskRequest { Name = "Task", Command = "cmd" };
        Assert.Equal(ExecutorType.Shell, req.Executor);
        Assert.Equal(300, req.TimeoutSeconds);
        Assert.Empty(req.EnvironmentVariables);
    }

    [Fact]
    public void CreateOperationRequest_Valid()
    {
        var req = new CreateOperationRequest
        {
            Name = "Restart",
            Operation = OperationKind.DockerRestartContainer,
            Target = "nginx"
        };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateOperationRequest_EmptyTarget_Fails()
    {
        var req = new CreateOperationRequest
        {
            Name = "Op",
            Operation = OperationKind.DockerRestartContainer,
            Target = ""
        };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void PendingTaskDto_DefaultValues()
    {
        var dto = new PendingTaskDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(string.Empty, dto.Command);
        Assert.Empty(dto.EnvironmentVariables);
        Assert.Equal(OperationKind.None, dto.Operation);
    }

    // --- Webhooks ---

    [Fact]
    public void CreateWebhookSubscriptionRequest_Valid()
    {
        var req = new CreateWebhookSubscriptionRequest
        {
            EventType = "push",
            TargetUrl = "https://example.com/hook"
        };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateWebhookSubscriptionRequest_EmptyEvent_Fails()
    {
        var req = new CreateWebhookSubscriptionRequest
        {
            EventType = "",
            TargetUrl = "https://example.com/hook"
        };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void UpdateWebhookSubscriptionRequest_Valid()
    {
        var req = new UpdateWebhookSubscriptionRequest
        {
            EventType = "push",
            TargetUrl = "https://example.com/hook"
        };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateWebhookSubscriptionRequest_Defaults()
    {
        var req = new UpdateWebhookSubscriptionRequest { EventType = "e", TargetUrl = "https://x.com" };
        Assert.True(req.IsEnabled);
        Assert.Null(req.Secret);
    }

    // --- Notifications ---

    [Fact]
    public void NotificationChannelDto_DefaultValues()
    {
        var dto = new NotificationChannelDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal("{}", dto.ConfigurationJson);
        Assert.False(dto.IsEnabled);
        Assert.Equal(0, dto.RuleCount);
    }

    [Fact]
    public void CreateNotificationChannelRequest_Valid()
    {
        var req = new CreateNotificationChannelRequest { Name = "Slack", ConfigurationJson = "{}" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateNotificationChannelRequest_EmptyName_Fails()
    {
        var req = new CreateNotificationChannelRequest { Name = "" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void UpdateNotificationChannelRequest_Valid()
    {
        var req = new UpdateNotificationChannelRequest { Name = "Updated", ConfigurationJson = "{}" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateNotificationChannelRequest_Defaults()
    {
        var req = new UpdateNotificationChannelRequest { Name = "t" };
        Assert.True(req.IsEnabled);
    }

    [Fact]
    public void CreateNotificationRuleRequest_Valid()
    {
        var req = new CreateNotificationRuleRequest { EventType = "alert.triggered" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateNotificationRuleRequest_Valid()
    {
        var req = new UpdateNotificationRuleRequest { EventType = "alert.triggered" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateNotificationRuleRequest_Defaults()
    {
        var req = new UpdateNotificationRuleRequest { EventType = "e" };
        Assert.True(req.IsEnabled);
        Assert.Null(req.FilterJson);
    }

    // --- AgentPool ---

    [Fact]
    public void CreateAgentPoolRequest_Valid()
    {
        var req = new CreateAgentPoolRequest { Name = "Pool" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateAgentPoolRequest_Defaults()
    {
        var req = new CreateAgentPoolRequest { Name = "t" };
        Assert.Equal(1, req.MaxConcurrency);
        Assert.Empty(req.ServerIds);
    }

    [Fact]
    public void UpdateAgentPoolRequest_Valid()
    {
        var req = new UpdateAgentPoolRequest { Name = "Updated" };
        Assert.Empty(ValidateModel(req));
    }

    private static List<ValidationResult> ValidateModel(object model)
    {
        var ctx = new ValidationContext(model);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, ctx, results, true);
        return results;
    }
}
