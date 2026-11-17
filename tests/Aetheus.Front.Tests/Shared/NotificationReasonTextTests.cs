// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Front.Components.Notifications;
using Aetheus.Front.Resources;
using Aetheus.Front.Tests.Pages.Servers;
using Microsoft.Extensions.Localization;

namespace Aetheus.Front.Tests.Shared;

/// <summary>R-30 follow-up: the inbox shows the reason of a refused automated launch in the user's language, from the
/// problem codes of the payload, and keeps the English reason of a notification stored before the codes existed.</summary>
public sealed class NotificationReasonTextTests : IDisposable
{
    private const string English = "1 scheduled occurrence(s) were missed.";

    private const string Payload =
        """{"PipelineName":"nightly","TriggerSource":"Scheduler","Reason":"1 scheduled occurrence(s) were missed.","ReasonProblems":[{"Code":"ScheduleOccurrenceMissed","Parameters":{"count":"1","first":"2026-11-05 02:00","last":"2026-11-05 02:00","limit":"6"}}]}""";

    private readonly CultureInfo _previousUiCulture = CultureInfo.CurrentUICulture;
    private readonly IStringLocalizer<AppStrings> _localizer = MailOperationDialogValidationTests.RealLocalizer();

    public void Dispose() => CultureInfo.CurrentUICulture = _previousUiCulture;

    private static string ReasonOf(IReadOnlyList<(string Name, string Value)> elements) =>
        elements.Single(element => element.Name == "Reason").Value;

    private IReadOnlyList<(string Name, string Value)> Shown(string culture, string payload)
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
        return NotificationReasonText.Apply(
            NotificationEventTypes.PipelineLaunchRefused, InboxNotificationDialog.PayloadElements(payload), _localizer);
    }

    [Fact]
    public void French_SaysTheReasonInFrench_AndHidesTheCodes()
    {
        var elements = Shown("fr-FR", Payload);

        Assert.Equal(
            "L'occurrence planifiée du 2026-11-05 02:00 UTC a été manquée au-delà de la limite de rattrapage de 6 h et n'a pas été lancée.",
            ReasonOf(elements));
        Assert.DoesNotContain(elements, element => element.Name == "ReasonProblems");
    }

    [Fact]
    public void English_SaysTheReasonFromTheCode()
    {
        Assert.Equal(
            "The scheduled occurrence of 2026-11-05 02:00 UTC was missed for longer than the catch-up limit of 6 h and was not run.",
            ReasonOf(Shown("en-US", Payload)));
    }

    [Fact]
    public void AnOldRowWithoutCodes_ShowsItsEnglishReason_InFrench()
    {
        var elements = Shown("fr-FR", """{"PipelineName":"nightly","Reason":"1 scheduled occurrence(s) were missed."}""");

        Assert.Equal(English, ReasonOf(elements));
    }

    [Fact]
    public void AnUnknownCode_ShowsTheEnglishReason()
    {
        var elements = Shown("fr-FR", """{"Reason":"1 scheduled occurrence(s) were missed.","ReasonProblems":[{"Code":"SomeFutureCode","Parameters":{}}]}""");

        Assert.Equal(English, ReasonOf(elements));
        Assert.DoesNotContain(elements, element => element.Name == "ReasonProblems");
    }

    [Fact]
    public void AnotherEventType_IsLeftAsItIs()
    {
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fr-FR");
        var elements = InboxNotificationDialog.PayloadElements(Payload);

        Assert.Same(elements, NotificationReasonText.Apply(NotificationEventTypes.PipelineFailed, elements, _localizer));
    }
}
