// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// PLAN-005 lot 3, the "Schedule a pipeline" and "Change frequency" form, driven through its markup against a
/// mocked HTTP API, the real English resources and a clock one hour ahead of UTC. What the mocks cannot prove
/// (the real preview of the real YAML, the written file) is the plan's end-of-lot control, not these tests.
/// </summary>
public sealed class PipelineScheduleDialogTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Tomorrow = new(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

    private readonly BunitTestHelper.TestHandler _handler;
    // Read when a test runs, never in the constructor: asking the container closes it to new registrations.
    private ImmediateDialogService _dialog => (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();

    public PipelineScheduleDialogTests()
    {
        _handler = BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        var clock = new FakeTimeProvider(new DateTimeOffset(Now));
        clock.SetLocalTimeZone(TimeZoneInfo.CreateCustomTimeZone("test-plus-one", TimeSpan.FromHours(1), "Test", "Test"));
        Services.AddSingleton<TimeProvider>(clock);
        SetPreview();
    }

    private static ScheduledTaskDto Task() => new()
    {
        PipelineId = 1,
        PipelineName = "nightly",
        Enabled = true,
        CronExpression = "0 0 * * *",
        Frequency = ScheduleFrequency.EveryEvening,
        Hour = 0,
        Minute = 0,
        DurationMinutes = 72,
        NextRunUtc = Tomorrow
    };

    private void SetPreview(string? yamlError = null, params ScheduleOverlapDto[] overlaps) =>
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/1/schedule-preview", new SchedulePreviewDto
        {
            PipelineId = 1,
            FromUtc = Now,
            ToUtc = Now.AddDays(7),
            YamlError = yamlError,
            Overlaps = [.. overlaps],
            Proposal = Task() with
            {
                Occurrences =
                [
                    new() { StartUtc = Tomorrow, EndUtc = Tomorrow.AddMinutes(72) },
                    new() { StartUtc = Tomorrow.AddDays(1), EndUtc = Tomorrow.AddDays(1).AddMinutes(72) },
                    new() { StartUtc = Tomorrow.AddDays(2), EndUtc = Tomorrow.AddDays(2).AddMinutes(72) },
                    new() { StartUtc = Tomorrow.AddDays(3), EndUtc = Tomorrow.AddDays(3).AddMinutes(72) }
                ]
            }
        });

    private IRenderedComponent<PipelineScheduleDialog> RenderEdit(ScheduledTaskDto? item = null)
    {
        var cut = Render<PipelineScheduleDialog>(parameters => parameters
            .Add(dialog => dialog.Item, item ?? Task())
            .Add(dialog => dialog.OrganizationId, 4)
            .Add(dialog => dialog.OnUndo, saved => { _undone = saved; return System.Threading.Tasks.Task.CompletedTask; }));
        cut.WaitForState(() => cut.Markup.Contains("schedule-next-runs", StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        return cut;
    }

    private PipelineScheduleDialogResult? _undone;

    private void Save(IRenderedComponent<PipelineScheduleDialog> cut) => cut.Find("form").Submit();

    // The request as the server reads it, so an assertion is on a typed value and not on a JSON path.
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    private static UpdatePipelineScheduleRequest Sent(string? body) =>
        JsonSerializer.Deserialize<UpdatePipelineScheduleRequest>(body!, WebJson)!;

    // ---------- the form ----------

    [Fact]
    public void RequiredFieldsCarryTheStar_AndTheHelpIsTheQuestionMark_NeverATextUnderTheField()
    {
        var cut = RenderEdit();

        foreach (var label in new[] { "Pipeline", "Frequency", "Start time" })
        {
            var field = cut.FindAll("label").FirstOrDefault(candidate => candidate.TextContent.Contains(label, StringComparison.Ordinal));
            Assert.NotNull(field);
            Assert.Contains("omni-label--required", field.ClassList);
        }

        // Frequency and start time explain themselves in the "?", whose content is part of the markup.
        Assert.Contains("Every two days follows the parity", cut.Markup, StringComparison.Ordinal);
        Assert.Contains("Your time (Test). It is written in UTC", cut.Markup, StringComparison.Ordinal);
        // One "?" for the pipeline, one for the frequency and one for the start time.
        Assert.Equal(3, cut.FindAll(".omni-form-field__help-mark").Count);
        Assert.DoesNotContain(" style=", cut.Markup, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFormStartsOnTheTasksFrequencyAndTheReadersLocalTime()
    {
        var cut = RenderEdit();

        // 00:00 UTC for a reader one hour ahead is 01:00; the time picker shows what the reader says, not the engine's UTC.
        Assert.Equal(new TimeOnly(1, 0).ToString("t", CultureInfo.CurrentCulture),
            cut.Find("#schedule-time").GetAttribute("value") ?? string.Empty, ignoreCase: true);
    }

    [Fact]
    public void EditingKeepsThePipelineFixed()
    {
        var cut = RenderEdit();

        Assert.True(cut.Find("#schedule-pipeline").HasAttribute("disabled"));
    }

    // ---------- preview and verdict ----------

    [Fact]
    public void ThePreviewListsTheNextThreeRuns_AtTheReadersClock_AndSaysNoOverlap()
    {
        var cut = RenderEdit();

        var runs = cut.FindAll("[data-testid='schedule-next-runs'] span").Select(span => span.TextContent.Trim()).ToList();
        Assert.Equal(3, runs.Count);
        Assert.Equal(TimeZoneInfo.ConvertTimeFromUtc(Tomorrow, TimeZoneInfo.CreateCustomTimeZone("test-plus-one", TimeSpan.FromHours(1), "Test", "Test")).ToString("g", CultureInfo.CurrentCulture), runs[0]);
        Assert.Equal("No overlap with another pipeline over the next 7 days.", cut.Find("[data-testid='schedule-verdict-none']").TextContent.Trim());
    }

    [Fact]
    public void AnOverlapIsAWarningWithTheOtherPipelineAndTheMinutes_AndSavingStaysPossible()
    {
        SetPreview(null, new ScheduleOverlapDto { PipelineId = 2, PipelineName = "docs", OverlapMinutes = 42, OverlappingRuns = 3 });

        var cut = RenderEdit();

        var verdict = cut.Find("[data-testid='schedule-verdict-overlap']");
        Assert.Equal("Runs at the same time as docs for 42 min.", verdict.TextContent.Trim());
        Assert.NotEmpty(verdict.QuerySelectorAll("svg, .omni-icon, [class*='omni-icon']"));
        Assert.False(cut.Find("[data-testid='schedule-save']").HasAttribute("disabled"));
    }

    [Fact]
    public void AYamlTheChangeWouldBreakIsSaid_AndBlocksSaving()
    {
        SetPreview("key written twice");

        var cut = RenderEdit();

        Assert.Equal("The pipeline file cannot be changed: key written twice", cut.Find("[data-testid='schedule-yaml-error']").TextContent.Trim());
        Assert.True(cut.Find("[data-testid='schedule-save']").HasAttribute("disabled"));
    }

    [Fact]
    public void ACustomScheduleWarnsThatSavingReplacesIt()
    {
        var custom = Task() with { Frequency = ScheduleFrequency.Custom, CronExpression = "*/20 6 * * *", Hour = null, Minute = null };

        var cut = RenderEdit(custom);

        Assert.Contains("replaces it", cut.Find("[data-testid='schedule-custom-warning']").TextContent, StringComparison.Ordinal);
    }

    // ---------- saving ----------

    [Fact]
    public void Saving_WritesTheFrequencyInUtc_WithWhatWasRead_AndClosesWithAnUndo()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/1/schedule", new PipelineDto { Id = 1, Name = "nightly" });
        var cut = RenderEdit();

        Save(cut);

        cut.WaitForAssertion(() => Assert.Equal(1, _dialog.CloseCount), BunitTestHelper.AsyncRenderWait);
        var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
        var body = Sent(put.Body);
        Assert.Equal(ScheduleFrequency.EveryEvening, body.Frequency);
        // The form shows 01:00 for the reader; the file gets what the engine reads, 00:00 UTC.
        Assert.Equal(0, body.Hour);
        Assert.Equal(0, body.Minute);
        Assert.Equal("0 0 * * *", body.ExpectedCronExpression);
        Assert.True(body.ExpectedEnabled);
        var result = Assert.IsType<PipelineScheduleDialogResult>(_dialog.LastCloseResult);
        Assert.True(result.Saved);
        Assert.Equal("nightly", result.PipelineName);
        Assert.Equal(ScheduleFrequency.EveryEvening, result.Undo!.Frequency);
        Assert.Equal(0, result.Undo.Hour);
    }

    [Fact]
    public async Task Saving_SaysSoInAToastWithUndo_WhichHandsThePreviousValueToThePage()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/1/schedule", new PipelineDto { Id = 1, Name = "nightly" });
        var cut = RenderEdit();

        Save(cut);

        cut.WaitForAssertion(() => Assert.Equal(1, _dialog.CloseCount), BunitTestHelper.AsyncRenderWait);
        var toast = Assert.Single(Services.GetRequiredService<OmniOverlayService>().Notifications);
        Assert.Equal(OmniSeverity.Success, toast.Severity);
        Assert.StartsWith("nightly: Every evening at ", toast.Message, StringComparison.Ordinal);
        Assert.Equal("Undo", toast.ActionText);
        await cut.InvokeAsync(() => toast.Action!());
        Assert.Equal(1, _undone!.PipelineId);
        Assert.Equal(ScheduleFrequency.EveryEvening, _undone.Undo!.Frequency);
    }

    [Fact]
    public void Saving_ForAPipelineWhoseOwnerIsInactive_WarnsAsWell()
    {
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/1/schedule", new PipelineDto { Id = 1, Name = "nightly", OwnerUsername = "gone", OwnerInactive = true });
        var cut = RenderEdit();

        Save(cut);

        cut.WaitForAssertion(() => Assert.Contains(Services.GetRequiredService<OmniOverlayService>().Notifications,
            toast => toast.Severity == OmniSeverity.Warning), BunitTestHelper.AsyncRenderWait);
    }

    [Fact]
    public void ARefusedSave_RaisesNoSuccessToast()
    {
        _handler.SetRawResponse(HttpMethod.Put, "api/pipelines/1/schedule", "{\"message\":\"stale\"}", "application/json", HttpStatusCode.Conflict);
        var cut = RenderEdit();

        Save(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-refusal']")), BunitTestHelper.AsyncRenderWait);
        Assert.Empty(Services.GetRequiredService<OmniOverlayService>().Notifications);
    }

    [Theory]
    [InlineData(HttpStatusCode.Conflict, "The schedule changed")]
    [InlineData(HttpStatusCode.Forbidden, "Not allowed")]
    [InlineData(HttpStatusCode.NotFound, "Pipeline not found")]
    public void ARefusedSave_IsSaidInPlaceWithReload_AndTheDialogStaysOpen(HttpStatusCode status, string title)
    {
        _handler.SetRawResponse(HttpMethod.Put, "api/pipelines/1/schedule", "{\"message\":\"the reason\"}", "application/json", status);
        var cut = RenderEdit();

        Save(cut);

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-refusal']")), BunitTestHelper.AsyncRenderWait);
        var alert = cut.Find("[data-testid='schedule-refusal']");
        Assert.Contains(title, alert.TextContent, StringComparison.Ordinal);
        Assert.Contains("the reason", alert.TextContent, StringComparison.Ordinal);
        Assert.Equal(0, _dialog.CloseCount);
    }

    [Fact]
    public void Reload_AfterARefusal_ClosesTheDialogAskingThePageToRead()
    {
        _handler.SetRawResponse(HttpMethod.Put, "api/pipelines/1/schedule", "{\"message\":\"stale\"}", "application/json", HttpStatusCode.Conflict);
        var cut = RenderEdit();
        Save(cut);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-refusal-reload']")), BunitTestHelper.AsyncRenderWait);

        cut.Find("[data-testid='schedule-refusal-reload']").Click();

        var result = Assert.IsType<PipelineScheduleDialogResult>(_dialog.LastCloseResult);
        Assert.True(result.Reload);
        Assert.False(result.Saved);
    }

    [Fact]
    public void ABadRequest_StaysOnTheForm_WithTheBackendsReason_AndNoReload()
    {
        _handler.SetRawResponse(HttpMethod.Put, "api/pipelines/1/schedule", "{\"message\":\"hour out of range\"}", "application/json", HttpStatusCode.BadRequest);
        var cut = RenderEdit();

        Save(cut);

        cut.WaitForAssertion(() => Assert.Contains("hour out of range", cut.Find("[data-testid='schedule-refusal']").TextContent, StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        Assert.Empty(cut.FindAll("[data-testid='schedule-refusal-reload']"));
        Assert.Equal(0, _dialog.CloseCount);
    }

    // ---------- creating ----------

    [Fact]
    public void Creating_OffersOnlyThePipelinesOfTheOrganizationThatHaveNoScheduleYet()
    {
        _handler.SetPaginatedJsonResponse("api/pipelines/fleet", new List<PipelineFleetItemDto>
        {
            new() { PipelineId = 1, PipelineName = "already scheduled", OrganizationId = 4 },
            new() { PipelineId = 10, PipelineName = "alpha", OrganizationId = 4 },
            new() { PipelineId = 11, PipelineName = "elsewhere", OrganizationId = 9 }
        });

        var cut = Render<PipelineScheduleDialog>(parameters => parameters
            .Add(dialog => dialog.OrganizationId, 4)
            .Add(dialog => dialog.ScheduledPipelineIds, [1]));
        cut.WaitForState(() => cut.FindAll("#schedule-pipeline").Count > 0, BunitTestHelper.AsyncRenderWait);

        // The list of the picker opens on a click: only the pipeline of this organization without a schedule is in it.
        cut.Find("#schedule-pipeline").Click();
        cut.WaitForState(() => cut.FindAll("[role='option']").Count > 0, BunitTestHelper.AsyncRenderWait);
        Assert.Equal(["alpha"], cut.FindAll("[role='option']").Select(option => option.TextContent.Trim()).ToList());
        Assert.Empty(cut.FindAll("[data-testid='schedule-no-candidates']"));
        Assert.True(cut.Find("[data-testid='schedule-save']").HasAttribute("disabled"));
    }

    [Fact]
    public void Creating_AfterChoosingAPipeline_PreviewsAndWritesAnEnabledScheduleWithNothingExpected()
    {
        _handler.SetPaginatedJsonResponse("api/pipelines/fleet", new List<PipelineFleetItemDto>
        {
            new() { PipelineId = 10, PipelineName = "alpha", OrganizationId = 4 }
        });
        _handler.SetJsonResponse(HttpMethod.Get, "api/pipelines/10/schedule-preview", new SchedulePreviewDto { PipelineId = 10, Proposal = Task() with { PipelineId = 10 } });
        _handler.SetJsonResponse(HttpMethod.Put, "api/pipelines/10/schedule", new PipelineDto { Id = 10, Name = "alpha" });
        var cut = Render<PipelineScheduleDialog>(parameters => parameters.Add(dialog => dialog.OrganizationId, 4));
        cut.WaitForState(() => cut.FindAll("#schedule-pipeline").Count > 0, BunitTestHelper.AsyncRenderWait);
        cut.Find("#schedule-pipeline").Click();
        cut.WaitForState(() => cut.FindAll("[role='option']").Count > 0, BunitTestHelper.AsyncRenderWait);

        cut.Find("[role='option']").Click();
        cut.WaitForState(() => cut.Markup.Contains("schedule-next-runs", StringComparison.Ordinal), BunitTestHelper.AsyncRenderWait);
        Assert.False(cut.Find("[data-testid='schedule-save']").HasAttribute("disabled"));
        Save(cut);

        cut.WaitForAssertion(() => Assert.Equal(1, _dialog.CloseCount), BunitTestHelper.AsyncRenderWait);
        var put = Assert.Single(_handler.RequestDetails, request => request.Method == "PUT");
        var body = Sent(put.Body);
        Assert.True(body.Enabled);
        Assert.Equal(ScheduleFrequency.EveryEvening, body.Frequency);
        // The default 02:00 of the reader (one hour ahead of UTC) is 01:00 UTC in the file.
        Assert.Equal(1, body.Hour);
        Assert.Equal(string.Empty, body.ExpectedCronExpression);
        Assert.False(body.ExpectedEnabled);
        // Undoing a creation switches the new schedule off.
        var result = Assert.IsType<PipelineScheduleDialogResult>(_dialog.LastCloseResult);
        Assert.False(result.Undo!.Enabled);
        Assert.Null(result.Undo.Frequency);
    }

    [Fact]
    public void Creating_WhenEveryPipelineIsScheduled_SaysSoInsteadOfAnEmptyList()
    {
        _handler.SetPaginatedJsonResponse("api/pipelines/fleet", new List<PipelineFleetItemDto>
        {
            new() { PipelineId = 1, PipelineName = "already scheduled", OrganizationId = 4 }
        });

        var cut = Render<PipelineScheduleDialog>(parameters => parameters
            .Add(dialog => dialog.OrganizationId, 4)
            .Add(dialog => dialog.ScheduledPipelineIds, [1]));

        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll("[data-testid='schedule-no-candidates']")), BunitTestHelper.AsyncRenderWait);
    }
}
