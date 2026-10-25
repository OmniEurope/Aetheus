// SPDX-License-Identifier: EUPL-1.2
using System.Xml.Linq;
using Aetheus.Back.Components.Pipelines;
using Aetheus.Back.Tests.Architecture;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>
/// R-14: the wizard tells what each library and vault it creates is for and which keys it holds. That text
/// is written by hand in the resource files, so it is held to the catalog the provisioning really writes
/// (<see cref="PipelineRequirementKeys"/>): a key added to the catalog without a word in the text, or a
/// key named in the text that is not created, fails here, in both languages.
/// </summary>
public sealed class PipelineRequirementPurposeTextTests
{
    private const string Slug = "atlas";

    public static TheoryData<string, string, bool, string> Resources() => new()
    {
        { "AppStrings.resx", "PipelineSetupPurposeLibraryShare", false, "share" },
        { "AppStrings.resx", "PipelineSetupPurposeLibraryQa", false, "qa" },
        { "AppStrings.resx", "PipelineSetupPurposeLibraryProd", false, "prod" },
        { "AppStrings.resx", "PipelineSetupPurposeVaultQa", true, "qa" },
        { "AppStrings.resx", "PipelineSetupPurposeVaultProd", true, "prod" },
        { "AppStrings.fr-FR.resx", "PipelineSetupPurposeLibraryShare", false, "share" },
        { "AppStrings.fr-FR.resx", "PipelineSetupPurposeLibraryQa", false, "qa" },
        { "AppStrings.fr-FR.resx", "PipelineSetupPurposeLibraryProd", false, "prod" },
        { "AppStrings.fr-FR.resx", "PipelineSetupPurposeVaultQa", true, "qa" },
        { "AppStrings.fr-FR.resx", "PipelineSetupPurposeVaultProd", true, "prod" }
    };

    [Theory]
    [MemberData(nameof(Resources))]
    public void EveryKeyTheSetupCreates_IsNamedInTheTextOfItsResource(string file, string key, bool isVault, string suffix)
    {
        var name = $"{Slug}.{suffix}";
        var created = (isVault ? PipelineRequirementKeys.ForVault(name, Slug) : PipelineRequirementKeys.ForLibrary(name, Slug))
            .Select(requirementKey => requirementKey.Key)
            .ToList();
        Assert.NotEmpty(created);

        var text = Value(file, key);

        Assert.All(created, createdKey => Assert.Contains(createdKey, text, StringComparison.Ordinal));
    }

    /// <summary>A library or vault the templates do not name gets no key: it is the only one whose text says
    /// that no key is required (<c>PipelineSetupPurposeNoKeys</c>).</summary>
    [Fact]
    public void TheSetupCreatesNoKeyForALibraryOrVaultTheTemplatesDoNotName()
    {
        Assert.Empty(PipelineRequirementKeys.ForLibrary($"{Slug}.other", Slug));
        Assert.Empty(PipelineRequirementKeys.ForVault($"{Slug}.other", Slug));
    }

    private static string Value(string file, string key) =>
        (string?)XDocument.Load(Path.Combine(RepositoryScan.Root, "src", "Aetheus.Front", "Resources", file))
            .Root!
            .Elements("data")
            .Single(element => (string?)element.Attribute("name") == key)
            .Element("value")
        ?? string.Empty;
}
