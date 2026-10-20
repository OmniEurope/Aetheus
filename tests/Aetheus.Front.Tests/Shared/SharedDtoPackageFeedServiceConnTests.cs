// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests;

public class SharedDtoPackageFeedServiceConnTests
{
    // --- PackageFeed ---

    [Fact]
    public void CreatePackageFeedRequest_Valid()
    {
        var req = new CreatePackageFeedRequest { Name = "Feed", UpstreamUrl = "https://example.com" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreatePackageFeedRequest_EmptyName_Fails()
    {
        var req = new CreatePackageFeedRequest { Name = "", UpstreamUrl = "https://example.com" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void UpdatePackageFeedRequest_Valid()
    {
        var req = new UpdatePackageFeedRequest { Name = "Updated", UpstreamUrl = "https://example.com" };
        Assert.Empty(ValidateModel(req));
    }

    // --- ServiceConnection ---

    [Fact]
    public void ServiceConnectionDetailDto_DefaultValues()
    {
        var dto = new ServiceConnectionDetailDto();
        Assert.Equal("{}", dto.ConfigurationJson);
    }

    [Fact]
    public void CreateServiceConnectionRequest_Valid()
    {
        var req = new CreateServiceConnectionRequest { Name = "Conn", ConfigurationJson = "{}" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void CreateServiceConnectionRequest_EmptyName_Fails()
    {
        var req = new CreateServiceConnectionRequest { Name = "", ConfigurationJson = "{}" };
        Assert.NotEmpty(ValidateModel(req));
    }

    [Fact]
    public void UpdateServiceConnectionRequest_Valid()
    {
        var req = new UpdateServiceConnectionRequest { Name = "Updated", ConfigurationJson = "{}" };
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
