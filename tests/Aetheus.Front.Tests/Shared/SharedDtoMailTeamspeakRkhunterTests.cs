// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests;

public class SharedDtoMailTeamspeakRkhunterTests
{
    // --- Mail DTOs ---

    [Fact]
    public void MailboxQuotaDto_UsagePercent_Computed()
    {
        var dto = new MailboxQuotaDto { QuotaMb = 200, UsedMb = 50 };
        Assert.Equal(25.0, dto.UsagePercent);
    }

    [Fact]
    public void MailboxQuotaDto_UsagePercent_ZeroQuota()
    {
        var dto = new MailboxQuotaDto { QuotaMb = 0, UsedMb = 50 };
        Assert.Equal(0, dto.UsagePercent);
    }

    [Fact]
    public void UpdateSpamFilterRequest_Defaults()
    {
        var req = new UpdateSpamFilterRequest();
        Assert.Equal(15, req.RejectScore);
        Assert.Equal(6, req.AddHeaderScore);
        Assert.Equal(4, req.GreylistScore);
    }

    [Fact]
    public void MailAliasDto_Defaults()
    {
        var dto = new MailAliasDto();
        Assert.Equal(string.Empty, dto.SourceEmail);
        Assert.Equal(string.Empty, dto.DestinationEmail);
        Assert.True(dto.IsActive);
    }

    [Fact]
    public void RequestMailCertificateRequest_Valid()
    {
        var req = new RequestMailCertificateRequest { Email = "a@example.com" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void MailDomainDto_Defaults()
    {
        var dto = new MailDomainDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.True(dto.IsActive);
        Assert.Equal("default", dto.DkimSelector);
    }

    // --- Teamspeak DTOs ---

    [Fact]
    public void TeamspeakChannelDto_Defaults()
    {
        var dto = new TeamspeakChannelDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(-1, dto.MaxClients);
    }

    // --- Cron DTOs ---

    [Fact]
    public void CronJobSaveRequest_Valid()
    {
        var req = new CronJobSaveRequest { User = "root", Schedule = "* * * * *", Command = "echo" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CronJobDeleteRequest_Valid()
    {
        var req = new CronJobDeleteRequest { Id = "c1", User = "root" };
        Assert.Empty(ValidateModel(req));
    }

    // --- Rkhunter DTOs ---

    // --- ProjectServer DTOs ---

    [Fact]
    public void CreateProjectServerRequest_Valid()
    {
        var req = new CreateProjectServerRequest
        {
            Type = ProjectServerType.AgentServer,
            DisplayName = "Web",
            Host = "web.com"
        };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateProjectServerRequest_Valid()
    {
        var req = new UpdateProjectServerRequest { DisplayName = "Updated", Host = "h.com" };
        Assert.Empty(ValidateModel(req));
    }

    // --- Docker ---

    // --- Vault ---

    // --- Git ---

    [Fact]
    public void PipelineStatusReport_Defaults()
    {
        var dto = new PipelineStatusReport();
        Assert.Equal(string.Empty, dto.State);
        Assert.Equal("aetheus-ci", dto.Context);
    }

    // --- Service DTOs ---

    [Fact]
    public void ServiceLogsRequest_Defaults()
    {
        var req = new ServiceLogsRequest { ServiceName = "svc" };
        Assert.Equal(100, req.Lines);
        Assert.False(req.Follow);
    }

    [Fact]
    public void ServiceActionRequest_Valid()
    {
        var req = new ServiceActionRequest { Action = ServiceAction.Restart, ServiceName = "nginx" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void ServiceInstallRequest_Valid()
    {
        var req = new ServiceInstallRequest { ServiceName = "nginx" };
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
