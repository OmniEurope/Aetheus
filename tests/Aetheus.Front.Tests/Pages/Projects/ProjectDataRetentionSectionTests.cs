// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Text.Json;
using Aetheus.Front.Components.Projects.ProjectDetailSections;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Projects;

/// <summary>
/// PLAN-013 lot 4: the « Conservation des données » section shows the project's profile, the profile's
/// value in each empty field, and saves the profile with only the values the user typed.
/// </summary>
public sealed class ProjectDataRetentionSectionTests : BunitContext
{
    private readonly BunitTestHelper.TestHandler _handler;

    public ProjectDataRetentionSectionTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
    }

    private static ProjectDataRetentionDto Retention(DataRetentionProfile profile, int? exportMinutes = null) => new()
    {
        ProjectId = 7,
        Profile = profile,
        Overrides = new DataRetentionOverrides { TelemetryExportIntervalMinutes = exportMinutes },
        Effective = DataRetentionProfiles.Resolve(profile, new DataRetentionOverrides { TelemetryExportIntervalMinutes = exportMinutes })
    };

    private IRenderedComponent<ProjectDataRetentionSection> RenderSection() =>
        Render<ProjectDataRetentionSection>(parameters => parameters.Add(section => section.ProjectId, 7));

    [Fact]
    public void EmptyFields_ShowTheProfilesValue_AndAnOverrideShowsItself()
    {
        _handler.SetJsonResponse("api/projects/7/data-retention", Retention(DataRetentionProfile.Light, exportMinutes: 1));

        var cut = RenderSection();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#project-data-retention-export")));
        Assert.Equal("1", cut.Find("#project-data-retention-export").GetAttribute("value"));
        // An empty field carries the profile's value as its placeholder (the test localizer returns the key).
        Assert.Equal(string.Empty, cut.Find("#project-data-retention-telemetry").GetAttribute("value") ?? string.Empty);
        Assert.Equal("DataRetentionProfileValue", cut.Find("#project-data-retention-telemetry").GetAttribute("placeholder"));
        Assert.Contains("Enum_DataRetentionProfile_Light", cut.Markup);
    }

    [Fact]
    public void Saving_SendsTheProfileAndOnlyTheTypedValues()
    {
        _handler.SetJsonResponse("api/projects/7/data-retention", Retention(DataRetentionProfile.Standard));
        _handler.SetJsonResponse(HttpMethod.Put, "api/projects/7/data-retention", Retention(DataRetentionProfile.Standard, exportMinutes: 15));
        var cut = RenderSection();
        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#project-data-retention-export")));

        cut.Find("#project-data-retention-export").Change("15");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(_handler.RequestDetails, request => request.Method == "PUT"));
        var body = _handler.RequestDetails.Last(request => request.Method == "PUT").Body!;
        var sent = JsonSerializer.Deserialize<UpdateProjectDataRetentionRequest>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(DataRetentionProfile.Standard, sent.Profile);
        Assert.Equal(15, sent.Overrides.TelemetryExportIntervalMinutes);
        Assert.Null(sent.Overrides.AppLogRetentionDays);
    }

    [Fact]
    public void TheSavedSendInterval_IsStatedNextToItsField_WithThePackageThatAppliesIt()
    {
        Services.AddSingleton<Microsoft.Extensions.Localization.IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(
            Aetheus.Front.Tests.TestDoubles.ResxLocalizer.French());
        _handler.SetJsonResponse("api/projects/7/data-retention", Retention(DataRetentionProfile.Standard));

        var cut = RenderSection();

        cut.WaitForAssertion(() => Assert.NotNull(cut.Find("#project-data-retention-export-field-description")));
        Assert.Equal("Envoi toutes les 5 min (profil Standard) : un point toutes les 5 minutes",
            cut.Find("#project-data-retention-export-field-description").TextContent.Trim());
        Assert.Contains("Aetheus.Telemetry 1.1.0", cut.Find("#project-data-retention-export-field-help-content").TextContent);
    }

    [Fact]
    public void AFailedLoad_SaysSo()
    {
        _handler.SetResponse("api/projects/7/data-retention", HttpStatusCode.InternalServerError);

        var cut = RenderSection();

        cut.WaitForAssertion(() => Assert.Contains("DataRetentionLoadFailed", cut.Markup));
    }
}
