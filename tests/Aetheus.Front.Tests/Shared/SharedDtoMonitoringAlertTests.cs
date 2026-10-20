// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests;

public class SharedDtoMonitoringAlertTests
{
    // --- ServiceInfo / Monitoring ---

    [Fact]
    public void ServiceInfoDto_DefaultValues()
    {
        var dto = new ServiceInfoDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(string.Empty, dto.Status);
        Assert.False(dto.IsRunning);
        Assert.False(dto.IsManageable);
        Assert.True(dto.IsInstalled);
    }

    [Fact]
    public void ServiceActionRequest_Valid()
    {
        var req = new ServiceActionRequest { Action = ServiceAction.Start, ServiceName = "nginx" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void ServiceActionRequest_EmptyName_Fails()
    {
        var req = new ServiceActionRequest { Action = ServiceAction.Start, ServiceName = "" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void ServiceInstallRequest_Valid()
    {
        var req = new ServiceInstallRequest { ServiceName = "nginx" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void ServiceLogsRequest_DefaultValues()
    {
        var req = new ServiceLogsRequest { ServiceName = "svc" };
        Assert.Equal(100, req.Lines);
        Assert.False(req.Follow);
    }

    // --- AlertRule ---

    [Fact]
    public void CreateAlertRuleRequest_Valid()
    {
        var req = new CreateAlertRuleRequest { Name = "High CPU", Threshold = 90 };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateAlertRuleRequest_EmptyName_Fails()
    {
        var req = new CreateAlertRuleRequest { Name = "" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void CreateAlertRuleRequest_DefaultSustained()
    {
        var req = new CreateAlertRuleRequest { Name = "Test" };
        Assert.Equal(60, req.SustainedSeconds);
    }

    [Fact]
    public void UpdateAlertRuleRequest_Valid()
    {
        var req = new UpdateAlertRuleRequest { Name = "Updated", Threshold = 80 };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateAlertRuleRequest_Defaults()
    {
        var req = new UpdateAlertRuleRequest { Name = "Test" };
        Assert.True(req.IsEnabled);
        Assert.Equal(60, req.SustainedSeconds);
    }

    // --- EnvironmentCheck ---

    // --- AlertTriggered ---

    private static List<ValidationResult> ValidateModel(object model)
    {
        var ctx = new ValidationContext(model);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, ctx, results, true);
        return results;
    }
}
