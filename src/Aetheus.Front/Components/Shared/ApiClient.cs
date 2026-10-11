// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Shared;

/// <summary>
/// The single injected entry point to the backend API, split per domain: <c>Api.Pipelines.XAsync()</c>.
/// The sub-clients are constructed here rather than injected so that one <c>AddHttpClient&lt;ApiClient&gt;</c>
/// registration keeps applying its whole handler chain (auth, error notification, no-store) to every
/// call, and so a page needs one injection instead of sixteen.
/// </summary>
public class ApiClient(HttpClient http)
{
    public AiApi Ai { get; } = new(http);
    public AnalysisApi Analysis { get; } = new(http);
    public AuthApi Auth { get; } = new(http);
    public GitApi Git { get; } = new(http);
    public MailApi Mail { get; } = new(http);
    public MailboxApi Mailbox { get; } = new(http);
    public MonitoringApi Monitoring { get; } = new(http);
    public Aetheus.Front.Components.Notifications.UserNotificationsApi Notifications { get; } = new(http);
    public PackagesApi Packages { get; } = new(http);
    public PipelinesApi Pipelines { get; } = new(http);
    public PipelineTemplatesApi PipelineTemplates { get; } = new(http);
    public ProjectsApi Projects { get; } = new(http);
    public SecurityApi Security { get; } = new(http);
    public ServersApi Servers { get; } = new(http);
    public ServerToolsApi ServerTools { get; } = new(http);
    public SettingsApi Settings { get; } = new(http);
    public TeamspeakApi Teamspeak { get; } = new(http);
    public VariablesApi Variables { get; } = new(http);
}
