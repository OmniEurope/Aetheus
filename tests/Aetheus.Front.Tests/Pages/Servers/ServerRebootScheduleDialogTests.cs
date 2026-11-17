// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Servers.ServerDetailSections;
using Aetheus.Front.Tests.TestDoubles;
using Bunit;
using Microsoft.Extensions.DependencyInjection;

namespace Aetheus.Front.Tests.Pages.Servers;

/// <summary>Recette R-8 (option B): the form of a server's daily reboot schedule.</summary>
public sealed class ServerRebootScheduleDialogTests : BunitContext
{
    private readonly ImmediateDialogService _dialogs;

    public ServerRebootScheduleDialogTests()
    {
        BunitTestHelper.RegisterServices(this);
        BunitTestHelper.UseImmediateDialogs(this);
        _dialogs = (ImmediateDialogService)Services.GetRequiredService<OmniDialogService>();
    }

    private IRenderedComponent<ServerRebootScheduleDialog> RenderDialog(ServerRebootScheduleDto? schedule = null)
    {
        var cut = Render<ServerRebootScheduleDialog>(p => p.Add(c => c.Schedule, schedule));
        BunitTestHelper.Interactive(this);
        return cut;
    }

    [Fact]
    public void RequiredFieldsCarryAStar_AndTheirHelpIsAMark_NotATextUnderTheField()
    {
        var cut = RenderDialog();

        var fields = cut.FindComponents<OmniFormField>();
        Assert.Equal(2, fields.Count);
        Assert.All(fields, field => Assert.True(field.Instance.Required));
        Assert.All(fields, field => Assert.False(string.IsNullOrWhiteSpace(field.Instance.Help)));
    }

    [Fact]
    public void ANewSchedule_StartsEnabledAtFourInTheMorningInUtc()
    {
        var cut = RenderDialog();
        cut.Find("form").Submit();

        var request = Assert.IsType<SaveServerRebootScheduleRequest>(_dialogs.LastCloseResult);
        Assert.Equal((4, 0, "UTC", true), (request.Hour, request.Minute, request.TimeZoneId, request.Enabled));
    }

    [Fact]
    public void AnExistingSchedule_FillsTheForm_AndSubmitsItsValues()
    {
        var cut = RenderDialog(new ServerRebootScheduleDto { Hour = 23, Minute = 15, TimeZoneId = "Europe/Brussels", Enabled = false });

        cut.Find("form").Submit();

        var request = Assert.IsType<SaveServerRebootScheduleRequest>(_dialogs.LastCloseResult);
        Assert.Equal((23, 15, "Europe/Brussels", false), (request.Hour, request.Minute, request.TimeZoneId, request.Enabled));
    }

    [Fact]
    public void AZoneOutsideTheClosedList_IsKeptAmongTheOptions_SoSavingDoesNotChangeIt()
    {
        var cut = RenderDialog(new ServerRebootScheduleDto { Hour = 1, Minute = 0, TimeZoneId = "Pacific/Auckland", Enabled = true });

        var zones = cut.FindComponent<OmniDropDown<string>>().Instance.Options.Select(option => option.Value).ToList();
        Assert.Contains("Pacific/Auckland", zones);
        Assert.Contains("UTC", zones);

        cut.Find("form").Submit();

        Assert.Equal("Pacific/Auckland", Assert.IsType<SaveServerRebootScheduleRequest>(_dialogs.LastCloseResult).TimeZoneId);
    }

    [Fact]
    public void EveryZoneOfTheListIsOneTheBackendAccepts_AndUtcComesFirst()
    {
        Assert.Equal("UTC", ServerRebootScheduleDialog.Zones[0]);
        Assert.Equal(ServerRebootScheduleDialog.Zones.Count, ServerRebootScheduleDialog.Zones.Distinct(StringComparer.Ordinal).Count());
        foreach (var zone in ServerRebootScheduleDialog.Zones.Skip(1))
            Assert.True(TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _), zone);
    }
}
