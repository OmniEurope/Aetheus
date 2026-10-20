// SPDX-License-Identifier: EUPL-1.2
using System.Net;
using System.Net.Http;
using System.Text.Json;
using Aetheus.Front.Components.AiTasks;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;

namespace Aetheus.Front.Tests.Pages.Ai;

/// <summary>
/// The two AI edit dialogs, end to end through their real forms: what they load, what they send,
/// and how they behave when the backend refuses. Both were entirely unexercised, so the owner
/// switch, the comma/newline parsing and the failure paths had no protection at all.
/// </summary>
public sealed class AiDialogTests : BunitContext
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly BunitTestHelper.TestHandler _handler;

    public AiDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, isAdmin: true);
        _handler.SetJsonResponse("api/ai/profile-options", new List<AiRunnerProfileDto>
        {
            new() { Id = 3, Name = "claude" },
            new() { Id = 4, Name = "codex" }
        });
        _handler.SetJsonResponse("api/projects", new PaginatedResult<ProjectDto>
        {
            Items = [new ProjectDto { Id = 10, Name = "aetheus" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
        _handler.SetJsonResponse("api/servers", new PaginatedResult<ServerDto>
        {
            Items = [new ServerDto { Id = 20, Name = "runner" }],
            TotalCount = 1,
            Page = 1,
            PageSize = 100
        });
    }

    private T LastBody<T>(string method, string urlContains)
    {
        var body = _handler.RequestDetails
            .Last(request => request.Method == method
                && request.Url.Contains(urlContains, StringComparison.Ordinal))
            .Body;
        return JsonSerializer.Deserialize<T>(body!, Json)!;
    }

    /// <summary>
    /// Neither dialog gives its inputs a <c>name</c>, so a field is addressed the way a user
    /// sees it: by the label its <c>OmniFormField</c> renders, without the required mark (*) a
    /// mandatory field adds to it. Both controls bind on <c>change</c>.
    /// </summary>
    private static IElement Field<TComponent>(
        IRenderedComponent<TComponent> cut, string label, string tag = "input")
        where TComponent : IComponent =>
        cut.FindAll(".rz-form-field, .omni-form-field")
            .First(field => field.QuerySelector("label")?.TextContent.Trim().TrimEnd('*').Trim() == label)
            .QuerySelector(tag)!;

    /// <summary>Types a model in the model field the way a user does (R3-7: OE autocomplete taking free
    /// text): the text is the value once the field is left.</summary>
    private static void PickModel(IRenderedComponent<AiRunnerProfileDialog> cut, string model)
    {
        var input = cut.Find("#ai-profile-model");
        input.Input(model);
        input.Blur();
    }

    /// <summary>Opens the whole suggestion list with the chevron and returns the suggestions' texts.</summary>
    private static IReadOnlyList<string> OpenSuggestions(IRenderedComponent<AiRunnerProfileDialog> cut)
    {
        cut.Find("[data-omni-autocomplete-toggle]").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#ai-profile-model-field .omni-autocomplete__results li")));
        return [.. cut.FindAll("#ai-profile-model-field .omni-autocomplete__results li").Select(option => option.TextContent.Trim())];
    }

    private bool Sent(string method, string urlContains) =>
        _handler.Requests.Any(request => request.Method == method
            && request.Url.Contains(urlContains, StringComparison.Ordinal));

    // ---------- AiTaskDialog ----------

    [Fact]
    public void R3_6_TaskDialog_StarsTheFieldsItsModelRequires_WithoutAHandWrittenMark()
    {
        // OE 1.8.0: OmniFormField.Required left null follows the [Required] of the bound property.
        var cut = Render<AiTaskDialog>();

        cut.WaitForAssertion(() => Assert.Contains("Create", cut.Markup));
        var required = cut.FindAll("label.omni-label--required").Select(label => label.TextContent).ToList();
        Assert.Contains(required, text => text.Contains("Name", StringComparison.Ordinal));
        Assert.Contains(required, text => text.Contains("PromptTemplate", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("ai-context-field", "AiContextPathsHint")]
    [InlineData("oe-pages-ai-aitaskdialog-10-field", "AiGitRangeHint")]
    [InlineData("oe-pages-ai-aitaskdialog-7-field", "AiScheduleHint")]
    [InlineData("oe-pages-ai-aitaskdialog-8-field", "AiEventTypesHint")]
    public void R3_13_TaskDialog_PutsEachFieldDescriptionBehindTheQuestionMark(string fieldId, string hint)
    {
        var cut = Render<AiTaskDialog>(parameters => parameters.Add(dialog => dialog.FixedProjectId, 10));

        cut.WaitForAssertion(() => Assert.Contains("Create", cut.Markup));
        Assert.Contains(hint, cut.Find($"#{fieldId}-help-content").TextContent, StringComparison.Ordinal);
        Assert.Single(cut.FindAll("*"), element => element.Children.Length == 0
            && element.TextContent.Contains(hint, StringComparison.Ordinal));
    }

    [Fact]
    public void TaskDialog_InCreateMode_PreselectsTheFirstProfileAndOffersBothOwnerKinds()
    {
        var cut = Render<AiTaskDialog>();

        cut.WaitForAssertion(() => Assert.Contains("Create", cut.Markup));
        Assert.Contains("OwnerType", cut.Markup);
        Assert.Contains("Server", cut.Markup);
    }

    [Fact]
    public void TaskDialog_CreatesAServerOwnedTaskWithTheParsedEventTypes()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/tasks", new AiTaskDefinitionDto { Id = 1, Name = "review" });

        var cut = Render<AiTaskDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        Field(cut, "Name").Input("review");
        Field(cut, "PromptTemplate", "textarea").Input("Review {diff}");
        Field(cut, "EventTypes").Input(" pipeline.completed , release.created ,, ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/tasks")));
        var request = LastBody<CreateAiTaskDefinitionRequest>("POST", "api/ai/tasks");
        Assert.Equal("review", request.Name);
        Assert.Equal("Review {diff}", request.PromptTemplate);
        Assert.Equal(3, request.ProfileId);
        Assert.Null(request.ProjectId);
        Assert.Equal(["pipeline.completed", "release.created"], request.EventTypes);
    }

    [Fact]
    public void TaskDialog_TurnsABlankScheduleIntoNoScheduleAtAll()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/tasks", new AiTaskDefinitionDto { Id = 1, Name = "review" });

        var cut = Render<AiTaskDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        Field(cut, "Name").Input("review");
        Field(cut, "PromptTemplate", "textarea").Input("Review");
        Field(cut, "Schedule").Input("   ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/tasks")));
        Assert.Null(LastBody<CreateAiTaskDefinitionRequest>("POST", "api/ai/tasks").Schedule);
    }

    [Fact]
    public void TaskDialog_WithAFixedProjectOwnsTheTaskByThatProjectAndHidesTheOwnerSwitch()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/tasks", new AiTaskDefinitionDto { Id = 1, Name = "review" });

        var cut = Render<AiTaskDialog>(
            parameters => parameters.Add(dialog => dialog.FixedProjectId, 10));
        cut.WaitForAssertion(() => cut.Find("form"));

        Assert.DoesNotContain("OwnerType", cut.Markup);
        Field(cut, "Name").Input("review");
        Field(cut, "PromptTemplate", "textarea").Input("Review");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/tasks")));
        var request = LastBody<CreateAiTaskDefinitionRequest>("POST", "api/ai/tasks");
        Assert.Equal(10, request.ProjectId);
        Assert.Null(request.ServerId);
    }

    [Fact]
    public void TaskDialog_ProjectTaskSendsItsPickedContextPathsAndItsGitRange()
    {
        _handler.SetPaginatedJsonResponse("api/git/repos?", new List<GitLightRepoDto> { new() { Id = 7, ProjectId = 10, Name = "aetheus", DefaultBranch = "main" } });
        _handler.SetPaginatedJsonResponse("api/git/repos/7/tree", new List<GitLightTreeEntryDto>
        {
            new() { Name = "AGENTS.md", Path = "AGENTS.md", Type = GitTreeEntryType.Blob },
            new() { Name = "README.md", Path = "README.md", Type = GitTreeEntryType.Blob }
        });
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/tasks", new AiTaskDefinitionDto { Id = 1, Name = "quick check" });

        var cut = Render<AiTaskDialog>(
            parameters => parameters.Add(dialog => dialog.FixedProjectId, 10));
        cut.WaitForAssertion(() => cut.Find("form"));

        Field(cut, "Name").Input("quick check");
        Field(cut, "PromptTemplate", "textarea").Input("Check the repository");
        cut.Find("#ai-context-add").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".omni-tree__select")));
        foreach (var name in new[] { "AGENTS.md", "README.md" })
            cut.FindAll(".omni-tree__select").First(button => button.TextContent.Trim() == name).Click();
        cut.FindAll(".ai-context-picker button").Last(button => button.TextContent.Contains("Add", StringComparison.Ordinal)).Click();
        Field(cut, "AiGitRange").Input(" HEAD~10..HEAD ");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/tasks")));
        var request = LastBody<CreateAiTaskDefinitionRequest>("POST", "api/ai/tasks");
        Assert.Equal(["AGENTS.md", "README.md"], request.ContextPaths);
        Assert.Equal("HEAD~10..HEAD", request.GitRange);
    }

    [Fact]
    public void TaskDialog_ServerTaskOffersNoRepositoryContextAndSendsNone()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/tasks", new AiTaskDefinitionDto { Id = 1, Name = "review" });

        var cut = Render<AiTaskDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        Assert.DoesNotContain("AiContextPaths", cut.Markup);
        Assert.DoesNotContain("AiGitRange", cut.Markup);
        Field(cut, "Name").Input("review");
        Field(cut, "PromptTemplate", "textarea").Input("Review");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/tasks")));
        var request = LastBody<CreateAiTaskDefinitionRequest>("POST", "api/ai/tasks");
        Assert.Empty(request.ContextPaths);
        Assert.Null(request.GitRange);
    }

    [Fact]
    public void TaskDialog_InEditModeLoadsTheDefinitionAndSendsAnUpdate()
    {
        _handler.SetJsonResponse("api/ai/tasks/7", new AiTaskDefinitionDto
        {
            Id = 7,
            Name = "nightly review",
            ProfileId = 4,
            PromptTemplate = "Review {diff}",
            ProjectId = 10,
            Schedule = "0 2 * * *",
            EventTypes = ["pipeline.completed"],
            Enabled = false
        });
        _handler.SetJsonResponse(
            HttpMethod.Put, "api/ai/tasks/7", new AiTaskDefinitionDto { Id = 7, Name = "nightly review" });

        var cut = Render<AiTaskDialog>(
            parameters => parameters.Add(dialog => dialog.DefinitionId, 7));
        cut.WaitForAssertion(() => cut.Find("form"));

        Assert.Contains("Save", cut.Markup);
        Assert.Equal("nightly review", Field(cut, "Name").GetAttribute("value"));
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("PUT", "api/ai/tasks/7")));
        var request = LastBody<UpdateAiTaskDefinitionRequest>("PUT", "api/ai/tasks/7");
        Assert.Equal("nightly review", request.Name);
        Assert.Equal(4, request.ProfileId);
        Assert.Equal(10, request.ProjectId);
        Assert.Equal("0 2 * * *", request.Schedule);
        Assert.Equal(["pipeline.completed"], request.EventTypes);
        Assert.False(request.Enabled);
    }

    [Fact]
    public void TaskDialog_StillRendersItsFormWhenTheOptionLoadFails()
    {
        _handler.SetResponse("api/ai/profile-options", HttpStatusCode.InternalServerError);

        var cut = Render<AiTaskDialog>();

        cut.WaitForAssertion(() => Assert.Contains("Create", cut.Markup));
    }

    // ---------- AiRunnerProfileDialog ----------

    [Fact]
    public void ProfileDialog_InCreateModeDoesNotCallTheBackendBeforeSubmit()
    {
        var cut = Render<AiRunnerProfileDialog>();

        cut.WaitForAssertion(() => cut.Find("form"));
        Assert.DoesNotContain(
            _handler.Requests,
            request => request.Url.Contains("api/ai/profiles", StringComparison.Ordinal));
    }

    [Fact]
    public void ProfileDialog_CreatesAnAnthropicProfile_WithTheSonnetLowEffortDefaults()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "claude-sonnet" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        Field(cut, "Name").Input("claude-sonnet");
        Field(cut, "AiApiKey").Input("sk-ant-typed");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        var request = LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles");
        Assert.Equal("claude-sonnet", request.Name);
        Assert.Equal(AiProvider.Anthropic, request.Provider);
        Assert.Equal("claude-sonnet-5-5", request.Model);
        Assert.Equal("sk-ant-typed", request.ApiKey);
        Assert.Equal(AiReasoningEffort.Low, request.ReasoningEffort);
        Assert.Equal("https://api.anthropic.com", request.BaseUrl);
        Assert.True(request.SendsDataExternally);
        // The key field is not a password-manager field: an API key is not the user's own password.
        Assert.NotEqual("password", Field(cut, "AiApiKey").GetAttribute("type"));
    }

    [Fact]
    public void ProfileDialog_OpenAiCompatibleVendor_SendsItsBaseUrlAndModel()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "vendor" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        var provider = Field(cut, "Provider", "select");
        var openAi = provider.QuerySelectorAll("option")
            .First(option => option.TextContent.Contains("AiProviderOpenAi", StringComparison.Ordinal));
        provider.Change(openAi.GetAttribute("value"));
        Field(cut, "Name").Input("vendor");
        PickModel(cut, "gpt-5.5");
        Field(cut, "AiBaseUrl").Input("https://llm.example.com/v1");
        Field(cut, "AiApiKey").Input("sk-vendor");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        var request = LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles");
        Assert.Equal(AiProvider.OpenAi, request.Provider);
        Assert.Equal("gpt-5.5", request.Model);
        Assert.Equal("https://llm.example.com/v1", request.BaseUrl);
    }

    [Fact]
    public void ProfileDialog_InEditMode_NeverReceivesTheKey_AndSendsItEmptyToKeepIt()
    {
        _handler.SetJsonResponse("api/ai/profiles/5", new AiRunnerProfileDto
        {
            Id = 5,
            Name = "claude-sonnet",
            Description = "the reviewer",
            Provider = AiProvider.Anthropic,
            Model = "claude-sonnet-5-5",
            HasApiKey = true,
            ReasoningEffort = AiReasoningEffort.Medium,
            MaxOutputTokens = 32_000,
            TimeoutSeconds = 900,
            MaxOutputBytes = 300_000,
            SendsDataExternally = true
        });
        _handler.SetJsonResponse(
            HttpMethod.Put, "api/ai/profiles/5", new AiRunnerProfileDto { Id = 5, Name = "claude-sonnet" });

        var cut = Render<AiRunnerProfileDialog>(
            parameters => parameters.Add(dialog => dialog.ProfileId, 5));
        cut.WaitForAssertion(() => Assert.Contains("AiApiKeyKeepHint", cut.Markup));

        Assert.Equal(string.Empty, Field(cut, "AiApiKey").GetAttribute("value") ?? string.Empty);
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("PUT", "api/ai/profiles/5")));
        var request = LastBody<UpdateAiRunnerProfileRequest>("PUT", "api/ai/profiles/5");
        Assert.Equal("claude-sonnet", request.Name);
        Assert.Equal("the reviewer", request.Description);
        Assert.Equal(string.Empty, request.ApiKey);
        Assert.Equal(AiReasoningEffort.Medium, request.ReasoningEffort);
        Assert.Equal(32_000, request.MaxOutputTokens);
        Assert.Equal(900, request.TimeoutSeconds);
        Assert.Equal(300_000, request.MaxOutputBytes);
    }

    [Fact]
    public void ProfileDialog_AFormerCliProfile_ShowsItMustBeReconfigured()
    {
        _handler.SetJsonResponse("api/ai/profiles/6", new AiRunnerProfileDto
        {
            Id = 6,
            Name = "claude-cli",
            NeedsReconfiguration = true,
            TimeoutSeconds = 600,
            MaxOutputBytes = 200_000
        });

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 6));

        cut.WaitForAssertion(() => Assert.Contains("AiProfileNeedsReconfiguration", cut.Markup));
    }

    [Fact]
    public void ProfileDialog_TestTheConnection_SendsTheProfileIdWithoutAKey_AndShowsTheOutcome()
    {
        _handler.SetJsonResponse("api/ai/profiles/5", new AiRunnerProfileDto
        {
            Id = 5,
            Name = "claude-sonnet",
            Provider = AiProvider.Anthropic,
            Model = "claude-sonnet-5-5",
            HasApiKey = true,
            TimeoutSeconds = 600,
            MaxOutputBytes = 200_000
        });
        _handler.SetJsonResponse(HttpMethod.Post, "api/ai/profiles/test",
            new AiConnectionTestDto { Succeeded = false, Message = "AI provider HTTP 401: the API key was refused.", DurationMs = 12 });

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 5));
        cut.WaitForAssertion(() => Assert.Contains("AiApiKeyKeepHint", cut.Markup));

        cut.FindAll("button").First(button => button.TextContent.Contains("TestConnection", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("AiConnectionFailed", cut.Markup));
        Assert.Contains("the API key was refused", cut.Markup);
        var request = LastBody<TestAiRunnerProfileRequest>("POST", "api/ai/profiles/test");
        Assert.Equal(5, request.ProfileId);
        Assert.Equal(string.Empty, request.ApiKey);
        Assert.False(Sent("PUT", "api/ai/profiles/5"));
    }

    [Fact]
    public void ProfileDialog_CreatesWithoutAName_AndAnnouncesTheGeneratedOne()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "anthropic-claude-sonnet-5-5" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));
        // The test localizer echoes keys: the hint is there, the name itself is proven by the backend test.
        Assert.Contains("AiProfileNameGenerated", Field(cut, "Name").GetAttribute("placeholder"), StringComparison.Ordinal);
        Field(cut, "AiApiKey").Input("sk-typed");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        Assert.Equal(string.Empty, LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles").Name);
    }

    [Fact]
    public void ProfileDialog_ProviderChange_MovesTheUntouchedUrlToTheNewProvider()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "openai" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));
        Assert.Equal("https://api.anthropic.com", Field(cut, "AiBaseUrl").GetAttribute("value"));

        var provider = Field(cut, "Provider", "select");
        provider.Change(provider.QuerySelectorAll("option")
            .First(option => option.TextContent.Contains("AiProviderOpenAi", StringComparison.Ordinal)).GetAttribute("value"));
        Field(cut, "Name").Input("openai");
        PickModel(cut, "gpt-5.5");
        Field(cut, "AiApiKey").Input("sk-openai");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        Assert.Equal("https://api.openai.com/v1", LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles").BaseUrl);
    }

    [Fact]
    public void ProfileDialog_TestRefusedByTheServer_ShowsTheReason()
    {
        _handler.SetJsonResponse(HttpMethod.Post, "api/ai/profiles/test",
            new { message = "The Model field is required." }, HttpStatusCode.BadRequest);

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));
        Field(cut, "AiApiKey").Input("sk-typed");
        cut.FindAll("button").First(button => button.TextContent.Contains("TestConnection", StringComparison.Ordinal)).Click();

        cut.WaitForAssertion(() => Assert.Contains("The Model field is required.", cut.Markup));
        Assert.Contains("AiConnectionFailed", cut.Markup);
    }

    [Fact]
    public void ProfileDialog_TestTheConnection_IsDisabledWithoutAnyKey()
    {
        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        var test = cut.FindAll("button").First(button => button.TextContent.Contains("TestConnection", StringComparison.Ordinal));

        Assert.True(test.HasAttribute("disabled"));
    }

    [Fact]
    public void ProfileDialog_StillRendersItsFormWhenTheProfileLoadFails()
    {
        _handler.SetResponse("api/ai/profiles/5", HttpStatusCode.InternalServerError);

        var cut = Render<AiRunnerProfileDialog>(
            parameters => parameters.Add(dialog => dialog.ProfileId, 5));

        cut.WaitForAssertion(() => cut.Find("form"));
    }

    [Fact]
    public void ProfileDialog_KeepsTheDialogOpenWhenTheBackendRejectsTheCreate()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post,
            "api/ai/profiles",
            (AiRunnerProfileDto?)null,
            HttpStatusCode.BadRequest);

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        Field(cut, "Name").Input("claude");
        Field(cut, "AiApiKey").Input("sk-x");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        Assert.NotNull(cut.Find("form"));
    }

    [Fact]
    public void R3_7_ProfileDialog_ATypedModelTheListLacks_IsSavedAsTheModel()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "x" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        var suggestions = OpenSuggestions(cut);
        Assert.Contains("claude-sonnet-5-5", suggestions);
        Assert.Contains("claude-opus-5-5", suggestions);
        Assert.Empty(cut.FindAll("#ai-profile-model-custom"));

        PickModel(cut, "claude-future-9");
        Field(cut, "AiApiKey").Input("sk-ant-typed");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        Assert.Equal("claude-future-9", LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles").Model);
    }

    [Fact]
    public void R3_7_ProfileDialog_APickedSuggestion_IsSavedAsTheModel()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "x" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));
        OpenSuggestions(cut);
        cut.FindAll("#ai-profile-model-field .omni-autocomplete__results li")
            .First(option => option.TextContent.Trim() == "claude-opus-5-5").Click();
        Field(cut, "AiApiKey").Input("sk-ant-typed");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        Assert.Equal("claude-opus-5-5", LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles").Model);
    }

    [Fact]
    public void R3_7_ProfileDialog_ATextMatchingASuggestionIgnoringCase_GivesTheSuggestionsValue()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "x" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));
        // The suggestion for the typed text is on screen (the search ran) when the field is left.
        var input = cut.Find("#ai-profile-model");
        input.Input("CLAUDE-OPUS-5-5");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("#ai-profile-model-field .omni-autocomplete__results li")));
        input.Blur();
        Field(cut, "AiApiKey").Input("sk-ant-typed");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        Assert.Equal("claude-opus-5-5", LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles").Model);
    }

    [Fact]
    public void R3_7_ProfileDialog_AListedModelFollowsTheProvider()
    {
        _handler.SetJsonResponse(
            HttpMethod.Post, "api/ai/profiles", new AiRunnerProfileDto { Id = 1, Name = "x" });

        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));
        var provider = Field(cut, "Provider", "select");
        provider.Change(provider.QuerySelectorAll("option")
            .First(option => option.TextContent.Contains("AiProviderOpenAi", StringComparison.Ordinal)).GetAttribute("value"));
        Field(cut, "AiApiKey").Input("sk-openai");
        cut.Find("form").Submit();

        cut.WaitForAssertion(() => Assert.True(Sent("POST", "api/ai/profiles")));
        Assert.Equal("gpt-5.5", LastBody<CreateAiRunnerProfileRequest>("POST", "api/ai/profiles").Model);
    }

    [Fact]
    public void R3_7_ProfileDialog_AnUnlistedStoredModel_IsShownInTheField()
    {
        _handler.SetJsonResponse("api/ai/profiles/6", new AiRunnerProfileDto
        {
            Id = 6,
            Name = "legacy",
            Provider = AiProvider.Anthropic,
            Model = "claude-legacy-1",
            HasApiKey = true,
            TimeoutSeconds = 600,
            MaxOutputBytes = 200_000
        });

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 6));

        cut.WaitForAssertion(() => Assert.Equal("claude-legacy-1", cut.Find("#ai-profile-model").GetAttribute("value")));
    }

    [Fact]
    public async Task R3_7_ProfileDialog_ReadNow_IsInTheTestMenu_VeilsTheModelField_ThenShowsTheNewList()
    {
        StoredProfileWithFetchedList(error: "AI provider HTTP 503: provider error.");
        var answer = new TaskCompletionSource<AiRunnerProfileDto>();
        _handler.SetAsyncJsonResponse(HttpMethod.Post, "api/ai/profiles/8/model-list/refresh", _ => answer.Task);

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 8));
        cut.WaitForAssertion(() => cut.Find("form"));
        // One visible action beside Save and Go back: Test, with "read now" in its menu (R3-41).
        var footer = cut.Find(".dialog-footer");
        Assert.Single(footer.QuerySelectorAll(".omni-split-button"));
        Assert.Equal(3, footer.QuerySelectorAll("button.omni-button, .omni-split-button__main").Length);
        Assert.False(cut.Find(".ai-profile-model-zone").ClassList.Contains("omni-busy"));
        Assert.Contains("AiModelListFailed", cut.Find("#ai-profile-model-field-help-content").TextContent, StringComparison.Ordinal);

        await cut.Find(".omni-split-button__toggle").ClickAsync(new());
        var item = cut.FindAll(".omni-split-button__menu .omni-menu__item")
            .Single(entry => entry.TextContent.Contains("AiModelListRefresh", StringComparison.Ordinal));
        var click = item.ClickAsync(new());

        // While the provider is read: the veil lies over the model field, which stays mounted under it.
        cut.WaitForAssertion(() =>
        {
            var zone = cut.Find(".ai-profile-model-zone");
            Assert.True(zone.ClassList.Contains("omni-busy"));
            Assert.Equal("true", zone.GetAttribute("aria-busy"));
            Assert.NotNull(zone.QuerySelector("#ai-profile-model"));
        });
        Assert.Empty(cut.FindAll(".aetheus-loader-logo"));

        answer.SetResult(new AiRunnerProfileDto
        {
            Id = 8,
            Name = "fetched",
            Provider = AiProvider.Anthropic,
            Model = "claude-haiku-5",
            HasApiKey = true,
            AvailableModels = ["claude-haiku-5", "claude-sonnet-6"],
            AvailableModelsFetchedAt = new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Local),
            ModelListCheckedAt = new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Local)
        });
        await click;

        cut.WaitForAssertion(() => Assert.False(cut.Find(".ai-profile-model-zone").ClassList.Contains("omni-busy")));
        Assert.Equal(["claude-haiku-5", "claude-sonnet-6"], OpenSuggestions(cut));
        // The test localizer echoes keys: the last error is gone from the "?" now that the read succeeded.
        var help = cut.Find("#ai-profile-model-field-help-content").TextContent;
        Assert.Contains("AiModelListFromProvider", help, StringComparison.Ordinal);
        Assert.DoesNotContain("AiModelListFailed", help, StringComparison.Ordinal);
        Assert.True(Sent("POST", "api/ai/profiles/8/model-list/refresh"));
        Assert.False(Sent("PUT", "api/ai/profiles/8"));
    }

    [Fact]
    public async Task R3_7_ProfileDialog_ReadNow_AModelNoLongerListed_StaysInTheField()
    {
        StoredProfileWithFetchedList();
        _handler.SetJsonResponse(HttpMethod.Post, "api/ai/profiles/8/model-list/refresh", new AiRunnerProfileDto
        {
            Id = 8,
            Provider = AiProvider.Anthropic,
            HasApiKey = true,
            AvailableModels = ["claude-opus-6"],
            ModelListCheckedAt = new DateTime(2026, 10, 13, 15, 0, 0, DateTimeKind.Local),
            ModelListError = "AI provider HTTP 503: provider error."
        });

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 8));
        cut.WaitForAssertion(() => cut.Find("form"));
        await cut.Find(".omni-split-button__toggle").ClickAsync(new());
        await cut.FindAll(".omni-split-button__menu .omni-menu__item")
            .Single(entry => entry.TextContent.Contains("AiModelListRefresh", StringComparison.Ordinal)).ClickAsync(new());

        cut.WaitForAssertion(() => Assert.Contains("AiModelListFailed", cut.Find("#ai-profile-model-field-help-content").TextContent, StringComparison.Ordinal));
        Assert.Equal("claude-haiku-5", cut.Find("#ai-profile-model").GetAttribute("value"));
        Assert.Equal(["claude-opus-6"], OpenSuggestions(cut));
        Assert.Contains("AiModelListFailed", cut.Find("#ai-profile-model-field-help-content").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task R3_7_ProfileDialog_ANewProfile_HasNoReadNow()
    {
        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        Assert.Empty(cut.FindAll(".omni-split-button"));
        Assert.False(cut.Instance.CanRefreshModelList);
        await cut.InvokeAsync(() => cut.Instance.RefreshModelListAsync());
        Assert.False(Sent("POST", "model-list/refresh"));
    }

    private void StoredProfileWithFetchedList(string? error = null) =>
        _handler.SetJsonResponse("api/ai/profiles/8", new AiRunnerProfileDto
        {
            Id = 8,
            Name = "fetched",
            Provider = AiProvider.Anthropic,
            Model = "claude-haiku-5",
            HasApiKey = true,
            TimeoutSeconds = 600,
            MaxOutputBytes = 200_000,
            AvailableModels = ["claude-haiku-5", "claude-opus-5-5", "claude-sonnet-5-5"],
            AvailableModelsFetchedAt = new DateTime(2026, 10, 13, 1, 5, 0, DateTimeKind.Local),
            ModelListCheckedAt = new DateTime(2026, 10, 13, 1, 5, 0, DateTimeKind.Local),
            ModelListError = error
        });

    [Fact]
    public void R3_7_ProfileDialog_OffersTheListReadAtTheProvider_AndSaysSoBehindTheQuestionMark()
    {
        StoredProfileWithFetchedList();

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 8));
        cut.WaitForAssertion(() => cut.Find("form"));

        Assert.Equal(["claude-haiku-5", "claude-opus-5-5", "claude-sonnet-5-5"], OpenSuggestions(cut));
        Assert.Equal("claude-haiku-5", cut.Find("#ai-profile-model").GetAttribute("value"));
        var help = cut.Find("#ai-profile-model-field-help-content").TextContent;
        Assert.Contains("AiModelListFromProvider", help, StringComparison.Ordinal);
        Assert.DoesNotContain("AiModelListFailed", help, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_7_ProfileDialog_ALastReadThatFailed_IsShownBehindTheQuestionMark()
    {
        StoredProfileWithFetchedList(error: "AI provider HTTP 401: the API key was refused.");

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 8));
        cut.WaitForAssertion(() => cut.Find("form"));

        Assert.Contains("AiModelListFailed", cut.Find("#ai-profile-model-field-help-content").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_7_ProfileDialog_AnotherProvider_FallsBackToTheApplicationList()
    {
        StoredProfileWithFetchedList();

        var cut = Render<AiRunnerProfileDialog>(parameters => parameters.Add(dialog => dialog.ProfileId, 8));
        cut.WaitForAssertion(() => cut.Find("form"));
        var provider = Field(cut, "Provider", "select");
        provider.Change(provider.QuerySelectorAll("option")
            .First(option => option.TextContent.Contains("AiProviderOpenAi", StringComparison.Ordinal)).GetAttribute("value"));

        Assert.Equal(["gpt-5.5"], OpenSuggestions(cut));
        Assert.Contains("AiModelListFixed", cut.Find("#ai-profile-model-field-help-content").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void R3_7_ProfileDialog_ANewProfile_UsesTheApplicationList()
    {
        var cut = Render<AiRunnerProfileDialog>();
        cut.WaitForAssertion(() => cut.Find("form"));

        Assert.Contains("AiModelListFixed", cut.Find("#ai-profile-model-field-help-content").TextContent, StringComparison.Ordinal);
    }
}
