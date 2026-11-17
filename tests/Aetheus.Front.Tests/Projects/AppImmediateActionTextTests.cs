// SPDX-License-Identifier: EUPL-1.2
using Aetheus.Front.Components.Projects;
using Aetheus.Front.Tests.TestDoubles;

namespace Aetheus.Front.Tests.Projects;

/// <summary> R-29: what the Supervision says after an immediate action, from the real French resources: the result with
/// its time when it arrived, the waiting step otherwise, and what is likely missing when nothing came. </summary>
public sealed class AppImmediateActionTextTests
{
    private static readonly DateTime Requested = new(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void NoRequest_SaysNothing()
    {
        Assert.Null(AppImmediateActionText.Probe(ResxLocalizer.French(), new AppProbeNowDto()));
        Assert.Null(AppImmediateActionText.Telemetry(ResxLocalizer.French(), null));
    }

    [Fact]
    public void AProbeResult_GivesTheStatusTheTimeAndTheDelay()
    {
        var at = Requested.AddSeconds(1);
        var up = AppImmediateActionText.Probe(ResxLocalizer.French(), new AppProbeNowDto
        {
            RequestedAt = Requested,
            Result = new AppHealthSampleDto { Timestamp = at, IsUp = true, StatusCode = 200, ResponseTimeMs = 42 }
        });
        var down = AppImmediateActionText.Probe(ResxLocalizer.French(), new AppProbeNowDto
        {
            RequestedAt = Requested,
            Result = new AppHealthSampleDto { Timestamp = at, IsUp = false, Error = "probe:timeout;host=a.example" }
        });

        Assert.Equal(OmniSeverity.Success, up?.Severity);
        Assert.Equal($"Disponible : HTTP 200 en 42 ms, sondée le {AppImmediateActionText.Time(at)}.", up?.Text);
        Assert.Equal(OmniSeverity.Danger, down?.Severity);
        Assert.Equal($"Indisponible : a.example n'a pas répondu dans le délai imparti, sondée le {AppImmediateActionText.Time(at)}.", down?.Text);
    }

    [Theory]
    [InlineData("probe:forbidden_target;host=localhost:5301", "Aetheus refuse de sonder une adresse privée ou locale (localhost:5301), par sécurité")]
    [InlineData("probe:dns;host=nope.example", "adresse introuvable : le nom nope.example n'est pas résolu")]
    [InlineData("probe:refused;host=a.example:8080", "connexion refusée par a.example:8080")]
    [InlineData("probe:timeout;host=a.example", "a.example n'a pas répondu dans le délai imparti")]
    [InlineData("probe:tls;host=a.example", "échec de la connexion sécurisée (certificat ou TLS) avec a.example")]
    [InlineData("probe:status;host=a.example;status=503;expected=200", "HTTP 503 reçu au lieu de HTTP 200")]
    [InlineData("probe:unreachable;host=a.example", "a.example est injoignable (la sonde a échoué)")]
    public void AFailureCode_IsSaidAsALocalizedSentence_NeverTheRawText(string stored, string expected)
    {
        var at = Requested.AddSeconds(1);
        var down = AppImmediateActionText.Probe(ResxLocalizer.French(), new AppProbeNowDto
        {
            RequestedAt = Requested,
            Result = new AppHealthSampleDto { Timestamp = at, IsUp = false, Error = stored }
        });

        Assert.Equal($"Indisponible : {expected}, sondée le {AppImmediateActionText.Time(at)}.", down?.Text?.Replace('’', '\''));
        Assert.DoesNotContain("probe:", down?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOldPlainTextFailure_IsShownAsItWas()
    {
        var down = AppImmediateActionText.Probe(ResxLocalizer.French(), new AppProbeNowDto
        {
            RequestedAt = Requested,
            Result = new AppHealthSampleDto { Timestamp = Requested, IsUp = false, Error = "Probe target resolved to a forbidden address." }
        });

        Assert.Contains("Probe target resolved to a forbidden address.", down?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAgentProbe_WaitsThenSaysItGotNoAnswer()
    {
        var waiting = AppImmediateActionText.Probe(ResxLocalizer.French(), new AppProbeNowDto { RequestedAt = Requested, ByAgent = true });
        var late = AppImmediateActionText.Probe(ResxLocalizer.French(), new AppProbeNowDto { RequestedAt = Requested, ByAgent = true, TimedOut = true });

        Assert.Equal(OmniSeverity.Info, waiting?.Severity);
        Assert.StartsWith($"Sonde demandée le {AppImmediateActionText.Time(Requested)}", waiting?.Text, StringComparison.Ordinal);
        Assert.Equal(OmniSeverity.Warning, late?.Severity);
        Assert.Contains("Aucun résultat", late?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATelemetryAnswer_GivesEachStepWithItsTime_AndTheVisits()
    {
        var delivered = Requested.AddSeconds(20);
        var received = Requested.AddSeconds(21);
        var message = AppImmediateActionText.Telemetry(ResxLocalizer.French(), new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            DeliveredAt = delivered,
            ReceivedAt = received,
            AnalyticsEnabled = true
        });

        Assert.Equal(OmniSeverity.Success, message?.Severity);
        Assert.StartsWith(
            $"Métriques reçues le {AppImmediateActionText.Time(received)} (demandées le {AppImmediateActionText.Time(Requested)}, prises par l'application le {AppImmediateActionText.Time(delivered)}).",
            message?.Text, StringComparison.Ordinal);
        Assert.EndsWith("rien n'attend d'être demandé).", message?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void ARequestNoPackageTook_NamesTheMissingPackageVersion()
    {
        var message = AppImmediateActionText.Telemetry(ResxLocalizer.French(), new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            TimedOut = true,
            AnalyticsEnabled = false
        });

        Assert.Equal(OmniSeverity.Warning, message?.Severity);
        Assert.Contains("Aetheus.Telemetry est antérieur à 1.2.2", message?.Text, StringComparison.Ordinal);
        Assert.EndsWith("La mesure d'audience n'est pas activée pour cette application.", message?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingReceivedSinceTheRequest_GivesTheLastDataBeforeIt_OrThatNoneEverCame()
    {
        var before = Requested.AddHours(-2);
        var withLast = AppImmediateActionText.Telemetry(ResxLocalizer.French(), new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            TimedOut = true,
            LastIngestAt = before
        });
        var never = AppImmediateActionText.Telemetry(ResxLocalizer.French(), new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            TimedOut = true
        });

        Assert.Equal(OmniSeverity.Warning, withLast?.Severity);
        Assert.Contains($"la dernière donnée reçue de l'application date du {AppImmediateActionText.Time(before)}", withLast?.Text?.Replace('’', '\''), StringComparison.Ordinal);
        Assert.Equal(OmniSeverity.Warning, never?.Severity);
        Assert.Contains("Aucune donnée n'a jamais été reçue de cette application", never?.Text?.Replace('’', '\''), StringComparison.Ordinal);
    }

    [Fact]
    public void APeriodicExportThatArrivedAfterTheClick_IsReportedAsTelemetryReceived_NotAsNothing()
    {
        var data = Requested.AddSeconds(70);
        var message = AppImmediateActionText.Telemetry(ResxLocalizer.French(), new AppTelemetryExportRequestDto
        {
            RequestedAt = Requested,
            TimedOut = true,
            LastIngestAt = data,
            DataReceivedAt = data
        });

        Assert.Equal(OmniSeverity.Success, message?.Severity);
        Assert.StartsWith($"Télémétrie reçue le {AppImmediateActionText.Time(data)} (envoi périodique).", message?.Text, StringComparison.Ordinal);
        Assert.Contains("antérieur à 1.2.2", message?.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void WhileWaiting_DataAlreadyArrivedAfterTheClickIsSaid_AndTheTestStaysOpen()
    {
        var data = Requested.AddSeconds(8);
        var state = new AppTelemetryExportRequestDto { RequestedAt = Requested, LastIngestAt = data, DataReceivedAt = data };
        var message = AppImmediateActionText.Telemetry(ResxLocalizer.French(), state);

        Assert.Equal(OmniSeverity.Info, message?.Severity);
        Assert.Contains($"Des données de l'application sont arrivées le {AppImmediateActionText.Time(data)}, après la demande.", message?.Text?.Replace('’', '\''), StringComparison.Ordinal);
        Assert.True(AppImmediateActions.IsWaiting(state));
    }

    [Fact]
    public void Polling_StopsOnceTheAnswerArrivedOrTimedOut()
    {
        Assert.True(AppImmediateActions.IsWaiting(new AppProbeNowDto { RequestedAt = Requested }));
        Assert.False(AppImmediateActions.IsWaiting(new AppProbeNowDto { RequestedAt = Requested, TimedOut = true }));
        Assert.False(AppImmediateActions.IsWaiting(new AppProbeNowDto { RequestedAt = Requested, Result = new AppHealthSampleDto() }));
        Assert.True(AppImmediateActions.IsWaiting(new AppTelemetryExportRequestDto { RequestedAt = Requested, DeliveredAt = Requested }));
        Assert.False(AppImmediateActions.IsWaiting(new AppTelemetryExportRequestDto { RequestedAt = Requested, ReceivedAt = Requested }));
        Assert.False(AppImmediateActions.IsWaiting((AppTelemetryExportRequestDto?)null));
    }
}
