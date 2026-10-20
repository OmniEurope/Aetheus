// SPDX-License-Identifier: EUPL-1.2
using System.Text.Json;
using Aetheus.Front.Components.AiTasks;
using Aetheus.Front.Components.Pipelines;
using Bunit;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>
/// The AI runner profile priority (lower is preferred) in its dialog, and the pipeline setting that names
/// the profile its AI steps fall back on.
/// </summary>
public sealed class AiProfilePriorityTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;

    public AiProfilePriorityTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>
        {
            new() { Id = 3, OrganizationId = 1, Name = "later", Priority = 50 },
            new() { Id = 4, OrganizationId = 1, Name = "preferred", Priority = 10 },
            new() { Id = 5, OrganizationId = 2, Name = "foreign", Priority = 1 },
            new() { Id = 6, OrganizationId = 1, Name = "broken", Priority = 30, NeedsReconfiguration = true }
        });
    }

    private T LastBody<T>(string method, string urlContains) =>
        JsonSerializer.Deserialize<T>(
            _handler.RequestDetails
                .Last(request => request.Method == method && request.Url.Contains(urlContains, StringComparison.Ordinal))
                .Body!, Json)!;

    // ---------- the dialog ----------

    [Fact]
    public void Dialog_ShowsThePriorityInAFormFieldWithAHelp_AndNoTextUnderIt()
    {
        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        var field = cut.Find("#ai-profile-priority-field");

        Assert.Contains("AiProfilePriority", field.TextContent, StringComparison.Ordinal);
        Assert.Contains("AiProfilePriorityHint", cut.Markup, StringComparison.Ordinal);
        Assert.Equal("100", cut.Find("#ai-profile-priority-field input").GetAttribute("value"));
    }

    [Fact]
    public void Dialog_CreatesWithTheDefaultPriority()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "claude" });
        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        cut.Find("#ai-profile-api-key").Input("sk-typed");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Method == "POST" && request.Url.Contains("api/ai/profiles", StringComparison.Ordinal)));
        Assert.Equal(AiProfilePriority.Default, LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles").Priority);
    }

    [Fact]
    public void Dialog_InEditMode_LoadsTheStoredPriority_AndSendsTheEditedOne()
    {
        _handler.SetJsonResponse("api/ai/profiles/5", new AiRunnerProfileDto
        {
            Id = 5,
            Name = "claude",
            Provider = AiProvider.Anthropic,
            Model = "claude-sonnet-5-5",
            HasApiKey = true,
            Priority = 30,
            TimeoutSeconds = 600,
            MaxOutputBytes = 200_000
        });
        _handler.SetJsonResponse(HttpMethod.Put, "api/ai/profiles/5", new AiRunnerProfileDto { Id = 5, Name = "claude" });

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 5));
        cut.WaitForAssertion(() => Assert.Contains("AiApiKeyKeepHint", cut.Markup, StringComparison.Ordinal));
        var input = cut.Find("#ai-profile-priority-field input");
        Assert.Equal("30", input.GetAttribute("value"));

        input.Change("15");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.Contains(_handler.Requests, request =>
            request.Method == "PUT" && request.Url.Contains("api/ai/profiles/5", StringComparison.Ordinal)));
        Assert.Equal(15, LastBody<UpdateAiRunnerProfileRequest>("PUT", "api/ai/profiles/5").Priority);
    }

    // ---------- the pipeline setting ----------

    [Fact]
    public void PipelineField_OffersAutomaticThenTheOrganizationsProfiles_MostPreferredFirst()
    {
        var cut = Render<PipelineAiProfileField>(parameters => parameters.Add(field => field.OrganizationId, 1));

        cut.WaitForAssertion(() => Assert.Equal(3, cut.Instance.Options.Count));
        Assert.Equal(["preferred", "broken (AiToReconfigure)", "later"], cut.Instance.Options.Select(option => option.Text));
        Assert.DoesNotContain(cut.Instance.Options, option => option.Text == "foreign");
        Assert.Contains("PipelineAiProfileHint", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("PipelineAiProfileAutomatic", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void PipelineField_WithoutAKnownOrganization_OffersEveryVisibleProfile()
    {
        var cut = Render<PipelineAiProfileField>();

        cut.WaitForAssertion(() => Assert.Equal(4, cut.Instance.Options.Count));
    }

    [Fact]
    public void PipelineField_PickingAProfile_RaisesValueChanged_AndEmptyMeansAutomatic()
    {
        int? picked = -1;
        var cut = Render<PipelineAiProfileField>(parameters => parameters
            .Add(field => field.OrganizationId, 1)
            .Add(field => field.Value, 4)
            .Add(field => field.ValueChanged, (int? value) => picked = value));
        cut.WaitForAssertion(() => Assert.Equal(3, cut.Instance.Options.Count));

        var select = cut.Find("#pipeline-ai-profile-field select");
        Assert.Equal("preferred", select.QuerySelectorAll("option").First(option => option.HasAttribute("selected")).TextContent);

        select.Change("2"); // the option at index 2 is "later" (id 3)
        Assert.Equal(3, picked);

        select.Change(string.Empty);
        Assert.Null(picked);
    }

    [Fact]
    public void PipelineField_WhenTheProfileListFails_StaysUsableWithAutomaticOnly()
    {
        _handler.SetResponse("api/ai/profile-options", System.Net.HttpStatusCode.InternalServerError);

        var cut = Render<PipelineAiProfileField>();

        cut.WaitForAssertion(() => cut.Find("#pipeline-ai-profile-field"));
        Assert.Empty(cut.Instance.Options);
    }
}
