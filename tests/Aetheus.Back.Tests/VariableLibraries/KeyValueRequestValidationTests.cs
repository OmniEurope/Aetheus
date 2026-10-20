// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Back.Tests.VariableLibraries;

/// <summary>Library entries may intentionally have empty values, while vault secrets may not. Both
/// request types share key validation.</summary>
public sealed class KeyValueRequestValidationTests
{
    private static bool IsValid(object request, out List<ValidationResult> results)
    {
        results = [];
        return Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
    }

    [Fact]
    public void LibraryEntry_WithAnEmptyValue_IsValid()
    {
        Assert.True(IsValid(new CreateVariableEntryRequest { Key = "HOST_PREFIX", Value = "" }, out var results),
            string.Join("; ", results.Select(result => result.ErrorMessage)));
        Assert.True(IsValid(new UpdateVariableEntryRequest { Key = "HOST_PREFIX", Value = "" }, out _));
    }

    [Fact]
    public void LibraryEntry_StillRefusesAValueOverTheLimit()
    {
        var request = new CreateVariableEntryRequest { Key = "K", Value = new string('x', KeyValueRequest.MaxValueLength + 1) };

        Assert.False(IsValid(request, out var results));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(KeyValueRequest.Value)));
    }

    [Fact]
    public void LibraryEntry_StillRequiresAKey()
    {
        Assert.False(IsValid(new CreateVariableEntryRequest { Key = "", Value = "x" }, out var results));
        Assert.Contains(results, result => result.MemberNames.Contains(nameof(KeyValueRequest.Key)));
    }

    [Fact]
    public void VaultSecret_WithAnEmptyValue_IsRefused()
    {
        Assert.False(IsValid(new CreateVaultSecretRequest { Key = "ADMIN_PASSWORD", Value = "" }, out var created));
        Assert.Contains(created, result => result.MemberNames.Contains(nameof(KeyValueRequest.Value)));
        Assert.False(IsValid(new UpdateVaultSecretRequest { Key = "ADMIN_PASSWORD", Value = "" }, out _));
    }
}
