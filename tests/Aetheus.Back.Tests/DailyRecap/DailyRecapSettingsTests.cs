// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Back.Components.DailyRecap;
using Aetheus.Back.Exceptions;

namespace Aetheus.Back.Tests;

/// <summary>The daily recap settings: their defaults, their storage and the validation of an update.</summary>
public sealed class DailyRecapSettingsTests
{
    private static UpdateDailyRecapSettingsRequest Valid() => new()
    {
        Enabled = true,
        SendTime = new TimeOnly(7, 30),
        TimeZoneId = "Europe/Paris",
        Language = DailyRecapLanguage.English,
        Sections = [DailyRecapSection.Pipelines, DailyRecapSection.Servers],
        Recipients = ["ops@example.org"]
    };

    [Fact]
    public void NothingSaved_MeansOnAtSevenParisInFrench_WithEverySection_ToTheAdministrators()
    {
        var settings = DailyRecapSettings.Parse(new Dictionary<string, string>());

        Assert.True(settings.Enabled);
        Assert.Equal(new TimeOnly(7, 0), settings.SendTime);
        Assert.Equal("Europe/Paris", settings.TimeZoneId);
        Assert.Equal(DailyRecapLanguage.French, settings.Language);
        Assert.Equal(Enum.GetValues<DailyRecapSection>().Length, settings.Sections.Count);
        Assert.Null(settings.Recipients);
    }

    [Fact]
    public void SavedSettings_ReadBackUnchanged()
    {
        var saved = DailyRecapSettings.FromRequest(Valid());

        var read = DailyRecapSettings.Parse(saved.ToRows());

        Assert.Equal(new TimeOnly(7, 30), read.SendTime);
        Assert.Equal(DailyRecapLanguage.English, read.Language);
        Assert.Equal([DailyRecapSection.Pipelines, DailyRecapSection.Servers], read.Sections.Order());
        Assert.Equal(["ops@example.org"], read.Recipients);
    }

    [Fact]
    public void AllSectionsSwitchedOff_StaysOff_InsteadOfFallingBackToAll()
    {
        var read = DailyRecapSettings.Parse(DailyRecapSettings.FromRequest(Valid() with { Sections = [] }).ToRows());

        Assert.Empty(read.Sections);
    }

    [Fact]
    public void UnknownTimezone_IsRefused()
    {
        var error = Assert.Throws<BadRequestException>(() => DailyRecapSettings.FromRequest(Valid() with { TimeZoneId = "Paris" }));

        Assert.Contains("Paris", error.Message);
    }

    [Fact]
    public void InvalidRecipient_IsRefused()
    {
        Assert.Throws<BadRequestException>(() => DailyRecapSettings.FromRequest(Valid() with { Recipients = ["not-an-address"] }));
    }

    [Fact]
    public void Recipients_AreTrimmed_AndBlankEntriesIgnored()
    {
        var settings = DailyRecapSettings.FromRequest(Valid() with { Recipients = [" ops@example.org ", "", "dev@example.org"] });

        Assert.Equal(["ops@example.org", "dev@example.org"], settings.Recipients);
    }

    [Fact]
    public void DuplicateRecipient_IsRefused()
    {
        var error = Assert.Throws<BadRequestException>(() => DailyRecapSettings.FromRequest(Valid() with { Recipients = ["ops@example.org", "OPS@example.org"] }));

        Assert.Contains("twice", error.Message);
    }

    [Fact]
    public void SwitchedOnWithoutRecipient_IsRefused_ButSwitchedOffIsAccepted()
    {
        Assert.Throws<BadRequestException>(() => DailyRecapSettings.FromRequest(Valid() with { Recipients = [] }));

        var off = DailyRecapSettings.FromRequest(Valid() with { Enabled = false, Recipients = [] });
        var read = DailyRecapSettings.Parse(off.ToRows());

        Assert.False(read.Enabled);
        Assert.NotNull(read.Recipients);
        Assert.Empty(read.Recipients);
    }

    [Fact]
    public void UnknownSectionOrLanguage_IsRefused()
    {
        Assert.Throws<BadRequestException>(() => DailyRecapSettings.FromRequest(Valid() with { Sections = [(DailyRecapSection)99] }));
        Assert.Throws<BadRequestException>(() => DailyRecapSettings.FromRequest(Valid() with { Language = (DailyRecapLanguage)9 }));
    }

    [Fact]
    public void SendTime_KeepsHoursAndMinutesOnly()
    {
        var settings = DailyRecapSettings.FromRequest(Valid() with { SendTime = new TimeOnly(6, 45, 31) });

        Assert.Equal(new TimeOnly(6, 45), settings.SendTime);
    }
}
