// SPDX-License-Identifier: EUPL-1.2
namespace Aetheus.Back.Tests.Architecture;

/// <summary>
/// Architectural guard: entities with the at-most-one-owner pattern (ProjectId / EnvironmentId /
/// ProjectServerId) must have the <c>[AtMostOneOwner]</c> validation attribute on their
/// corresponding Create and Update request DTOs. This prevents regressions where a new DTO
/// is introduced without the ownership constraint.
/// </summary>
public class OwnershipValidationAuditTests
{
    private static readonly string[] OwnedEntityNames = ["VariableLibrary", "Vault", "Pipeline"];

    [Fact]
    public void AllOwnedEntityDtos_HaveAtMostOneOwnerAttribute()
    {
        var sharedAssembly = typeof(Aetheus.Shared.Components.Shared.PaginatedResult<>).Assembly;
        var attributeType = typeof(Aetheus.Shared.Components.Shared.AtMostOneOwnerAttribute);
        var offenders = new List<string>();

        foreach (var entityName in OwnedEntityNames)
        {
            var createDtoName = $"Create{entityName}Request";
            var updateDtoName = $"Update{entityName}Request";

            var createType = sharedAssembly.GetTypes().FirstOrDefault(t => t.Name == createDtoName);
            var updateType = sharedAssembly.GetTypes().FirstOrDefault(t => t.Name == updateDtoName);

            if (createType is null)
            {
                offenders.Add($"{createDtoName} - DTO not found");
                continue;
            }
            if (updateType is null)
            {
                offenders.Add($"{updateDtoName} - DTO not found");
                continue;
            }

            if (!Attribute.IsDefined(createType, attributeType))
                offenders.Add($"{createDtoName} - missing [AtMostOneOwner]");
            if (!Attribute.IsDefined(updateType, attributeType))
                offenders.Add($"{updateDtoName} - missing [AtMostOneOwner]");
        }

        Assert.True(offenders.Count == 0,
            "These DTOs for owned entities are missing the [AtMostOneOwner] validation attribute. " +
            "Add it to enforce the at-most-one-owner constraint (ProjectId | EnvironmentId | ProjectServerId):" +
            System.Environment.NewLine +
            string.Join(System.Environment.NewLine, offenders.Select(o => $"  - {o}")));
    }

    [Fact]
    public void AtMostOneOwnerAttribute_AcceptsZeroOwners()
    {
        var dto = new Aetheus.Shared.Components.VariableLibraries.CreateVariableLibraryRequest
        {
            Name = "test",
            Description = "test"
        };

        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var ctx = new System.ComponentModel.DataAnnotations.ValidationContext(dto);
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(dto, ctx, results, true);

        Assert.True(isValid, "Should accept when no owner is set (global entity)");
    }

    [Fact]
    public void AtMostOneOwnerAttribute_RejectsMultipleOwners()
    {
        var dto = new Aetheus.Shared.Components.VariableLibraries.CreateVariableLibraryRequest
        {
            Name = "test",
            Description = "test",
            ProjectId = 1,
            EnvironmentId = 2
        };

        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var ctx = new System.ComponentModel.DataAnnotations.ValidationContext(dto);
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(dto, ctx, results, true);

        Assert.False(isValid, "Should reject when multiple owners are set");
    }

    [Fact]
    public void AtMostOneOwnerAttribute_AcceptsSingleOwner()
    {
        var dto = new Aetheus.Shared.Components.VariableLibraries.CreateVariableLibraryRequest
        {
            Name = "test",
            Description = "test",
            ProjectId = 1
        };

        var results = new List<System.ComponentModel.DataAnnotations.ValidationResult>();
        var ctx = new System.ComponentModel.DataAnnotations.ValidationContext(dto);
        var isValid = System.ComponentModel.DataAnnotations.Validator.TryValidateObject(dto, ctx, results, true);

        Assert.True(isValid, "Should accept when exactly one owner is set");
    }
}
