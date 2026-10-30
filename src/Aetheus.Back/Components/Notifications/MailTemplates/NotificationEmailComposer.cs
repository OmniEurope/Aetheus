// SPDX-License-Identifier: EUPL-1.2
using System.Globalization;
using System.Text;
using System.Text.Json;
using Aetheus.Back.Services.Email;
using static Aetheus.Back.Components.Notifications.MailTemplates.NotificationMailVariables;
using static Aetheus.Back.Services.Email.EmailHtmlLayout;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>Presentation only: dispatch, subscriptions and delivery outcomes remain in the existing services.</summary>
internal sealed class NotificationEmailComposer(string language, MailBlockColours colours, TimeZoneInfo timeZone, DateTime sentAtUtc)
{
    /// <summary>The wall clock a mail states its times in until a recipient or platform zone exists: the one the daily recap defaults to (recette R-55).</summary>
    private const string DefaultTimeZoneId = "Europe/Paris";

    /// <summary>A metric that has a dedicated page on the server gets its alert there (recette R-52); any other lands on the overview.</summary>
    private static readonly Dictionary<string, string> AlertPages = new(StringComparer.Ordinal)
    {
        ["DockerContainerRisk"] = "docker",
        ["RkhunterWarnings"] = "rkhunter",
        ["BlockedIps"] = "portsentry"
    };

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
        ["certbot.certificate-expiring"] = "CertificateExpiring",
        ["SecretExpiring"] = "SecretExpiring",
        ["server.offline"] = "ServerOffline",
        ["notification.test"] = "ChannelTest"
    };

    /// <summary>
    /// The status an event stands for when its payload names none the banner knows: its colour is the
    /// colour of that status in <see cref="StatusTones"/>. An event absent here is neutral.
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
        ["certbot.certificate-expiring"] = "Warning",
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
    public static MailTemplateData Build(NotificationEmail email, string? frontOrigin,
        string language = MailTemplateLanguages.English, MailBlockColours? colours = null, TimeZoneInfo? timeZone = null, TimeProvider? clock = null) =>
        new NotificationEmailComposer(MailTemplateLanguages.Normalize(language), colours ?? MailBlockColours.Light,
            timeZone ?? (TimeZoneInfo.TryFindSystemTimeZoneById(DefaultTimeZoneId, out var paris) ? paris : TimeZoneInfo.Utc),
            (clock ?? TimeProvider.System).GetUtcNow().UtcDateTime).Compose(email, frontOrigin);

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
            [InboxUrl] = Url(frontOrigin, "/notifications") ?? string.Empty,
            [SentAt] = Local(sentAtUtc)
        };
        var serverUrl = Id(root, "ServerId") is { } serverId ? Url(frontOrigin, $"/servers/{serverId}/overview") : null;
        var blocks = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [StatusBanner] = Banner(heading, BannerColours(email.EventType, root)),
            [ApprovalActions] = ApprovalMailActions.Block(email.EventType, Id(root, "PipelineRunId"), Id(root, "ApprovalId"), frontOrigin, colours, T),
            [ContextLink] = ContextBlock(context, serverUrl, root),
            [Details] = DetailTable(email, root, projectPrefix, serverUrl),
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
    private string Banner(string heading, (string Background, string Ink) pair) =>
        new StringBuilder("<div style=\"padding:20px 24px;background-color:").Append(pair.Background)
            .Append(";color:").Append(pair.Ink).Append("\">")
            .Append("<span style=\"font-size:12px;font-weight:700;color:").Append(pair.Ink).Append("\">AETHEUS · ")
            .Append(Encode(T("Label"))).Append("</span><br>")
            .Append("<strong style=\"font-size:22px;color:").Append(pair.Ink).Append("\">").Append(Encode(heading)).Append("</strong>")
            .Append("</div>").ToString();

    /// <summary>The colours of the status the payload reports, else of the status the event stands for, else neutral.</summary>
    private (string Background, string Ink) BannerColours(string eventType, JsonElement? root)
    {
        if (root is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (property.Name is "Status" or "GateStatus" or "Severity"
                    && StatusName(eventType, property) is { } status && StatusTones.TryGetValue(status, out var reported))
                    return colours.Pair(reported);
            }
        }
        return colours.Pair(EventStatuses.TryGetValue(eventType, out var standsFor) ? StatusTones[standsFor] : MailTone.Neutral);
    }

    /// <summary>The subject on its own line; a server's name opens that server (recette R-54).</summary>
    private static string ContextBlock(string? context, string? serverUrl, JsonElement? root)
    {
        if (string.IsNullOrWhiteSpace(context)) return string.Empty;
        var name = "<strong>" + Encode(context) + "</strong>";
        var isServer = serverUrl is not null && string.Equals(Value(root, "ServerName"), context, StringComparison.Ordinal);
        return "<p>" + (isServer ? "<a href=\"" + Encode(serverUrl) + "\">" + name + "</a>" : name) + "</p>";
    }

    /// <summary>An instant as the recipient's wall clock, with the zone named so the hour is not ambiguous.</summary>
    private string Local(DateTime utc) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), timeZone)
            .ToString("G", CultureInfo.GetCultureInfo(language)) + " (" + timeZone.Id + ")";

    private string DetailTable(NotificationEmail email, JsonElement? root, string projectPrefix, string? serverUrl)
    {
        var html = new StringBuilder("<table>");
        if (Value(root, "ProjectName") is null && ProjectOfSubject(email, projectPrefix) is { } project)
            Detail(html, T("ProjectName"), project);
        if (root is { ValueKind: JsonValueKind.Object } obj)
        {
            foreach (var property in obj.EnumerateObject())
            {
                if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) continue;
                if (IsHiddenField(email.EventType, property.Name, serverUrl)) continue;
                if (WordedRows(html, email.EventType, root, property)) continue;
                Detail(html, NotificationEmailText.Label(property.Name, language), DisplayField(email.EventType, property),
                    property.Name is "Status" or "GateStatus" or "Severity", StatusName(email.EventType, property),
                    property.Name == "ServerName" ? serverUrl : null);
            }
        }
        else if (!string.IsNullOrWhiteSpace(email.PayloadJson))
            Detail(html, T("Payload"), email.PayloadJson);
        return html.Append("</table>").ToString();
    }

    /// <summary>The fields the mail words itself instead of showing the producer's value: an alert's diagnostic,
    /// an agent update's failure, a trigger instant. True when the field was handled here.</summary>
    private bool WordedRows(StringBuilder html, string eventType, JsonElement? root, JsonProperty property)
    {
        if (eventType == "alert.triggered" && property.Name == "Message")
        {
            // The producers' sentence is English: the mail words the diagnostic itself, or shows none.
            if (AlertDiagnosticText.Build(root, language) is { } diagnostic)
                Detail(html, NotificationEmailText.Label("Message", language), diagnostic);
            return true;
        }
        if (eventType.StartsWith("agent-update.", StringComparison.Ordinal) && property.Name is "FailureCode" or "FailureDiagnostic")
        {
            AgentFailureRows(html, root, property.Name);
            return true;
        }
        if (property.Name == "TriggeredAt" && TriggeredInstant(property.Value) is { } triggered)
        {
            Detail(html, T("TriggeredAtLocal"), Local(triggered));
            Detail(html, T("TriggeredAt"), triggered.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    private static readonly Dictionary<string, string> AgentFailureCauses = new(StringComparer.Ordinal)
    {
        ["wrong-version"] = "AgentFailureWrongVersion",
        ["unsupported-protocol"] = "AgentFailureUnsupportedProtocol",
        ["missing-capabilities"] = "AgentFailureMissingCapabilities",
        ["confirmation-timeout"] = "AgentFailureConfirmationTimeout",
        ["update-task-timeout"] = "AgentFailureTaskTimeout"
    };

    /// <summary>A failed agent update is worded from its failure code in the recipient's language; the producer's English
    /// diagnostic stays in the logs. What the host itself reported (no code of ours) is shown as the host's raw output,
    /// never as a sentence of the mail.</summary>
    private void AgentFailureRows(StringBuilder html, JsonElement? root, string property)
    {
        var code = Value(root, "FailureCode");
        var known = code is not null && AgentFailureCauses.ContainsKey(code);
        if (property == "FailureCode")
        {
            if (known) Detail(html, T("FailureCause"), T(AgentFailureCauses[code!]));
            else if (code is not null && code != "agent-update-failed") Detail(html, T("FailureCode"), code);
            return;
        }
        if (!known && Value(root, "FailureDiagnostic") is { Length: > 0 } output)
            Detail(html, T("FailureHostOutput"), output, monospace: true);
    }

    private string NoLinkNote() =>
        "<p style=\"font-size:13px;color:" + colours.Muted + "\">" + Encode(T("NoLink")) + "</p>";

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

    /// <summary>The tone of a status value, matched exactly (ordinal); any other value is neutral. The colours of a
    /// tone are the recipient's (<see cref="MailBlockColours"/>, recette R1-8).</summary>
    private static readonly Dictionary<string, MailTone> StatusTones = new(StringComparer.Ordinal)
    {
        ["Failed"] = MailTone.Danger,
        ["Error"] = MailTone.Danger,
        ["Blocked"] = MailTone.Danger,
        ["Critical"] = MailTone.Danger,
        ["Down"] = MailTone.Danger,
        ["Offline"] = MailTone.Danger,
        ["Passed"] = MailTone.Success,
        ["Success"] = MailTone.Success,
        ["Succeeded"] = MailTone.Success,
        ["Online"] = MailTone.Success,
        ["Healthy"] = MailTone.Success,
        ["Warning"] = MailTone.Warning,
        ["High"] = MailTone.Warning,
        ["Degraded"] = MailTone.Warning
    };

    private static DateTime? TriggeredInstant(JsonElement value) =>
        value.ValueKind == JsonValueKind.String
        && DateTime.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var instant)
            ? instant : null;

    private void Detail(StringBuilder html, string label, string value, bool isStatus = false, string? statusName = null, string? href = null, bool monospace = false)
    {
        html.Append("<tr><td style=\"width:38%;padding:10px;font-size:13px;color:").Append(colours.Muted).Append("\">").Append(Encode(label))
            .Append("</td><td style=\"padding:10px;font-size:14px;color:").Append(colours.Ink).Append("\">");
        var encoded = Encode(value).Replace("\n", "<br>", StringComparison.Ordinal);
        if (isStatus)
        {
            // The colour follows the status itself, not its translated words.
            var (background, ink) = colours.Pair(StatusTones.GetValueOrDefault(statusName ?? value, MailTone.Neutral));
            html.Append("<span style=\"padding:2px 8px;font-weight:600;background-color:").Append(background)
                .Append(";color:").Append(ink).Append("\">").Append(encoded).Append("</span>");
        }
        else if (monospace)
            html.Append("<pre style=\"margin:0;font-family:Consolas,'Courier New',monospace;font-size:13px;white-space:pre-wrap;word-break:break-word\">")
                .Append(Encode(value)).Append("</pre>");
        else if (href is not null)
            html.Append("<a href=\"").Append(Encode(href)).Append("\">").Append(encoded).Append("</a>");
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
        // An alert opens on its own page; the pages of its server below only serve an alert raised before
        // alerts were stored (recette R-52).
        if (eventType == "alert.triggered" && LongId(root, "AlertId") is { } alertId) return ($"/alerts/{alertId}", "AlertLink");
        var serverEvent = eventType.StartsWith("agent-update.", StringComparison.Ordinal)
            || eventType is "alert.triggered" or "server.offline" or "certbot.renewal-check.failed" or "certbot.certificate-expiring";
        if (!serverEvent || Id(root, "ServerId") is not { } serverId) return null;
        // The button leads to the alert itself, not only to its server (recette R-52).
        if (eventType == "alert.triggered")
            return ($"/servers/{serverId}/" + AlertPages.GetValueOrDefault(Value(root, "Metric") ?? string.Empty, "overview"), "AlertLink");
        return ($"/servers/{serverId}/" + (eventType is "certbot.renewal-check.failed" or "certbot.certificate-expiring" ? "certbot" : "overview"), "ServerLink");
    }

    private static int? Id(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var id) && id > 0 ? id : null;

    private static long? LongId(JsonElement? root, string name) =>
        root is { ValueKind: JsonValueKind.Object } obj && obj.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var id) && id > 0 ? id : null;

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
        EnumName(eventType, property) is { } name ? T("Status" + name)
        : eventType == "alert.triggered" && AlertValue(property) is { } alert ? alert
        : Display(property.Value);

    /// <summary>The rule's name tells the reader nothing the event does not (recette R-53); the server's identifier is
    /// no reader's business when the server opens from its name, in every server event (R-58 follow-up); the structured
    /// SSH changes are worded under the diagnostic, never shown as data. An agent update's request and project
    /// identifiers mean nothing to a reader: the server and the project are named elsewhere in the mail. An approval's identifier only feeds the buttons at the top (recette R-24).</summary>
    private static bool IsHiddenField(string eventType, string name, string? serverUrl) =>
        (name == "ServerId" && serverUrl is not null)
        || (eventType == "alert.triggered" && name is "RuleName" or "Changes" or "AlertId")
        || (eventType.StartsWith("agent-update.", StringComparison.Ordinal) && name is "AgentUpdateRequestId" or "ProjectId")
        || (eventType == "pipeline.approval-requested" && name == "ApprovalId");

    /// <summary>The words of an alert's metric, comparison, severity and host risks; a value without a word stays as the producer wrote it.</summary>
    private string? AlertValue(JsonProperty property)
    {
        if (property.Name == "Risks")
        {
            // The producer joins the risks into one text; a list of risks reads the same.
            var risks = property.Value.ValueKind == JsonValueKind.Array
                ? property.Value.EnumerateArray().Select(item => item.GetString() ?? string.Empty)
                : property.Value.ValueKind == JsonValueKind.String
                    ? (property.Value.GetString() ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries)
                    : null;
            return risks is null ? null : string.Join(", ", risks.Select(Risk));
        }
        if (property.Value.ValueKind != JsonValueKind.String || property.Value.GetString() is not { } text) return null;
        return property.Name switch
        {
            "Metric" => AlertWord("AlertMetric", text),
            "Operator" => AlertWord("AlertOperator", text),
            "Severity" => AlertWord("AlertSeverity", text),
            _ => null
        };
    }

    private string AlertWord(string prefix, string value) => NotificationEmailText.Find(prefix + value, language) ?? value;

    private static readonly Dictionary<string, string> RiskKeys = new(StringComparer.Ordinal)
    {
        ["privileged"] = "AlertRiskPrivileged",
        ["Docker socket mounted"] = "AlertRiskDockerSocket",
        ["host PID namespace"] = "AlertRiskHostPid",
        ["host network"] = "AlertRiskHostNetwork"
    };

    private const string CapabilitiesPrefix = "capabilities ";

    private string Risk(string token) =>
        RiskKeys.TryGetValue(token, out var key) ? T(key)
        : token.StartsWith(CapabilitiesPrefix, StringComparison.Ordinal)
            ? string.Format(CultureInfo.GetCultureInfo(language), T("AlertRiskCapabilities"), token[CapabilitiesPrefix.Length..])
            : token;

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
