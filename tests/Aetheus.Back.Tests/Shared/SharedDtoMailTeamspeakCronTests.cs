// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;
using Aetheus.Shared.Components.Organizations;

namespace Aetheus.Back.Tests.SharedDtos;

public class SharedDtoMailTeamspeakCronTests
{
    // --- Mail DTOs ---

    [Fact]
    public void MailQueueItemDto_DefaultValues()
    {
        var dto = new MailQueueItemDto();
        Assert.Equal(string.Empty, dto.Id);
        Assert.Equal(string.Empty, dto.Sender);
        Assert.Equal(string.Empty, dto.Recipient);
        Assert.Equal(0, dto.SizeBytes);
        Assert.Equal(string.Empty, dto.Status);
    }

    [Fact]
    public void MailCertificateDto_DefaultValues()
    {
        var dto = new MailCertificateDto();
        Assert.Equal(string.Empty, dto.Hostname);
        Assert.Equal(string.Empty, dto.Issuer);
        Assert.Null(dto.ExpiresAt);
        Assert.False(dto.UsesLetsEncryptLineage);
    }

    [Fact]
    public void MailCertificateDto_CanSetAll()
    {
        var dto = new MailCertificateDto
        {
            Hostname = "mail.example.com",
            CertPath = "/etc/letsencrypt/live/mail.example.com/fullchain.pem",
            IsReadable = true,
            Issuer = "CN=R11, O=Let's Encrypt, C=US",
            ExpiresAt = DateTime.UtcNow.AddDays(90),
            UsesLetsEncryptLineage = true
        };
        Assert.Equal("mail.example.com", dto.Hostname);
        Assert.True(dto.IsReadable);
        Assert.True(dto.UsesLetsEncryptLineage);
    }

    [Fact]
    public void MailboxQuotaDto_DefaultValues()
    {
        var dto = new MailboxQuotaDto();
        Assert.Equal(string.Empty, dto.Email);
        Assert.Equal(0, dto.QuotaMb);
        Assert.Equal(0, dto.UsedMb);
        Assert.Equal(0, dto.UsagePercent);
    }

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
    public void SpamFilterConfigDto_DefaultValues()
    {
        var dto = new SpamFilterConfigDto();
        Assert.False(dto.IsInstalled);
        Assert.False(dto.IsRunning);
        Assert.Equal(string.Empty, dto.Version);
        Assert.Null(dto.RejectScore);
        Assert.Null(dto.AddHeaderScore);
        Assert.Null(dto.GreylistScore);
    }

