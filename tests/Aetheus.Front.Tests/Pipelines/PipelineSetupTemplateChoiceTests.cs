// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;

namespace Aetheus.Front.Tests.Pipelines;

/// <summary>R-78: the wizard pins the version of the template the backend resolver will run.</summary>
public sealed class PipelineSetupTemplateChoiceTests
{
    private static readonly PipelineTemplateSummaryDto OtherOrganization = new() { Id = 1, Name = "application-ci", OrganizationId = 30 };
    private static readonly PipelineTemplateSummaryDto Shared = new() { Id = 2, Name = "application-ci", OrganizationId = 1, IsShared = true };
    private static readonly PipelineTemplateSummaryDto Own = new() { Id = 3, Name = "Application-CI", OrganizationId = 20 };

    [Fact]
    public void TheProjectsOrganizationsOwnTemplate_Wins()
    {
        Assert.Same(Own, PipelineSetupTemplateChoice.For([OtherOrganization, Shared, Own], "application-ci", 20));
    }

    [Fact]
    public void WithoutAnOwnTemplate_TheSharedOne_WinsOverAnotherOrganizationsTemplate()
    {
        Assert.Same(Shared, PipelineSetupTemplateChoice.For([OtherOrganization, Shared], "application-ci", 20));
    }

    [Fact]
    public void AnUnknownName_GivesNoTemplate()
    {
        Assert.Null(PipelineSetupTemplateChoice.For([Shared, Own], "application-qa", 20));
    }
}
