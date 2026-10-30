// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Tests;

public class DtoValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(object dto)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(dto, new ValidationContext(dto), results, validateAllProperties: true);
        return results;
    }

    // Real DataAnnotations behaviour (not a set-then-get tautology): the security-critical registration
    // request must reject a blank required field and an over-length one.
    [Fact]
    public void ServerRegistrationRequest_Valid_PassesValidation()
    {
        var dto = new ServerRegistrationRequest { RegistrationToken = "tok", Hostname = "host-1" };
        Assert.Empty(Validate(dto));
    }

    [Fact]
    public void ServerRegistrationRequest_MissingRequired_FailsValidation()
    {
        // RegistrationToken and Hostname are [Required]; both blank must produce validation errors.
        var dto = new ServerRegistrationRequest { RegistrationToken = "", Hostname = "" };
        var errors = Validate(dto);
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(ServerRegistrationRequest.RegistrationToken)));
        Assert.Contains(errors, e => e.MemberNames.Contains(nameof(ServerRegistrationRequest.Hostname)));
    }

    [Fact]
    public void ServerRegistrationRequest_OverLengthHostname_FailsValidation()
    {
        // Hostname is [StringLength(200)]; 201 chars must be rejected.
        var dto = new ServerRegistrationRequest { RegistrationToken = "tok", Hostname = new string('h', 201) };
        Assert.Contains(Validate(dto), e => e.MemberNames.Contains(nameof(ServerRegistrationRequest.Hostname)));
    }

    [Theory]
    [InlineData("https://git.example.com/team/repo.git", true)]
    [InlineData("http://git.example.com/team/repo.git", false)]
    [InlineData("https://user:token@git.example.com/team/repo.git", false)]
    [InlineData("ssh://git@git.example.com/team/repo.git", false)]
    [InlineData("not-a-url", false)]
    public void ProjectRepositoryUrl_RequiresHttps(string url, bool valid)
    {
        var errors = Validate(new CreateProjectRequest { Name = "Project", RepositoryUrl = url });

        Assert.Equal(valid, !errors.Any(e => e.MemberNames.Contains(nameof(CreateProjectRequest.RepositoryUrl))));
    }

    [Fact]
    public void ProjectRepositoryUrl_WithCredentials_ExplainsSecretStorageAlternatives()
    {
        var error = Assert.Single(Validate(new CreateProjectRequest
        {
            Name = "Project",
            RepositoryUrl = "https://user:token@git.example.com/team/repo.git"
        }), candidate => candidate.MemberNames.Contains(nameof(CreateProjectRequest.RepositoryUrl)));

        Assert.Contains("ServiceConnection", error.ErrorMessage, StringComparison.Ordinal);
        Assert.Contains("Vault", error.ErrorMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("elevenchars", false)]
    [InlineData("twelve-chars", true)]
    public void CompletePasswordSetupRequest_EnforcesSharedPasswordMinimum(string password, bool valid)
    {
        var errors = Validate(new CompletePasswordSetupRequest { Token = "token", NewPassword = password });

        Assert.Equal(valid, !errors.Any(e => e.MemberNames.Contains(nameof(CompletePasswordSetupRequest.NewPassword))));
    }

    [Theory]
    [InlineData("someone@example.com", true)]
    [InlineData("not-an-address", false)]
    [InlineData("", false)]
    public void RequestPasswordResetRequest_RequiresAnAddress(string email, bool valid)
    {
        var errors = Validate(new RequestPasswordResetRequest { Email = email });

        Assert.Equal(valid, errors.Count == 0);
    }

    [Theory]
    [InlineData("elevenchars", false)]
    [InlineData("twelve-chars", true)]
    public void ChangeUserPasswordRequest_EnforcesSharedPasswordMinimum(string password, bool valid)
    {
        var errors = Validate(new ChangeUserPasswordRequest { NewPassword = password });

        Assert.Equal(valid, !errors.Any(e => e.MemberNames.Contains(nameof(ChangeUserPasswordRequest.NewPassword))));
    }

    [Fact]
    public void ServerDto_DefaultValues_AreCorrect()
    {
        var dto = new ServerDto();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal(string.Empty, dto.Hostname);
        Assert.Equal(ServerStatus.Online, dto.Status);
        Assert.Empty(dto.Tags);
    }

    [Fact]
    public void PaginatedResult_EmptyResult_HasCorrectDefaults()
    {
        var result = new PaginatedResult<ServerDto>
        {
            Items = [],
            TotalCount = 0,
            Page = 1,
            PageSize = 20
        };
        Assert.Empty(result.Items);
        Assert.Equal(0, result.TotalCount);
    }

    [Fact]
    public void AgentPoolRequests_RejectMoreThanTwoHundredServers()
    {
        var serverIds = Enumerable.Range(1, 201).ToList();

        Assert.Contains(Validate(new CreateAgentPoolRequest { Name = "pool", ServerIds = serverIds }),
            error => error.MemberNames.Contains(nameof(CreateAgentPoolRequest.ServerIds)));
        Assert.Contains(Validate(new UpdateAgentPoolRequest { Name = "pool", ServerIds = serverIds }),
            error => error.MemberNames.Contains(nameof(UpdateAgentPoolRequest.ServerIds)));
    }

    [Fact]
    public void BackupPolicyRequests_RejectUnboundedFilePathCollections()
    {
        var tooManyPaths = Enumerable.Range(1, 201).Select(index => $"/data/{index}").ToList();
        var tooLongPath = new List<string> { new('/', 4097) };

        Assert.Contains(Validate(new CreateBackupPolicyRequest
        {
            Name = "backup",
            ProjectId = 1,
            ServerId = 1,
            ScheduleCron = "0 0 * * *",
            FilePaths = tooManyPaths
        }), error => error.MemberNames.Contains(nameof(CreateBackupPolicyRequest.FilePaths)));
        Assert.Contains(Validate(new UpdateBackupPolicyRequest
        {
            Name = "backup",
            ScheduleCron = "0 0 * * *",
            FilePaths = tooLongPath
        }), error => error.MemberNames.Contains(nameof(UpdateBackupPolicyRequest.FilePaths)));
    }

    [Fact]
    public void PipelineRunRequest_RejectsMoreThanSixtyFourParameters()
    {
        var request = new PipelineRunRequest
        {
            Parameters = Enumerable.Range(1, PipelineRunRequest.MaxParameterCount + 1)
                .ToDictionary(index => $"KEY_{index}", _ => "value")
        };

        Assert.Contains(Validate(request),
            error => error.MemberNames.Contains(nameof(PipelineRunRequest.Parameters)));
    }

    // aetheus-ci and aetheus-deploy-prod exceed 60 000 characters: the API must accept them as the
    // repository sync does, and still bound what it accepts.
    [Fact]
    public void PipelineDefinitionRequests_AcceptARepositorySizedDefinition_AndRejectAnOversizedOne()
    {
        var large = new string('a', 63_272);
        var oversized = new string('a', PipelineDefinitionLimits.MaxYamlLength + 1);

        Assert.Empty(Validate(new UpdatePipelineRequest { Name = "ci", YamlDefinition = large, ProjectId = 1 }));
        Assert.Empty(Validate(new ValidateYamlRequest { Yaml = large }));
        Assert.Contains(Validate(new UpdatePipelineRequest { Name = "ci", YamlDefinition = oversized, ProjectId = 1 }),
            error => error.MemberNames.Contains(nameof(UpdatePipelineRequest.YamlDefinition)));
        Assert.Contains(Validate(new ValidateYamlRequest { Yaml = oversized }),
            error => error.MemberNames.Contains(nameof(ValidateYamlRequest.Yaml)));
    }

    [Fact]
    public void BoundedSnapshotCollections_RejectOversizedPayloads()
    {
        var services = Enumerable.Range(0, 2049)
            .Select(index => new ServiceInfoDto { Name = $"service-{index}" }).ToList();
        var certificates = Enumerable.Range(0, 2049)
            .Select(index => new CertbotCertificateDto { Name = $"cert-{index}" }).ToList();
        var cronJobs = Enumerable.Range(0, 2049)
            .Select(index => new CronJobDto { Id = index.ToString() }).ToList();

        Assert.Contains(Validate(new ServerDetailDto { Services = services }),
            error => error.MemberNames.Contains(nameof(ServerDetailDto.Services)));
        Assert.Contains(Validate(new CertbotDataDto { Certificates = certificates }),
            error => error.MemberNames.Contains(nameof(CertbotDataDto.Certificates)));
        Assert.Contains(Validate(new CronDataDto { Jobs = cronJobs }),
            error => error.MemberNames.Contains(nameof(CronDataDto.Jobs)));
    }

    [Fact]
    public void ModuleLinkPageRequest_RejectsOversizedResourceSet()
    {
        var request = new ModuleLinkPageRequest
        {
            ResourceIdentifiers = Enumerable.Range(0, 2049).Select(index => $"resource-{index}").ToList()
        };

        Assert.Contains(Validate(request),
            error => error.MemberNames.Contains(nameof(ModuleLinkPageRequest.ResourceIdentifiers)));
    }

    [Fact]
    public void AgentHeartbeat_RejectsOversizedCapabilityAndDiagnosticItems()
    {
        var request = new ServerHeartbeatDto
        {
            AgentCapabilities = [new string('c', 201)],
            CapabilityDiagnostics = [new string('d', 1001)]
        };

        var errors = Validate(request);

        Assert.Contains(errors,
            error => error.MemberNames.Contains(nameof(ServerHeartbeatDto.AgentCapabilities)));
        Assert.Contains(errors,
            error => error.MemberNames.Contains(nameof(ServerHeartbeatDto.CapabilityDiagnostics)));
    }

    [Fact]
    public void ConfigureWebAnalytics_RejectsOversizedAllowedOrigin()
    {
        var request = new ConfigureAppWebAnalyticsRequest
        {
            SiteId = "site",
            AllowedOrigins = ["https://" + new string('a', 2049)]
        };

        Assert.Contains(Validate(request),
            error => error.MemberNames.Contains(nameof(ConfigureAppWebAnalyticsRequest.AllowedOrigins)));
    }

    [Fact]
    public void BackupResult_RejectsOversizedAgentControlledPathsAndMessages()
    {
        var request = new BackupExecuteResultDto
        {
            ArchivePath = new string('/', 4097),
            Message = new string('m', 2001)
        };

        var errors = Validate(request);

        Assert.Contains(errors,
            error => error.MemberNames.Contains(nameof(BackupExecuteResultDto.ArchivePath)));
        Assert.Contains(errors,
            error => error.MemberNames.Contains(nameof(BackupExecuteResultDto.Message)));
    }
}
