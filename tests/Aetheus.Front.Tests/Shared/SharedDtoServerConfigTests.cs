// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Front.Tests;

public class SharedDtoServerConfigTests
{
    // --- ServerConfig ---

    [Fact]
    public void ServerConfigYaml_DefaultValues()
    {
        var dto = new ServerConfigYaml();
        Assert.NotNull(dto.Server);
        Assert.Null(dto.Docker);
        Assert.Null(dto.Services);
    }

    [Fact]
    public void ServerConfigServerSection_DefaultValues()
    {
        var dto = new ServerConfigServerSection();
        Assert.Equal(string.Empty, dto.Name);
        Assert.Equal("Normal", dto.Type);
        Assert.Empty(dto.Tags);
    }

    [Fact]
    public void ServerConfigImportRequest_Valid()
    {
        var req = new ServerConfigImportRequest { Yaml = "server:\n  name: web" };
        Assert.Empty(ValidateModel(req));
    }

    [Fact]
    public void ServerConfigImportRequest_EmptyYaml_Fails()
    {
        var req = new ServerConfigImportRequest { Yaml = "" };
        Assert.NotEmpty(ValidateModel(req));
    }

    // --- MaxItemStringLengthAttribute ---

    private class TestMaxItemModel
    {
        [MaxItemStringLength(5)]
        public List<string>? Tags { get; set; }
    }

    [Fact]
    public void MaxItemStringLength_NullList_Passes()
    {
        var model = new TestMaxItemModel { Tags = null };
        Assert.Empty(ValidateModel(model));
    }

    [Fact]
    public void MaxItemStringLength_EmptyList_Passes()
    {
        var model = new TestMaxItemModel { Tags = [] };
        Assert.Empty(ValidateModel(model));
    }

    [Fact]
    public void MaxItemStringLength_ValidItems_Passes()
    {
        var model = new TestMaxItemModel { Tags = ["abc", "de"] };
        Assert.Empty(ValidateModel(model));
    }

    [Fact]
    public void MaxItemStringLength_TooLongItem_Fails()
    {
        var model = new TestMaxItemModel { Tags = ["short", "toolong"] };
        var results = ValidateModel(model);
        Assert.NotEmpty(results);
        Assert.Contains("index 1", results[0].ErrorMessage!);
    }

    [Fact]
    public void MaxItemStringLength_FirstItemTooLong_Fails()
    {
        var model = new TestMaxItemModel { Tags = ["123456"] };
        var results = ValidateModel(model);
        Assert.NotEmpty(results);
        Assert.Contains("index 0", results[0].ErrorMessage!);
    }

    // --- PaginatedResult ---

    private static List<ValidationResult> ValidateModel(object model)
    {
        var ctx = new ValidationContext(model);
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(model, ctx, results, true);
        return results;
    }
}
