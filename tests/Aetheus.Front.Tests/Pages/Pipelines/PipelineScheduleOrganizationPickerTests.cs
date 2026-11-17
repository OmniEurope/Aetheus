// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>
/// R-34: the build planner needs an organization. A site Admin starts without one ("all organizations"), so
/// the page offers the organization choice itself instead of a message with no way out. Driven through the
/// markup against a mocked HTTP API with the real English resources; the mocked HTTP proves what the page
/// shows and sends, not the real endpoints (their refusal of a foreign organization is proved by the
/// back integration tests) nor the real layout.
/// </summary>
public sealed class PipelineScheduleOrganizationPickerTests : BunitContext
{
    private static readonly DateTime Now = new(2026, 11, 2, 18, 0, 0, DateTimeKind.Utc);
    private const string Picker = "[data-testid='organization-required-picker']";

    private readonly BunitTestHelper.TestHandler _handler;

    public PipelineScheduleOrganizationPickerTests()
    {
        _handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: true);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(Now)));
        _handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto>());
        _handler.SetJsonResponse("api/organizations/administered", new List<AdministeredOrganizationDto>
        {
            new(4, "Aetheus", []),
            new(9, "Other", [])
        });
        foreach (var id in new[] { 4, 9 })
            _handler.SetJsonResponse(HttpMethod.Get, $"api/pipelines/schedule-plan?organizationId={id}", new SchedulePlanDto
            {
                OrganizationId = id,
                FromUtc = Now,
                ToUtc = Now.AddDays(7),
                Tasks =
                [
                    new()
                    {
                        PipelineId = id * 10, PipelineName = $"nightly-{id}", Enabled = true, CronExpression = "0 0 * * *",
                        Frequency = ScheduleFrequency.EveryEvening, Hour = 0, Minute = 0, DurationMinutes = 30, IsPlanned = true,
                        NextRunUtc = Now.AddHours(6)
                    }
                ]
            });
    }

    private async Task<IRenderedComponent<PipelineSchedule>> RenderWithoutOrganizationAsync()
    {
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync(Xunit.TestContext.Current.CancellationToken);
        return Render<PipelineSchedule>();
    }

    [Fact]
    public async Task WithoutAnOrganization_ThePageOffersTheOrganizationsInItself_NotADeadEndMessage()
    {
        var cut = await RenderWithoutOrganizationAsync();

        cut.WaitForElement(Picker, BunitTestHelper.AsyncRenderWait);
        Assert.Contains("Choose an organization to see its planner.", cut.Markup, StringComparison.Ordinal);
        var options = cut.Find($"{Picker} select").QuerySelectorAll("option").ToList();
        // The planner needs one organization: "all organizations" is not offered here.
        Assert.Equal(["Aetheus", "Other"], options.Where(option => !string.IsNullOrEmpty(option.GetAttribute("value"))).Select(option => option.TextContent.Trim()));
        Assert.DoesNotContain(options, option => option.TextContent.Contains("All organizations", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid^='schedule-card-']"));
    }

    [Fact]
    public async Task ChoosingAnOrganizationInThePage_LoadsItsPlanner_AndTheChoiceBecomesTheActiveOrganization()
    {
        var cut = await RenderWithoutOrganizationAsync();
        cut.WaitForElement(Picker, BunitTestHelper.AsyncRenderWait);

        var select = cut.Find($"{Picker} select");
        // An option's value is its position in the list, not the organization id: choose it by what it says.
        select.Change(select.QuerySelectorAll("option").Single(option => option.TextContent.Trim() == "Other").GetAttribute("value"));

        cut.WaitForElement("[data-testid='schedule-card-90']", BunitTestHelper.AsyncRenderWait);
        Assert.Equal(9, Services.GetRequiredService<ActiveOrganizationService>().Active!.Id);
        Assert.Contains(_handler.RequestsSnapshot(), request => request.Url.Contains("schedule-plan?organizationId=9", StringComparison.Ordinal));
        Assert.DoesNotContain(_handler.RequestsSnapshot(), request => request.Url.Contains("schedule-plan?organizationId=4", StringComparison.Ordinal));
        Assert.Empty(cut.FindAll(Picker));
    }
}
