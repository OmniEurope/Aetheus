// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Pipelines;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Time.Testing;

namespace Aetheus.Front.Tests.Pages.Pipelines;

/// <summary>R-34: a user who is not a site Admin and belongs to no organization has nothing to choose in the build planner.</summary>
public sealed class PipelineScheduleNoOrganizationMemberTests : BunitContext
{
    [Fact]
    public async Task AUserOfNoOrganization_GetsTheMessageAlone_WithNothingToChoose()
    {
        var handler = BunitTestHelper.RegisterServices(this, authenticated: true, isAdmin: false);
        BunitTestHelper.UseImmediateDialogs(this);
        Services.AddSingleton<IStringLocalizer<Aetheus.Front.Resources.AppStrings>>(ResxLocalizer.English());
        Services.AddSingleton<TimeProvider>(new FakeTimeProvider(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero)));
        handler.SetJsonResponse("api/organizations/me", new List<MyOrganizationDto>());
        await Services.GetRequiredService<ActiveOrganizationService>().RefreshAsync(Xunit.TestContext.Current.CancellationToken);

        var cut = Render<PipelineSchedule>();

        cut.WaitForAssertion(() => Assert.Contains("Choose an organization to see its planner.", cut.Markup, StringComparison.Ordinal));
        Assert.Empty(cut.FindAll("[data-testid='organization-required-picker']"));
    }
}
