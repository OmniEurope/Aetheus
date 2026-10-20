// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Tests.SharedDtos;

public class SharedDtoMailTeamspeakCronTests
{
    // --- Mail DTOs ---

    [Fact]
    public void MailboxQuotaDto_UsagePercent_Computed()
    {
        var dto = new MailboxQuotaDto { AccountId = 1, Email = "a@b.com", QuotaMb = 1024, UsedMb = 512 };
        Assert.Equal(50.0, dto.UsagePercent);
    }

    [Fact]
    public void MailboxQuotaDto_UsagePercent_ZeroQuota()
    {
        var dto = new MailboxQuotaDto { QuotaMb = 0, UsedMb = 100 };
        Assert.Equal(0, dto.UsagePercent);
    }

    [Fact]
    public void UpdateSpamFilterRequest_Valid()
    {
        var req = new UpdateSpamFilterRequest { RejectScore = 20, AddHeaderScore = 8, GreylistScore = 5 };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateSpamFilterRequest_OutOfRangeScore_Fails()
    {
        var req = new UpdateSpamFilterRequest { RejectScore = 5000 };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void MailAliasDto_DefaultValues()
    {
        var dto = new MailAliasDto();
        Assert.Equal(string.Empty, dto.SourceEmail);
        Assert.Equal(string.Empty, dto.DestinationEmail);
        Assert.True(dto.IsActive);
    }

    [Fact]
    public void UpdateMailAliasRequest_Valid()
    {
        var req = new UpdateMailAliasRequest { DestinationEmail = "new@d.com" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void RequestMailCertificateRequest_Valid()
    {
        var req = new RequestMailCertificateRequest { Email = "admin@example.com" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void RequestMailCertificateRequest_NoEmail_IsValid()
    {
        // The Let's Encrypt contact is optional; the certbot helper registers without one.
        var req = new RequestMailCertificateRequest { Email = null };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void RequestMailCertificateRequest_EmptyEmail_Fails()
    {
        var req = new RequestMailCertificateRequest { Email = "" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void MailDomainDto_DefaultValues()
    {
        var dto = new MailDomainDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.True(dto.IsActive);
        Assert.Equal("default", dto.DkimSelector);
    }

    [Fact]
    public void MailAccountDto_DefaultValues()
    {
        var dto = new MailAccountDto();
        Assert.Equal(string.Empty, dto.Email);
        Assert.Equal(string.Empty, dto.Domain);
        Assert.True(dto.IsActive);
    }

    [Fact]
    public void DkimRotationRequest_Valid()
    {
        var req = new DkimRotationRequest { NewSelector = "sel2024" };
        Assert.Empty(ValidateModel(req));
    }

    // --- Teamspeak DTOs ---

    [Fact]
    public void TeamspeakChannelDto_DefaultValues()
    {
        var dto = new TeamspeakChannelDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(-1, dto.MaxClients);
        Assert.False(dto.IsDefault);
        Assert.False(dto.HasPassword);
        Assert.False(dto.IsPermanent);
    }

    [Fact]
    public void TeamspeakSetupRequest_Defaults()
    {
        var req = new TeamspeakSetupRequest();
        Assert.Equal(9987, req.VoicePort);
        Assert.Equal(10011, req.QueryPort);
    }

    [Fact]
    public void TeamspeakKickRequest_Valid()
    {
        var req = new TeamspeakKickRequest { ClientId = 1, ReasonMessage = "Bye" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void TeamspeakBanRequest_Valid()
    {
        var req = new TeamspeakBanRequest { ClientUniqueId = "uid123" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void TeamspeakCreateChannelRequest_Defaults()
    {
        var req = new TeamspeakCreateChannelRequest { Name = "General" };
        Assert.True(req.IsPermanent);
        Assert.Null(req.ParentId);
        Assert.Null(req.Password);
        Assert.Null(req.MaxClients);
    }

    [Fact]
    public void TeamspeakGlobalMessageRequest_Valid()
    {
        var req = new TeamspeakGlobalMessageRequest { Message = "Hello!" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void TeamspeakLogRequest_Default()
    {
        var req = new TeamspeakLogRequest();
        Assert.Equal(100, req.Lines);
    }

    // --- Cron DTOs ---

    [Fact]
    public void CronJobSaveRequest_Valid()
    {
        var req = new CronJobSaveRequest { User = "root", Schedule = "0 * * * *", Command = "echo hi" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CronJobSaveRequest_EmptyUser_Fails()
    {
        var req = new CronJobSaveRequest { User = "", Schedule = "0 * * * *", Command = "echo" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void CronJobDeleteRequest_Valid()
    {
        var req = new CronJobDeleteRequest { Id = "cron1", User = "root" };
        Assert.Empty(ValidateModel(req));
    }

    // --- Rkhunter DTOs ---

    [Fact]
    public void RkhunterLogRequest_Defaults()
    {
        var req = new RkhunterLogRequest();
        Assert.Equal(200, req.Lines);
    }

    // --- ProjectServer DTOs ---

    [Fact]
    public void CreateProjectServerRequest_Valid()
    {
        var req = new CreateProjectServerRequest
        {
            Type = ProjectServerType.AgentServer,
            DisplayName = "Web Server",
            Host = "web.example.com"
        };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateProjectServerRequest_EmptyName_Fails()
    {
        var req = new CreateProjectServerRequest { DisplayName = "", Host = "h" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void UpdateProjectServerRequest_Valid()
    {
        var req = new UpdateProjectServerRequest { DisplayName = "Updated", Host = "h.com" };
        Assert.Empty(ValidateModel(req));
    }

    // --- Vault ---

    [Fact]
    public void RotateVaultSecretRequest_Valid()
    {
        var req = new RotateVaultSecretRequest { Value = "new-secret-value" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void RotateVaultSecretRequest_EmptyValue_Fails()
    {
        var req = new RotateVaultSecretRequest { Value = "" };
        Assert.NotEmpty(ValidateModel(req));
    }

    // --- Git DTOs ---

    [Fact]
    public void PipelineStatusReport_DefaultValues()
    {
        var dto = new PipelineStatusReport();
        Assert.Equal(string.Empty, dto.State);
        Assert.Null(dto.Description);
        Assert.Null(dto.TargetUrl);
        Assert.Equal("aetheus-ci", dto.Context);
    }

    // --- Service logs ---

    [Fact]
    public void ServiceLogsRequest_Defaults()
    {
        var req = new ServiceLogsRequest { ServiceName = "nginx" };
        Assert.Equal(100, req.Lines);
        Assert.False(req.Follow);
    }

    [Fact]
    public void ServiceActionRequest_Valid()
    {
        var req = new ServiceActionRequest { Action = ServiceAction.Start, ServiceName = "nginx" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void ServiceInstallRequest_Valid()
    {
        var req = new ServiceInstallRequest { ServiceName = "nginx" };
        Assert.Empty(ValidateModel(req));
    }

    // --- Validation attributes (backend side) ---

    [Fact]
    public void OperationTargetValidator_DockerOps_Valid()
    {
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.DockerRestartContainer, "nginx"));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.DockerStartContainer, "app:v1"));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.DockerStopContainer, "web"));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.DockerPullImage, "registry.io/org/img:tag"));
    }

    [Fact]
    public void OperationTargetValidator_ServiceOps_Valid()
    {
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ServiceStart, "nginx"));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ServiceStop, "apache2"));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ServiceRestart, "sshd"));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ServiceStatus, "postfix"));
    }

    [Fact]
    public void OperationTargetValidator_ApacheOps_Valid()
    {
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ApacheReload, "-"));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ApacheTestConfig, "apache2"));
        // empty string is caught by the IsNullOrWhiteSpace guard, so it returns false
        Assert.False(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ApacheReload, ""));
    }

    [Fact]
    public void OperationTargetValidator_AgentSelfUpdate_AlwaysValid()
    {
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.AgentSelfUpdate, null));
        Assert.True(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.AgentSelfUpdate, ""));
    }

    [Fact]
    public void OperationTargetValidator_None_Invalid()
    {
        Assert.False(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.None, "target"));
    }

    [Fact]
    public void OperationTargetValidator_NullTarget_Invalid()
    {
        Assert.False(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.DockerRestartContainer, null));
        Assert.False(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid(OperationKind.ServiceStart, "  "));
    }

