// SPDX-License-Identifier: EUPL-1.2
using System.Text;
using System.Text.Json;
using Aetheus.Back.Components.Notifications.MailTemplates;
using static Aetheus.Back.Components.Notifications.MailTemplates.NotificationMailVariables;
using static Aetheus.Back.Services.Email.EmailHtmlLayout;

namespace Aetheus.Back.Components.Notifications;

/// <summary>Presentation only: dispatch, subscriptions and delivery outcomes remain in the existing services.</summary>
internal sealed class NotificationEmailComposer(string language)
{
    private static readonly Dictionary<string, string> Events = new(StringComparer.Ordinal)
    {
        ["pipeline.failed"] = "PipelineFailed",
        ["pipeline.succeeded"] = "PipelineSucceeded",
        ["pipeline.rejected"] = "PipelineRejected",
        ["pipeline.child-results"] = "PipelineChildResult",
        ["pipeline.approval-requested"] = "PipelineApproval",
        ["pipeline.launch-refused"] = "PipelineRefused",
        ["release.deployed"] = "ReleaseDeployed",
        ["ai-task.completed"] = "AiTaskCompleted",
        ["agent-update.completed"] = "AgentCompleted",
        ["agent-update.failed"] = "AgentFailed",
        ["analysis.report.completed"] = "AnalysisCompleted",
        ["analysis.gate.warning"] = "GateWarning",
        ["analysis.gate.blocked"] = "GateBlocked",
        ["analysis.gate.error"] = "GateError",
        ["analysis.cve-sync.failed"] = "CveFailed",
        ["analysis.governance.expiring"] = "GovernanceExpiring",
        ["analysis.operational.cve-sync-failed"] = "OperationalCve",
        ["analysis.operational.cve-db-stale"] = "CveStale",
        ["analysis.operational.storage-drift"] = "StorageDrift",
        ["analysis.operational.scanner-obsolete"] = "ScannerObsolete",
        ["analysis.operational.workspace-residual"] = "WorkspaceResidual",
        ["analysis.operational.scanner-unavailable"] = "ScannerUnavailable",
        ["app.down"] = "AppDown",
        ["app.recovered"] = "AppRecovered",
        ["app.analytics.quota"] = "AnalyticsQuota",
        ["project.storage.quota"] = "StorageQuota",
        ["app.metric.threshold"] = "MetricThreshold",
        ["alert.triggered"] = "AlertTriggered",
        ["certbot.renewal-check.failed"] = "CertificateFailed",
        ["SecretExpiring"] = "SecretExpiring",
        ["server.offline"] = "ServerOffline",
        ["notification.test"] = "ChannelTest"
    };

    /// <summary>
    /// The status an event stands for when its payload names none the banner knows: its colour is the
    /// colour of that status in <see cref="StatusColours"/>. An event absent here is neutral.
    /// </summary>
    private static readonly Dictionary<string, string> EventStatuses = new(StringComparer.Ordinal)
    {
        ["pipeline.failed"] = "Failed",
        ["pipeline.rejected"] = "Failed",
        ["pipeline.launch-refused"] = "Failed",
        ["agent-update.failed"] = "Failed",
        ["analysis.gate.blocked"] = "Blocked",
        ["analysis.gate.error"] = "Error",
        ["analysis.cve-sync.failed"] = "Failed",
        ["analysis.operational.cve-sync-failed"] = "Failed",
        ["analysis.operational.scanner-unavailable"] = "Failed",
        ["app.down"] = "Down",
        ["certbot.renewal-check.failed"] = "Failed",
        ["server.offline"] = "Offline",
        ["pipeline.succeeded"] = "Succeeded",
        ["release.deployed"] = "Succeeded",
        ["agent-update.completed"] = "Succeeded",
        ["app.recovered"] = "Healthy",
        ["analysis.gate.warning"] = "Warning",
        ["analysis.governance.expiring"] = "Warning",
        ["analysis.operational.cve-db-stale"] = "Warning",
        ["analysis.operational.storage-drift"] = "Warning",
        ["analysis.operational.scanner-obsolete"] = "Warning",
        ["analysis.operational.workspace-residual"] = "Warning",
        ["app.analytics.quota"] = "Warning",
        ["project.storage.quota"] = "Warning",
        ["app.metric.threshold"] = "Warning",
        ["alert.triggered"] = "Warning",
        ["SecretExpiring"] = "Warning"
    };

