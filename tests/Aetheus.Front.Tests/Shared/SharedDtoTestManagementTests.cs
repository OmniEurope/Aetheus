// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests;

public class SharedDtoTestManagementTests
{
    [Fact]
    public void CreateTestSuiteRequest_Validation_Valid()
    {
        var req = new CreateTestSuiteRequest { ProjectId = 1, Name = "My Suite" };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void CreateTestSuiteRequest_Validation_EmptyName_Fails()
    {
        var req = new CreateTestSuiteRequest { ProjectId = 1, Name = "" };
        var results = ValidateModel(req);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void CreateTestSuiteRequest_DefaultType_IsAutomated()
    {
        var req = new CreateTestSuiteRequest { Name = "t" };
        Assert.Equal(TestSuiteType.Automated, req.Type);
    }

    [Fact]
    public void UpdateTestSuiteRequest_Validation_Valid()
    {
        var req = new UpdateTestSuiteRequest { Name = "Updated Suite" };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void IngestTestResultsRequest_Validation_Valid()
    {
        var req = new IngestTestResultsRequest
        {
            ProjectId = 1,
            SuiteName = "Suite",
            XmlContent = "<xml/>",
            Format = "junit"
        };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void IngestTestResultsRequest_DefaultFormat_IsJunit()
    {
        var req = new IngestTestResultsRequest();
        Assert.Equal("junit", req.Format);
    }

    [Fact]
    public void TriggerReleaseBuildRequest_Validation_Valid()
    {
        var req = new TriggerReleaseBuildRequest { PipelineId = 1 };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void CreateVaultRequest_Validation_Valid()
    {
        var req = new CreateVaultRequest { Name = "Vault" };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void CreateVaultRequest_Validation_EmptyName_Fails()
    {
        var req = new CreateVaultRequest { Name = "" };
        var results = ValidateModel(req);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void UpdateVaultRequest_Validation_Valid()
    {
        var req = new UpdateVaultRequest { Name = "Updated" };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void CreateVaultSecretRequest_Validation_Valid()
    {
        var req = new CreateVaultSecretRequest { Key = "API_KEY", Value = "secret" };
        var results = ValidateModel(req);
        Assert.Empty(results);
    }

    [Fact]
    public void CreateVaultSecretRequest_Validation_EmptyKey_Fails()
    {
        var req = new CreateVaultSecretRequest { Key = "", Value = "val" };
        var results = ValidateModel(req);
        Assert.NotEmpty(results);
    }

    [Fact]
    public void CreateVaultSecretRequest_Validation_AcceptsSigningCertificatePayload()
    {
        var req = new CreateVaultSecretRequest
        {
            Key = "AETHEUS_NUGET_SIGNING_PFX_BASE64",
            Value = new string('a', 4_756)
        };

        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateVaultSecretRequest_Validation_RejectsOversizedValue()
    {
        var req = new CreateVaultSecretRequest
        {
            Key = "SECRET",
            Value = new string('a', KeyValueRequest.MaxValueLength + 1)
        };

        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void RotateVaultSecretRequest_Validation_Valid()
    {
        var req = new RotateVaultSecretRequest { Value = "new-secret" };
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