    [Fact]
    public void OperationTargetValidator_Unknown_Invalid()
    {
        Assert.False(Aetheus.Shared.Components.Shared.OperationTargetValidator.IsValid((OperationKind)999, "target"));
    }

    // --- BoundedDictionary (backend side) ---

    private class TestBoundedModel
    {
        [Aetheus.Shared.Components.Shared.BoundedDictionary(maxEntries: 3, maxKeyLength: 10, maxValueLength: 20)]
        public Dictionary<string, string>? Data { get; set; }
    }

    [Fact]
    public void BoundedDictionary_Null_Passes()
    {
        var model = new TestBoundedModel { Data = null };
        Assert.Empty(ValidateModel(model));
    }

    [Fact]
    public void BoundedDictionary_Empty_Passes()
    {
        var model = new TestBoundedModel { Data = new() };
        Assert.Empty(ValidateModel(model));
    }

    [Fact]
    public void BoundedDictionary_Valid_Passes()
    {
        var model = new TestBoundedModel { Data = new() { ["k1"] = "v1", ["k2"] = "v2" } };
        Assert.Empty(ValidateModel(model));
    }

    [Fact]
    public void BoundedDictionary_TooManyEntries_Fails()
    {
        var model = new TestBoundedModel
        {
            Data = new() { ["a"] = "1", ["b"] = "2", ["c"] = "3", ["d"] = "4" }
        };
        Assert.NotEmpty(ValidateModel(model));
    }

    [Fact]
    public void BoundedDictionary_KeyTooLong_Fails()
    {
        var model = new TestBoundedModel
        {
            Data = new() { [new string('k', 11)] = "v" }
        };
        Assert.NotEmpty(ValidateModel(model));
    }

    [Fact]
    public void BoundedDictionary_ValueTooLong_Fails()
    {
        var model = new TestBoundedModel
        {
            Data = new() { ["k"] = new string('v', 21) }
        };
        Assert.NotEmpty(ValidateModel(model));
    }

    private static List<ValidationResult> ValidateModel(object model)
    {
        var ctx = new ValidationContext(model);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, ctx, results, true);
        return results;
    }
}