    /// <summary>
    /// What the notification template (recette R1-75) is rendered with, in the recipient's language (recette
    /// R1-74), English by default: the texts and links, and the blocks only Aetheus can write (the status
    /// banner, the detail table, the note under the button).
    /// </summary>
    public static MailTemplateData Build(NotificationEmail email, string? frontOrigin, string language = MailTemplateLanguages.English) =>
        new NotificationEmailComposer(MailTemplateLanguages.Normalize(language)).Compose(email, frontOrigin);

    /// <summary>The values of the channel test template (recette R1-75).</summary>
    public static Dictionary<string, string?> ChannelTestValues(string channelName, string? frontOrigin) =>
        new(StringComparer.Ordinal)
        {
            [ChannelName] = channelName,
            [InboxUrl] = Url(frontOrigin, "/notifications") ?? string.Empty
        };

    private MailTemplateData Compose(NotificationEmail email, string? frontOrigin)
    {
        ArgumentNullException.ThrowIfNull(email);
        var key = Events.GetValueOrDefault(email.EventType, "Unknown");
        var heading = T(key + "Title");
        using var payload = Parse(email.PayloadJson);
        var root = payload?.RootElement;
        var projectPrefix = email.EventType + " in project ";
        var context = Context(email, root, projectPrefix);
        var (path, linkKey) = Destination(email.EventType, root);
        var link = Url(frontOrigin, path);
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Event] = heading,
            [Summary] = heading + (string.IsNullOrWhiteSpace(context) ? "" : Sep + OneLine(context)),
            [Intro] = T(key + "Intro"),
            [NotificationMailVariables.Context] = context ?? string.Empty,
            [Project] = Value(root, "ProjectName") ?? ProjectOfSubject(email, projectPrefix) ?? string.Empty,
            // No address, no button: the note under it says why instead.
            [Link] = link ?? string.Empty,
            [LinkLabel] = link is null ? string.Empty : T(linkKey),
            [InboxUrl] = Url(frontOrigin, "/notifications") ?? string.Empty
        };
        var blocks = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StatusBanner] = Banner(heading, BannerColours(email.EventType, root)),
            [Details] = DetailTable(email, root, projectPrefix),
            [LinkNote] = link is null ? NoLinkNote() : RootRunNote(root, frontOrigin)
        };
        return new MailTemplateData(values, blocks);
    }

    // The named subject of the event: a payload name first, else the project or free subject line.
    private static string? Context(NotificationEmail email, JsonElement? root, string projectPrefix)
    {
        var context = Value(root, "PipelineName") ?? Value(root, "ServerName") ?? Value(root, "AppName")
            ?? Value(root, "ProjectName");
        if (context is null && email.Subject.StartsWith(projectPrefix, StringComparison.Ordinal))
            context = email.Subject[projectPrefix.Length..];
        if (context is null && !string.Equals(email.Subject, email.EventType, StringComparison.Ordinal))
            context = email.Subject;
        return context;
    }

    private static string? ProjectOfSubject(NotificationEmail email, string projectPrefix) =>
        email.Subject.StartsWith(projectPrefix, StringComparison.Ordinal) ? email.Subject[projectPrefix.Length..] : null;

    /// <summary>The head of the mail: the platform, the kind of mail and the event, on the colours of its status.</summary>
    private string Banner(string heading, (string Background, string Ink) colours) =>
        new StringBuilder("<div style=\"padding:20px 24px;background-color:").Append(colours.Background)
            .Append(";color:").Append(colours.Ink).Append("\">")
            .Append("<span style=\"font-size:12px;font-weight:700;color:").Append(colours.Ink).Append("\">AETHEUS · ")
            .Append(Encode(T("Label"))).Append("</span><br>")
            .Append("<strong style=\"font-size:22px;color:").Append(colours.Ink).Append("\">").Append(Encode(heading)).Append("</strong>")
            .Append("</div>").ToString();

    /// <summary>The colours of the status the payload reports, else of the status the event stands for, else neutral.</summary>
    private static (string Background, string Ink) BannerColours(string eventType, JsonElement? root)
    {
        if (root is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (property.Name is "Status" or "GateStatus" or "Severity"
                    && StatusName(eventType, property) is { } status && StatusColours.TryGetValue(status, out var reported))
                    return reported;
            }
        }
        return EventStatuses.TryGetValue(eventType, out var standsFor) ? StatusColours[standsFor] : NeutralStatusColours;
    }

    private string DetailTable(NotificationEmail email, JsonElement? root, string projectPrefix)
    {
        var html = new StringBuilder("<table>");
        if (Value(root, "ProjectName") is null && ProjectOfSubject(email, projectPrefix) is { } project)
            Detail(html, T("ProjectName"), project);
        if (root is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                Detail(html, NotificationEmailText.Label(property.Name, language), DisplayField(email.EventType, property),
                    property.Name is "Status" or "GateStatus" or "Severity", StatusName(email.EventType, property));
            }
        }
        else if (!string.IsNullOrWhiteSpace(email.PayloadJson))
            Detail(html, T("Payload"), email.PayloadJson);
        Detail(html, T("Source"), email.EventType);
        return html.Append("</table>").ToString();
    }

    private string NoLinkNote() =>
        "<p style=\"font-size:13px;color:" + Muted + "\">" + Encode(T("NoLink")) + "</p>";

    /// <summary>Recette R1-77: the result of a run another pipeline started also links the run at the root of
    /// its lineage (the one whose own result the subscriber receives by default).</summary>
    private string RootRunNote(JsonElement? root, string? frontOrigin)
    {
        if (root is not { ValueKind: JsonValueKind.Object } obj
            || !obj.TryGetProperty("IsChildRun", out var isChild) || isChild.ValueKind != JsonValueKind.True
            || Id(root, "RootRunId") is not { } rootRunId
            || Url(frontOrigin, $"/pipelines/runs/{rootRunId}") is not { } url)
            return string.Empty;
        var label = Value(root, "RootPipelineName") is { } name ? $"{T("RootRunLink")} ({name})" : T("RootRunLink");
        return "<p style=\"font-size:14px\"><a href=\"" + Encode(url) + "\">" + Encode(label) + "</a></p>";
    }

    private static readonly (string Background, string Ink) NeutralStatusColours = ("#eef0f3", "#404b5a");

    /// <summary>Badge colours of a status value, matched exactly (ordinal); any other value is neutral.</summary>
    private static readonly Dictionary<string, (string Background, string Ink)> StatusColours = new(StringComparer.Ordinal)
    {
        ["Failed"] = ("#fde8e6", "#a3231a"),
        ["Error"] = ("#fde8e6", "#a3231a"),
        ["Blocked"] = ("#fde8e6", "#a3231a"),
        ["Critical"] = ("#fde8e6", "#a3231a"),
        ["Down"] = ("#fde8e6", "#a3231a"),
        ["Offline"] = ("#fde8e6", "#a3231a"),
        ["Passed"] = ("#e3f4e8", "#17643a"),
        ["Success"] = ("#e3f4e8", "#17643a"),
        ["Succeeded"] = ("#e3f4e8", "#17643a"),
        ["Online"] = ("#e3f4e8", "#17643a"),
        ["Healthy"] = ("#e3f4e8", "#17643a"),
        ["Warning"] = ("#fff3d6", "#7a4b00"),
        ["High"] = ("#fff3d6", "#7a4b00"),
        ["Degraded"] = ("#fff3d6", "#7a4b00")
    };

    private static void Detail(StringBuilder html, string label, string value, bool isStatus = false, string? statusName = null)
    {
        html.Append("<tr><td style=\"width:38%;padding:10px;font-size:13px;color:").Append(Muted).Append("\">").Append(Encode(label))
            .Append("</td><td style=\"padding:10px;font-size:14px;color:").Append(Ink).Append("\">");
        var encoded = Encode(value).Replace("\n", "<br>", StringComparison.Ordinal);
        if (isStatus)
        {
            // The colour follows the status itself, not its translated words.
            var (background, ink) = StatusColours.GetValueOrDefault(statusName ?? value, NeutralStatusColours);
            html.Append("<span style=\"padding:2px 8px;font-weight:600;background-color:").Append(background)
                .Append(";color:").Append(ink).Append("\">").Append(encoded).Append("</span>");
        }
        else
            html.Append(encoded);
        html.Append("</td></tr>");
    }

    private static (string Path, string LinkKey) Destination(string eventType, JsonElement? root)
    {
        var project = Id(root, "ProjectId");
        return RunDestination(eventType, root, project) ?? PlaceDestination(eventType, root, project)
            ?? (project is { } projectId ? ($"/projects/{projectId}/overview", "ProjectLink") : ("/notifications", "Inbox"));
    }

    /// <summary>Where a run-shaped event points: its pipeline run, its release, or its AI report.</summary>
    private static (string Path, string LinkKey)? RunDestination(string eventType, JsonElement? root, int? project)
    {
        if (eventType == "ai-task.completed" && project is { } aiProject && Id(root, "AiTaskDefinitionId") is { } aiTask)
            return ($"/projects/{aiProject}/ai-tasks/{aiTask}?result={Id(root, "AiRunResultId")}", "AiTaskLink");
        if (eventType == "release.deployed" && Id(root, "ReleaseId") is { } release)
            return ($"/releases/{release}", "ReleaseLink");
        if (!eventType.StartsWith("pipeline.", StringComparison.Ordinal) && eventType != "release.deployed") return null;
        if (Id(root, "PipelineRunId") is { } run) return ($"/pipelines/runs/{run}", "RunLink");
        return Id(root, "PipelineId") is { } pipeline ? ($"/pipelines/{pipeline}", "RunLink") : null;
    }

    /// <summary>Where a place-shaped event points: the quality page, the vaults, or its server.</summary>
    private static (string Path, string LinkKey)? PlaceDestination(string eventType, JsonElement? root, int? project)
    {
        if (eventType.StartsWith("analysis.", StringComparison.Ordinal))
            return project is { } analysisProject ? ($"/projects/{analysisProject}/quality", "AnalysisLink") : ("/analysis", "AnalysisLink");
        if (eventType == "SecretExpiring") return ("/vaults", "VaultLink");
        var serverEvent = eventType.StartsWith("agent-update.", StringComparison.Ordinal)
            || eventType is "alert.triggered" or "server.offline" or "certbot.renewal-check.failed";
        if (!serverEvent || Id(root, "ServerId") is not { } serverId) return null;
        return ($"/servers/{serverId}/" + (eventType == "certbot.renewal-check.failed" ? "certbot" : "overview"), "ServerLink");
    }

    private static int? Id(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id) && id > 0 ? id : null;

    private static string? Value(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private string Display(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => T("True"),
        JsonValueKind.False => T("False"),
        JsonValueKind.Array => string.Join(", ", value.EnumerateArray().Select(Display)),
        _ => value.GetRawText()
    };

    private string DisplayField(string eventType, JsonProperty property) =>
        EnumName(eventType, property) is { } name ? T("Status" + name) : Display(property.Value);

    /// <summary>The untranslated status a badge is coloured by: the enum name, else the payload's own text.</summary>
    private static string? StatusName(string eventType, JsonProperty property) =>
        EnumName(eventType, property) ?? (property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString() : null);

    private static string? EnumName(string eventType, JsonProperty property)
    {
        if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value)) return null;
        return property.Name switch
        {
            "GateStatus" when eventType.StartsWith("analysis.", StringComparison.Ordinal) => Enum.GetName(typeof(AnalysisGateStatus), value),
            "Status" when eventType == "analysis.report.completed" => Enum.GetName(typeof(AnalysisReportStatus), value),
            _ => null
        };
    }

    private static JsonDocument? Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonDocument.Parse(json); }
        catch (JsonException) { return null; } // Preserve malformed data as encoded text rather than fail delivery.
    }

    private static string OneLine(string value) => value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
    private string T(string key) => NotificationEmailText.Get(key, language);

    // French sets a space before the colon.
    private string Sep => language == MailTemplateLanguages.French ? " : " : ": ";
}
