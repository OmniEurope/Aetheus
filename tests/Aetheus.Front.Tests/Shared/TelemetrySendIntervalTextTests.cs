// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Tests.TestDoubles;

namespace Aetheus.Front.Tests.Shared;

/// <summary>
/// The sentence the telemetry views and the « Conservation des données » section use to state the send
/// interval, read from the real resource files so the wording itself is what is checked.
/// </summary>
public sealed class TelemetrySendIntervalTextTests
{
    [Fact]
    public void TheStandardProfile_ReadsAsTheUserAskedForIt_InFrenchAndInEnglish()
    {
        Assert.Equal(
            "Envoi toutes les 5 min (profil Standard) : un point toutes les 5 minutes",
            TelemetrySendIntervalText.Setting(ResxLocalizer.French(), 300, DataRetentionProfile.Standard, isProjectOverride: false));
        Assert.Equal(
            "Sent every 5 min (Standard profile): one point every 5 minutes",
            TelemetrySendIntervalText.Setting(ResxLocalizer.English(), 300, DataRetentionProfile.Standard, isProjectOverride: false));
    }

    [Fact]
    public void AProjectOverride_NamesTheProjectSetting_AndOneMinuteReadsAsSuch()
    {
        Assert.Equal(
            "Envoi chaque minute (réglage du projet) : un point par minute",
            TelemetrySendIntervalText.Setting(ResxLocalizer.French(), 60, DataRetentionProfile.Light, isProjectOverride: true));
        Assert.Equal("toutes les 45 secondes", TelemetrySendIntervalText.Long(ResxLocalizer.French(), 45));
        Assert.Equal("toutes les 45 s", TelemetrySendIntervalText.Short(ResxLocalizer.French(), 45));
    }

    [Fact]
    public void AnOldPackage_IsShownWithTheIntervalItActuallySends()
    {
        var observation = TelemetrySendIntervalText.Observation(ResxLocalizer.French(),
            new AppTelemetrySendIntervalDto { ServerIntervalSeconds = 300, ObservedIntervalSeconds = 60 });

        Assert.NotNull(observation);
        Assert.StartsWith("Intervalle observé : un point par minute.", observation, StringComparison.Ordinal);
        Assert.Contains("Aetheus.Telemetry 1.1.0", observation, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutAnObservedInterval_TheVersionThatAppliesItIsNamed()
    {
        var observation = TelemetrySendIntervalText.Observation(ResxLocalizer.French(),
            new AppTelemetrySendIntervalDto { ServerIntervalSeconds = 300, ObservedIntervalSeconds = null });

        Assert.Equal(
            "L'intervalle du serveur ne s'applique qu'à partir d'Aetheus.Telemetry 1.1.0 ; une version antérieure garde son propre intervalle.",
            observation);
    }

    [Theory]
    [InlineData(300, 300, true)]
    [InlineData(300, 305, true)]
    [InlineData(300, 330, true)]
    [InlineData(300, 60, false)]
    [InlineData(60, 70, false)]
    public void ASendThatFollowsTheServer_SaysNothingMore(int server, int observed, bool follows)
    {
        Assert.Equal(follows, TelemetrySendIntervalText.Follows(server, observed));
        var observation = TelemetrySendIntervalText.Observation(ResxLocalizer.French(),
            new AppTelemetrySendIntervalDto { ServerIntervalSeconds = server, ObservedIntervalSeconds = observed });
        Assert.Equal(follows, observation is null);
    }
}
