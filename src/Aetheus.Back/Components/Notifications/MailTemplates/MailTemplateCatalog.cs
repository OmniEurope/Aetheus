// SPDX-License-Identifier: EUPL-1.2
using static Aetheus.Back.Components.Notifications.MailTemplates.DailyRecapMailVariables;
using static Aetheus.Back.Components.Notifications.MailTemplates.NotificationMailVariables;

namespace Aetheus.Back.Components.Notifications.MailTemplates;

/// <summary>
/// What each kind of mail offers its template: the variables and what each holds (a text, an address an
/// action button may point to, or a block of HTML Aetheus writes), with a sample value per language for
/// the preview and the test mail.
/// </summary>
internal static class MailTemplateCatalog
{
    private const string SampleOrigin = "https://aetheus.example.org";

    public static IReadOnlyList<MailTemplateKind> Kinds { get; } = Enum.GetValues<MailTemplateKind>();

    /// <summary>The variables of a kind, in the order the editor offers them.</summary>
    public static IReadOnlyList<MailTemplateVariable> Variables(MailTemplateKind kind) => kind switch
    {
        MailTemplateKind.NotificationChannelTest => [Text(ChannelName), Link(InboxUrl)],
        MailTemplateKind.NotificationEvent =>
        [
            Text(Event), Text(Summary), Text(Intro), Text(Context), Text(Project), Text(LinkLabel), Link(NotificationMailVariables.Link),
            Link(InboxUrl), Block(StatusBanner), Block(Details), Block(LinkNote)
        ],
        MailTemplateKind.DailyRecap => [Text(Date), Text(Period), Link(SettingsUrl), Block(Tiles), Block(Sections)],
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mail template kind.")
    };

    /// <summary>The variable names of a kind, in the order the editor offers them.</summary>
    public static IReadOnlyList<string> VariableNames(MailTemplateKind kind) => [.. Variables(kind).Select(variable => variable.Name)];

    /// <summary>The sample value of every variable of a kind, in a language: the texts and links, and the blocks apart.</summary>
    public static MailTemplateData Samples(MailTemplateKind kind, string language)
    {
        var french = MailTemplateLanguages.Normalize(language) == MailTemplateLanguages.French;
        return kind switch
        {
            MailTemplateKind.NotificationChannelTest => MailTemplateData.Text(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                [ChannelName] = french ? "Équipe exploitation" : "Operations team",
                [InboxUrl] = SampleOrigin + "/notifications"
            }),
            // The real composer writes the sample of a failed run: the preview shows the blocks as they are sent.
            MailTemplateKind.NotificationEvent => NotificationEmailComposer.Build(new NotificationEmail("reader@example.org",
                "pipeline.failed in project Shop", "pipeline.failed",
                """{"PipelineRunId":4821,"PipelineId":41,"PipelineName":"release-candidate","BuildNumber":118,"ProjectId":3,"ProjectName":"Shop","Status":"Failed"}"""),
                SampleOrigin, language),
            MailTemplateKind.DailyRecap => DailyRecapSamples.For(french, SampleOrigin),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mail template kind.")
        };
    }

    private static MailTemplateVariable Text(string name) => new(name, MailTemplateVariableKind.Text);
    private static MailTemplateVariable Link(string name) => new(name, MailTemplateVariableKind.Link);
    private static MailTemplateVariable Block(string name) => new(name, MailTemplateVariableKind.Block);
}