    [Fact]
    public void SpamFilterConfigDto_CanSetAll()
    {
        var dto = new SpamFilterConfigDto
        {
            IsInstalled = true,
            IsRunning = true,
            Name = "rspamd",
            Version = "3.4",
            RejectScore = 15,
            AddHeaderScore = 6,
            GreylistScore = 4
        };
        Assert.True(dto.IsInstalled);
        Assert.Equal(15, dto.RejectScore);
        Assert.Equal("rspamd", dto.Name);
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
    public void MailAliasDto_CanSetAll()
    {
        var dto = new MailAliasDto
        {
            Id = 1,
            SourceEmail = "alias@d.com",
            DestinationEmail = "real@d.com",
            IsActive = false,
            CreatedAt = DateTime.UtcNow
        };
        Assert.Equal("alias@d.com", dto.SourceEmail);
        Assert.False(dto.IsActive);
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
        // Recette R1-6: the Let's Encrypt contact is optional; the certbot helper registers without one.
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
    public void MailDataDto_DefaultValues()
    {
        var dto = new MailDataDto();
        Assert.False(dto.IsInstalled);
        Assert.False(dto.IsPostfixRunning);
        Assert.False(dto.IsDovecotRunning);
        Assert.Empty(dto.Domains);
        Assert.Empty(dto.Accounts);
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
    public void MailDnsRecordsDto_DefaultValues()
    {
        var dto = new MailDnsRecordsDto();
        Assert.Equal(string.Empty, dto.Domain);
        Assert.Equal(string.Empty, dto.SpfRecord);
        Assert.Equal(string.Empty, dto.DkimRecord);
        Assert.Equal(string.Empty, dto.DkimSelector);
        Assert.Equal(string.Empty, dto.DkimPublicKey);
        Assert.Equal(string.Empty, dto.DmarcRecord);
        Assert.Equal(string.Empty, dto.MxRecord);
    }

    [Fact]
    public void DkimRotationRequest_Valid()
    {
        var req = new DkimRotationRequest { NewSelector = "sel2024" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void DkimRotationResultDto_DefaultValues()
    {
        var dto = new DkimRotationResultDto();
        Assert.Equal(string.Empty, dto.Domain);
        Assert.Equal(string.Empty, dto.OldSelector);
        Assert.Equal(string.Empty, dto.NewSelector);
        Assert.Equal(string.Empty, dto.DnsRecordName);
        Assert.Equal(string.Empty, dto.DnsRecordValue);
    }

    // --- Teamspeak DTOs ---

    [Fact]
    public void TeamspeakClientDto_DefaultValues()
    {
        var dto = new TeamspeakClientDto();
        Assert.Equal(string.Empty, dto.UniqueId);
        Assert.Equal(string.Empty, dto.Nickname);
        Assert.Equal(string.Empty, dto.Platform);
        Assert.Equal(string.Empty, dto.Version);
        Assert.Equal(0, dto.IdleTimeSeconds);
        Assert.Equal(0, dto.ConnectionTimeSeconds);
        Assert.False(dto.IsServerQuery);
    }

    [Fact]
    public void TeamspeakClientDto_CanSetAll()
    {
        var dto = new TeamspeakClientDto
        {
            ClientId = 5,
            UniqueId = "abc123",
            Nickname = "Player1",
            ChannelId = 2,
            Platform = "Windows",
            Version = "3.5.6",
            IdleTimeSeconds = 120,
            ConnectionTimeSeconds = 3600,
            IsServerQuery = true
        };
        Assert.Equal("Player1", dto.Nickname);
        Assert.True(dto.IsServerQuery);
    }

    [Fact]
    public void TeamspeakBanDto_DefaultValues()
    {
        var dto = new TeamspeakBanDto();
        Assert.Equal(string.Empty, dto.Ip);
        Assert.Equal(string.Empty, dto.UniqueId);
        Assert.Equal(string.Empty, dto.Nickname);
        Assert.Equal(string.Empty, dto.Reason);
        Assert.Equal(0, dto.Duration);
        Assert.Equal(0, dto.Created);
    }

    [Fact]
    public void TeamspeakBanDto_CanSetAll()
    {
        var dto = new TeamspeakBanDto
        {
            BanId = 1,
            Ip = "192.168.1.1",
            UniqueId = "uid",
            Nickname = "Spammer",
            Reason = "Spam",
            Duration = 86400,
            Created = 1700000000
        };
        Assert.Equal("Spammer", dto.Nickname);
        Assert.Equal(86400, dto.Duration);
    }

    [Fact]
    public void TeamspeakDataDto_DefaultValues()
    {
        var dto = new TeamspeakDataDto();
        Assert.False(dto.IsInstalled);
        Assert.False(dto.IsRunning);
        Assert.Equal(string.Empty, dto.Version);
        Assert.Equal(string.Empty, dto.Platform);
        Assert.Equal(string.Empty, dto.ServerName);
        Assert.Empty(dto.Channels);
        Assert.Empty(dto.Clients);
    }

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
    public void TeamspeakServerEditRequest_Defaults()
    {
        var req = new TeamspeakServerEditRequest();
        Assert.Null(req.ServerName);
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
    public void CronJobDto_DefaultValues()
    {
        var dto = new CronJobDto();
        Assert.Equal(string.Empty, dto.Id);
        Assert.Equal(string.Empty, dto.User);
        Assert.Equal(string.Empty, dto.Schedule);
        Assert.Equal(string.Empty, dto.Command);
        Assert.Equal(string.Empty, dto.Source);
        Assert.False(dto.IsSystem);
    }

    [Fact]
    public void CronJobDto_CanSetAll()
    {
        var dto = new CronJobDto
        {
            Id = "cron1",
            User = "root",
            Schedule = "0 * * * *",
            Command = "/bin/backup",
            Source = "/etc/cron.d/backup",
            IsSystem = true
        };
        Assert.Equal("root", dto.User);
        Assert.True(dto.IsSystem);
    }

    [Fact]
    public void CronDataDto_DefaultValues()
    {
        var dto = new CronDataDto();
        Assert.False(dto.IsInstalled);
        Assert.Empty(dto.Jobs);
    }

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
    public void RkhunterScanResultDto_DefaultValues()
    {
        var dto = new RkhunterScanResultDto();
        Assert.Equal(string.Empty, dto.Status);
        Assert.Equal(0, dto.WarningCount);
        Assert.Equal(string.Empty, dto.Summary);
    }

    [Fact]
    public void RkhunterScanResultDto_CanSetAll()
    {
        var dto = new RkhunterScanResultDto
        {
            Id = 1,
            ScanTime = DateTime.UtcNow,
            Status = "Clean",
            WarningCount = 2,
            Summary = "2 warnings found"
        };
        Assert.Equal("Clean", dto.Status);
        Assert.Equal(2, dto.WarningCount);
    }

    [Fact]
    public void RkhunterDataDto_DefaultValues()
    {
        var dto = new RkhunterDataDto();
        Assert.False(dto.IsInstalled);
        Assert.Equal(string.Empty, dto.Version);
        Assert.Equal(string.Empty, dto.DatabaseVersion);
        Assert.Equal(string.Empty, dto.LastScanStatus);
        Assert.Null(dto.ScanScheduleCron);
    }

    [Fact]
    public void RkhunterWarningDto_DefaultValues()
    {
        var dto = new RkhunterWarningDto();
        Assert.Equal(string.Empty, dto.Category);
        Assert.Equal(string.Empty, dto.Detail);
        Assert.Equal(string.Empty, dto.Severity);
        Assert.False(dto.IsArchived);
    }

    [Fact]
    public void RkhunterSetupRequest_Defaults()
    {
        var req = new RkhunterSetupRequest();
        Assert.Equal(string.Empty, req.MailOnWarning);
    }

    [Fact]
    public void RkhunterLogRequest_Defaults()
    {
        var req = new RkhunterLogRequest();
        Assert.Equal(200, req.Lines);
    }

    [Fact]
    public void RkhunterScheduleRequest_Defaults()
    {
        var req = new RkhunterScheduleRequest();
        Assert.Null(req.CronExpression);
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
    public void CreateProjectServerRequest_Defaults()
    {
        var req = new CreateProjectServerRequest { DisplayName = "s", Host = "h" };
        Assert.Null(req.ServerId);
        Assert.Null(req.Port);
        Assert.Null(req.Notes);
    }

    [Fact]
    public void UpdateProjectServerRequest_Valid()
    {
        var req = new UpdateProjectServerRequest { DisplayName = "Updated", Host = "h.com" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void UpdateProjectServerRequest_Defaults()
    {
        var req = new UpdateProjectServerRequest { DisplayName = "s", Host = "h" };
        Assert.Null(req.Port);
        Assert.Null(req.Notes);
    }

    [Fact]
    public void ProjectServerDto_DefaultValues()
    {
        var dto = new ProjectServerDto();
        Assert.Equal(string.Empty, dto.DisplayName);
        Assert.Equal(string.Empty, dto.Host);
        Assert.Null(dto.ServerId);
        Assert.Null(dto.Port);
        Assert.Null(dto.Notes);
        Assert.Null(dto.ServerName);
        Assert.Null(dto.ServerHostname);
        Assert.Null(dto.ServerStatus);
    }

    // --- Docker ---

    [Fact]
    public void DockerEnvVarDto_DefaultValues()
    {
        var dto = new DockerEnvVarDto();
        Assert.Equal(string.Empty, dto.Key);
        Assert.Equal(string.Empty, dto.Value);
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

    [Fact]
    public void RotateVaultSecretRequest_Defaults()
    {
        var req = new RotateVaultSecretRequest { Value = "v" };
        Assert.Null(req.ExpiresAt);
    }

    // --- Organization DTOs ---

    [Fact]
    public void OrganizationDetailDto_CanConstruct()
    {
        var dto = new OrganizationDetailDto(
            1, "Acme", "acme", "Acme Corp",
            DateTime.UtcNow, DateTime.UtcNow,
            [new OrganizationMemberDto(1, 10, "admin", "a@b.com", OrganizationRole.Owner, DateTime.UtcNow)],
            [new OrganizationProjectDto(1, "App", ProjectStatus.Active)]);
        Assert.Equal("Acme", dto.Name);
        Assert.Single(dto.Members);
        Assert.Single(dto.Projects);
    }

    [Fact]
    public void OrganizationMemberDto_CanConstruct()
    {
        var dto = new OrganizationMemberDto(1, 10, "user1", "u@b.com", OrganizationRole.Member, DateTime.UtcNow);
        Assert.Equal("user1", dto.Username);
        Assert.Equal(OrganizationRole.Member, dto.Role);
    }

    [Fact]
    public void MyOrganizationDto_CanConstruct()
    {
        var dto = new MyOrganizationDto(1, "Org", "org", OrganizationRole.Maintainer);
        Assert.Equal("Org", dto.Name);
        Assert.Equal("org", dto.Slug);
        Assert.Equal(OrganizationRole.Maintainer, dto.Role);
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

    [Fact]
    public void PipelineStatusReport_CanSetAll()
    {
        var dto = new PipelineStatusReport
        {
            PipelineRunId = 1,
            State = "success",
            Description = "All checks passed",
            TargetUrl = "https://ci.example.com",
            Context = "my-ci"
        };
        Assert.Equal("success", dto.State);
        Assert.Equal("my-ci", dto.Context);
    }

    [Fact]
    public void GitLightCommitDto_DefaultValues()
    {
        var dto = new GitLightCommitDto();
        Assert.Equal(string.Empty, dto.Sha);
        Assert.Equal(string.Empty, dto.ShortSha);
        Assert.Equal(string.Empty, dto.Message);
        Assert.Equal(string.Empty, dto.AuthorName);
        Assert.Equal(string.Empty, dto.AuthorEmail);
        Assert.Empty(dto.ParentShas);
    }

    [Fact]
    public void GitLightCommitDto_CanSetAll()
    {
        var dto = new GitLightCommitDto
        {
            Sha = "abc123",
            ShortSha = "abc",
            Message = "feat: add",
            AuthorName = "Dev",
            AuthorEmail = "dev@test.com",
            AuthorDate = DateTime.UtcNow,
            ParentShas = ["parent1"]
        };
        Assert.Equal("abc123", dto.Sha);
        Assert.Single(dto.ParentShas);
    }

    [Fact]
    public void GitLightTreeEntryDto_DefaultValues()
    {
        var dto = new GitLightTreeEntryDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(string.Empty, dto.Path);
        Assert.Null(dto.Size);
        Assert.Null(dto.Mode);
    }

    [Fact]
    public void SystemLogFileDto_CanConstruct()
    {
        var dto = new SystemLogFileDto("syslog", 1024, DateTime.UtcNow);
        Assert.Equal("syslog", dto.FileName);
        Assert.Equal(1024, dto.SizeBytes);
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
