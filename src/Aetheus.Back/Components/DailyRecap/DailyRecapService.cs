// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using Aetheus.Back.Components.Notifications.MailTemplates;
using Aetheus.Back.Services.Email;
using OmniEurope.Blazor.Components;

namespace Aetheus.Back.Components.DailyRecap;

/// <summary>
/// The daily recap: builds yesterday's e-mail from the saved settings, sends it through the
/// platform sender (the no-reply mailbox by default), and decides once a day whether the
/// scheduled send is due. A day is claimed before it is sent, so a restart, a second instance or
/// an SMTP failure never sends the same day twice; a failed day is reported, and "Send now" is
/// there to try again by hand.
/// </summary>
internal sealed class DailyRecapService(
    IDailyRecapRepository repo,
    DailyRecapCollector collector,
    IEmailSender sender,
    IMailTemplateRenderer templates,
    IAuditService audit,
    TimeProvider timeProvider,
    IConfiguration configuration,
    ILogger<DailyRecapService> logger) : IDailyRecapService
{
    internal const string NoRecipientMessage =
        "No recipient: the list is empty and no active administrator has an e-mail address; nothing was sent.";

    public async Task<DailyRecapSettingsDto> GetSettingsAsync(CancellationToken ct = default)
    {
        var rows = await repo.GetSettingRowsAsync(ct).ConfigureAwait(false);
        var settings = DailyRecapSettings.Parse(rows);
        var last = DailyRecapLastSend.TryParse(rows.GetValueOrDefault(DailyRecapSettingKeys.LastResult));
        return new DailyRecapSettingsDto
        {
            Enabled = settings.Enabled,
            SendTime = settings.SendTime,
            TimeZoneId = settings.TimeZoneId,
            Language = settings.Language,
            Sections = [.. Enum.GetValues<DailyRecapSection>().Where(settings.Sections.Contains)],
            Recipients = await RecipientsAsync(settings, ct).ConfigureAwait(false),
            RecipientsAreDefault = settings.Recipients is null,
            LastSentDate = ReadLastSentDate(rows),
            LastSentAt = last?.SentAtUtc,
            LastRecipientCount = last?.Recipients,
            LastDeliveredCount = last?.Delivered,
            LastError = last?.Error
        };
    }

    public async Task<DailyRecapSettingsDto> UpdateSettingsAsync(UpdateDailyRecapSettingsRequest request, CancellationToken ct = default)
    {
        var settings = DailyRecapSettings.FromRequest(request);
        await repo.SaveSettingRowsAsync(settings.ToRows(), Now, ct).ConfigureAwait(false);
        await audit.LogAsync("Updated", "Setting", null, "DailyRecap", ct).ConfigureAwait(false);
        return await GetSettingsAsync(ct).ConfigureAwait(false);
    }

    public async Task<DailyRecapPreviewDto> PreviewAsync(CancellationToken ct = default)
    {
        var settings = DailyRecapSettings.Parse(await repo.GetSettingRowsAsync(ct).ConfigureAwait(false));
        var timeZone = ResolveTimeZone(settings);
        var day = DailyRecapClock.LocalToday(Now, timeZone).AddDays(-1);
        var recap = await BuildAsync(settings, timeZone, day, ct).ConfigureAwait(false);
        return new DailyRecapPreviewDto { Day = day, Subject = recap.Subject, Html = recap.Html, Text = recap.Text };
    }

    public async Task<DailyRecapSendResultDto> SendNowAsync(CancellationToken ct = default)
    {
        var settings = DailyRecapSettings.Parse(await repo.GetSettingRowsAsync(ct).ConfigureAwait(false));
        var timeZone = ResolveTimeZone(settings);
        var day = DailyRecapClock.LocalToday(Now, timeZone).AddDays(-1);
        var recipients = await RecipientsAsync(settings, ct).ConfigureAwait(false);
        if (recipients.Count == 0)
            return new DailyRecapSendResultDto { Status = DailyRecapSendStatus.NotConfigured, Message = NoRecipientMessage };

        var outcome = await SendAsync(settings, timeZone, day, recipients, ct).ConfigureAwait(false);
        if (outcome.Delivered > 0)
            await audit.LogAsync("Sent", "DailyRecap", null, day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
        return new DailyRecapSendResultDto
        {
            Status = outcome.Status,
            Message = outcome.Message,
            Recipients = recipients,
            Delivered = outcome.Delivered
        };
    }

    /// <summary>
    /// One pass of the scheduler: sends yesterday's recap when today's send time has passed and today
    /// was not sent yet. Returns why it did or did not send, for the tests and the log.
    /// </summary>
    internal async Task<DailyRecapScheduleOutcome> RunScheduledAsync(CancellationToken ct)
    {
        var rows = await repo.GetSettingRowsAsync(ct).ConfigureAwait(false);
        var settings = DailyRecapSettings.Parse(rows);
        var now = Now;
        var timeZone = ResolveTimeZone(settings);
        var decision = DailyRecapClock.Decide(now, settings.Enabled, settings.SendTime, timeZone, ReadLastSentDate(rows));
        if (decision.Outcome != DailyRecapScheduleOutcome.Due) return decision.Outcome;

        // The day is claimed before anything is sent: whatever happens next, it is not sent twice.
        await repo.SaveSettingRowsAsync(new Dictionary<string, string>
        {
            [DailyRecapSettingKeys.LastSentDate] = decision.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
        }, now, ct).ConfigureAwait(false);

        DailyRecapLastSend last;
        try
        {
            last = await SendScheduledAsync(settings, timeZone, decision.CoveredDay, now, ct).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The day is already claimed and will not be retried: the failure is shown on the settings page.
            logger.LogError(exception, "[DailyRecap] recap of {Day} could not be built or sent", decision.CoveredDay);
            last = new DailyRecapLastSend(now, settings.Recipients?.Count ?? 0, 0, exception.Message);
        }

        await repo.SaveSettingRowsAsync(new Dictionary<string, string>
        {
            [DailyRecapSettingKeys.LastResult] = last.Serialize()
        }, Now, ct).ConfigureAwait(false);
        logger.LogInformation(
            "[DailyRecap] recap of {Day} sent to {Delivered} of {Recipients} recipient(s){Error}",
            decision.CoveredDay, last.Delivered, last.Recipients, last.Error is null ? string.Empty : ": " + last.Error);
        return decision.Outcome;
    }

    private async Task<DailyRecapLastSend> SendScheduledAsync(
        DailyRecapSettings settings, TimeZoneInfo timeZone, DateOnly coveredDay, DateTime now, CancellationToken ct)
    {
        var recipients = await RecipientsAsync(settings, ct).ConfigureAwait(false);
        if (recipients.Count == 0) return new DailyRecapLastSend(now, 0, 0, NoRecipientMessage);
        var outcome = await SendAsync(settings, timeZone, coveredDay, recipients, ct).ConfigureAwait(false);
        return new DailyRecapLastSend(now, recipients.Count, outcome.Delivered,
            outcome.Status == DailyRecapSendStatus.Sent ? null : outcome.Message);
    }

    /// <summary>The saved list, or the active administrators' addresses while no list was ever saved.</summary>
    private async Task<List<string>> RecipientsAsync(DailyRecapSettings settings, CancellationToken ct) =>
        settings.Recipients is { } saved
            ? [.. saved]
            : await repo.GetActiveAdministratorEmailsAsync(ct).ConfigureAwait(false);

    /// <summary>
    /// Sends one message per recipient over one connection. Sent only when every address was accepted;
    /// otherwise the first refusal, verbatim, with the address it concerns.
    /// </summary>
    private async Task<(DailyRecapSendStatus Status, int Delivered, string Message)> SendAsync(
        DailyRecapSettings settings, TimeZoneInfo timeZone, DateOnly day, List<string> recipients, CancellationToken ct)
    {
        var recap = await BuildAsync(settings, timeZone, day, ct).ConfigureAwait(false);
        var results = await sender.SendBatchAsync(
            recipients.Select(address => new EmailMessage(address, recap.Subject, recap.Text, recap.Html)).ToList(), ct).ConfigureAwait(false);
        var delivered = results.Count(result => result.Status == EmailSendStatus.Sent);
        var refusal = results
            .Select((result, index) => (result, address: recipients[index]))
            .FirstOrDefault(item => item.result.Status != EmailSendStatus.Sent);
        if (refusal.result is null)
            return (DailyRecapSendStatus.Sent, delivered, results[0].Message);
        var status = results.All(result => result.Status == EmailSendStatus.NotConfigured)
            ? DailyRecapSendStatus.NotConfigured
            : DailyRecapSendStatus.Failed;
        return (status, delivered, $"{refusal.address}: {refusal.result.Message}");
    }

    private async Task<(string Subject, string Html, string Text)> BuildAsync(
        DailyRecapSettings settings, TimeZoneInfo timeZone, DateOnly day, CancellationToken ct)
    {
        var report = await collector.CollectAsync(day, timeZone, settings.Sections, Now, ct).ConfigureAwait(false);
        var document = DailyRecapComposer.Compose(report, new DailyRecapText(settings.Culture), FrontOrigin);
        // One mail for every recipient: the recap's own language, the platform's default colours.
        var style = new MailRecipientStyle(MailTemplateLanguages.Normalize(settings.Culture.Name), OmniMailTheme.Default);
        var mail = await templates.RenderAsync(MailTemplateKind.DailyRecap,
            DailyRecapHtmlRenderer.Data(document), style, ct).ConfigureAwait(false);
        return (mail.Subject, mail.Html, mail.Text);
    }

    /// <summary>The settings page refuses an unknown zone; a host without timezone data falls back to UTC, and says so.</summary>
    private TimeZoneInfo ResolveTimeZone(DailyRecapSettings settings)
    {
        if (DailyRecapClock.TryResolveTimeZone(settings.TimeZoneId, out var timeZone)) return timeZone;
        logger.LogWarning("[DailyRecap] timezone {TimeZone} is unknown on this host; UTC is used", settings.TimeZoneId);
        return TimeZoneInfo.Utc;
    }

    private static DateOnly? ReadLastSentDate(IReadOnlyDictionary<string, string> rows) =>
        rows.TryGetValue(DailyRecapSettingKeys.LastSentDate, out var text)
        && DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date : null;

    private DateTime Now => timeProvider.GetUtcNow().UtcDateTime;

    /// <summary>The first Cors:Origins entry (FRONT_URL in the blue-green compose files),
    /// else Cors:FrontendUrl for local launches.</summary>
    private string? FrontOrigin =>
        configuration["Cors:Origins:0"] is { Length: > 0 } front ? front : configuration["Cors:FrontendUrl"];
}
