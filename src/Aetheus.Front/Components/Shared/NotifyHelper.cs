// SPDX-License-Identifier: EUPL-1.2
using OmniEurope.Blazor.Components;

namespace Aetheus.Front.Components.Shared;

/// <summary>Route toasts through the shared OE host and use the summary as the message when detail
/// is absent because OE requires a nonempty message; report errors with a correlation identifier so
/// administrators can locate their system-log context.</summary>
public class NotifyHelper(
    OmniOverlayService overlay,
    IStringLocalizer<AppStrings> localizer,
    ClientErrorReporter? reporter = null,
    AuthStateProvider? auth = null)
{
    internal static readonly TimeSpan InfoDuration = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan WarningDuration = TimeSpan.FromSeconds(7);
    internal static readonly TimeSpan ErrorDuration = TimeSpan.FromSeconds(10);

    public void Success(string titleKey, string messageKey, params object[] args) =>
        Raise(OmniSeverity.Success, localizer[titleKey].Value, FormatMessage(messageKey, args));

    public void Error(string titleKey, string messageKey, params object[] args) =>
        ErrorRaw(localizer[titleKey].Value, FormatMessage(messageKey, args));

    public void Info(string titleKey, string messageKey, params object[] args) =>
        Raise(OmniSeverity.Info, localizer[titleKey].Value, FormatMessage(messageKey, args));

    public void Warning(string titleKey, string messageKey, params object[] args) =>
        Raise(OmniSeverity.Warning, localizer[titleKey].Value, FormatMessage(messageKey, args));

    public void Notify(OmniSeverity severity, string titleKey, string rawMessage)
    {
        if (severity == OmniSeverity.Danger)
        {
            ErrorRaw(localizer[titleKey].Value, rawMessage);
            return;
        }
        Raise(severity, localizer[titleKey].Value, rawMessage);
    }

    private string FormatMessage(string messageKey, object[] args) =>
        args.Length > 0
            ? string.Format(localizer[messageKey].Value, args)
            : localizer[messageKey].Value;

    public void Success(string summaryKey) =>
        Raise(OmniSeverity.Success, localizer[summaryKey].Value, string.Empty);

    public void Error(string summaryKey) =>
        ErrorRaw(localizer[summaryKey].Value, string.Empty);

    public void ErrorRaw(
        string summary,
        string detail,
        string? correlationId = null,
        bool reportClientError = true)
    {
        correlationId ??= Guid.NewGuid().ToString("N");
        if (reportClientError)
            reporter?.Report(
                correlationId,
                summary,
                string.IsNullOrWhiteSpace(detail) ? summary : detail);

        // Relative to <base href="/">, without the leading slash: OE checks the link with
        // Uri.TryCreate(RelativeOrAbsolute), which on Linux (where the tests also run) reads "/admin/…"
        // as an absolute file URI and refuses it.
        var logsHref = auth?.IsAdmin == true
            ? $"admin/system-logs?search={Uri.EscapeDataString(correlationId)}"
            : null;
        Raise(OmniSeverity.Danger, summary, detail, logsHref);
    }

    private void Raise(OmniSeverity severity, string summary, string detail, string? detailsHref = null)
    {
        var hasDetail = !string.IsNullOrWhiteSpace(detail);
        overlay.Notify(
            hasDetail ? detail : summary,
            severity,
            hasDetail ? summary : null,
            DurationOf(severity),
            detailsHref);
    }

    internal static TimeSpan DurationOf(OmniSeverity severity) => severity switch
    {
        OmniSeverity.Danger => ErrorDuration,
        OmniSeverity.Warning => WarningDuration,
        _ => InfoDuration
    };

}
