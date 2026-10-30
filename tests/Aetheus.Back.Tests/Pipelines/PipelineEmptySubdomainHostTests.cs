// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.Pipelines;

namespace Aetheus.Back.Tests.Pipelines;

/// <summary>R-74: the application sub-domain may be empty. The host formula of the generic deployments adds the dot
/// only when there is a sub-domain, and the guard that refuses an empty library value accepts the empty one.</summary>
public sealed class PipelineEmptySubdomainHostTests
{
    // The formula the generic deployment templates write for the application host (the QA one ends with $(DOMAIN:-)).
    private const string AppHostFormula = "$(HOST_PREFIX:-)$(APP_SUBDOMAIN:suffix=.)$(DOMAIN)";

    private static Dictionary<string, string> Variables(params (string Key, string Value)[] values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

    [Theory]
    [InlineData("", "", "w7.example.org")]
    [InlineData("app", "", "app.w7.example.org")]
    [InlineData("", "qa.", "qa.w7.example.org")]
    [InlineData("app", "qa.", "qa.app.w7.example.org")]
    public void TheDotIsWrittenOnlyWhenTheSubdomainIsNotEmpty(string subdomain, string prefix, string expected)
    {
        var host = PipelineCommandBuilder.Substitute(
            AppHostFormula,
            Variables(("HOST_PREFIX", prefix), ("APP_SUBDOMAIN", subdomain), ("DOMAIN", "w7.example.org")));

        Assert.Equal(expected, host);
    }

    [Fact]
    public void AnUnsetSubdomain_IsAsEmptyAsAnEmptyOne_ExceptWhileTheNameMayStillComeLater()
    {
        var variables = Variables(("DOMAIN", "w7.example.org"));

        Assert.Equal("w7.example.org", PipelineCommandBuilder.Substitute(AppHostFormula, variables));
        Assert.Contains(
            "$(APP_SUBDOMAIN:suffix=.)",
            PipelineCommandBuilder.Substitute(AppHostFormula, variables, name => name != "APP_SUBDOMAIN"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptySubdomain_IsNotReportedAsAnEmptyValueToFill_WhenTheFormulaUsesTheSuffixForm()
    {
        var optional = new PipelineYamlDefinition { Variables = { ["APP_HOST"] = AppHostFormula } };
        var required = new PipelineYamlDefinition { Variables = { ["APP_HOST"] = "$(HOST_PREFIX:-)$(APP_SUBDOMAIN).$(DOMAIN)" } };
        IReadOnlySet<string> empty = new HashSet<string>(["APP_SUBDOMAIN"], StringComparer.OrdinalIgnoreCase);

        Assert.Empty(PipelineUnresolvedVariableGuard.FindConsumedEmptyLibraryKeys(optional, empty));
        Assert.Contains("APP_SUBDOMAIN", PipelineUnresolvedVariableGuard.FindConsumedEmptyLibraryKeys(required, empty).Keys);
    }

    [Fact]
    public void ASuffixReference_IsNotAnUnresolvedName()
    {
        Assert.False(PipelineUnresolvedVariableGuard.ReferencesAnUnresolvedName(
            "$(APP_SUBDOMAIN:suffix=.)", Variables(("DOMAIN", "w7.example.org"))));
        Assert.True(PipelineUnresolvedVariableGuard.ReferencesAnUnresolvedName(
            "$(APP_SUBDOMAIN)", Variables(("DOMAIN", "w7.example.org"))));
    }

    [Fact]
    public void TheWizardCreatesTheApplicationSubdomainEmpty_AndKeepsApiForTheApi()
    {
        var share = PipelineRequirementKeys.ForLibrary("w7.share", "w7");

        Assert.Equal(string.Empty, share.Single(key => key.Key == "APP_SUBDOMAIN").Value());
        Assert.Equal("api", share.Single(key => key.Key == "API_SUBDOMAIN").Value());
    }
}
