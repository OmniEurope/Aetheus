// SPDX-License-Identifier: EUPL-1.2
using System.ComponentModel.DataAnnotations;

namespace Aetheus.Shared.Components.MailTemplates;

/// <summary>The languages a mail template is written in, by their two-letter code.</summary>
public static class MailTemplateLanguages
{
    public const string English = "en";
    public const string French = "fr";

    public static IReadOnlyList<string> All { get; } = [English, French];

    /// <summary>The template language of a culture or preference value (<c>fr-FR</c>, <c>fr</c>, <c>en</c>); English for anything else.</summary>
    public static string Normalize(string? value) =>
        value is not null && value.Trim().StartsWith(French, StringComparison.OrdinalIgnoreCase) ? French : English;

    public static bool IsSupported(string? value) =>
        value is not null && All.Contains(value, StringComparer.Ordinal);
}

/// <summary>One template of the list: a kind in a language, and whether an administrator changed it.</summary>
public sealed record MailTemplateSummaryDto
{
    public MailTemplateKind Kind { get; init; }
    public string Language { get; init; } = MailTemplateLanguages.English;
    public bool IsCustomized { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    /// <summary>The organization whose own version this line is; null for the platform's.</summary>
    public int? OrganizationId { get; init; }
}

/// <summary>The kinds an organization may have its own version of: the mails about its resources. The
/// others (channel test, daily recap, account mails) concern no organization and stay the platform's.</summary>
public static class MailTemplateScopes
{
    public static IReadOnlyList<MailTemplateKind> OrganizationKinds { get; } = [MailTemplateKind.NotificationEvent];

    public static bool IsOrganizationKind(MailTemplateKind kind) => OrganizationKinds.Contains(kind);
}

/// <summary>A variable the template of a kind may insert, written <c>{{Name}}</c>; its label is localized by the front.</summary>
public sealed record MailTemplateVariableDto
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The value the preview and the test mail use, in the template's language.</summary>
    public string SampleValue { get; init; } = string.Empty;

    /// <summary>A text, an address (the target of an action button), or a block (an HTML fragment, sampled as HTML).</summary>
    public MailTemplateVariableKind Kind { get; init; }
}

/// <summary>A template as it is sent: the administrator's version, or the default one when nobody changed it.</summary>
public sealed record MailTemplateDto
{
    public MailTemplateKind Kind { get; init; }
    public string Language { get; init; } = MailTemplateLanguages.English;
    public string Subject { get; init; } = string.Empty;
    public string Body { get; init; } = string.Empty;
    /// <summary>For the platform version, changed by a site Admin; for an organization, the organization has
    /// its own version (else the platform's is shown, as it is sent).</summary>
    public bool IsCustomized { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string? UpdatedBy { get; init; }

    /// <summary>The organization the template was read for; null for the platform's.</summary>
    public int? OrganizationId { get; init; }
    public List<MailTemplateVariableDto> Variables { get; init; } = [];
}

/// <summary>The subject and the body of a template, to save or to send as a test.</summary>
public sealed record SaveMailTemplateRequest
{
    [Required]
    [StringLength(MailTemplateLimits.MaxSubjectLength)]
    public string Subject { get; init; } = string.Empty;

    [Required]
    [StringLength(MailTemplateLimits.MaxBodyLength)]
    public string Body { get; init; } = string.Empty;
}

public static class MailTemplateLimits
{
    public const int MaxSubjectLength = 300;
    public const int MaxBodyLength = 100_000;
}

/// <summary>What happened to a test mail: sent to <see cref="To"/>, or why not, the server's text verbatim.</summary>
public sealed record MailTemplateTestResultDto
{
    public NotificationTestStatus Status { get; init; }
    public string? To { get; init; }
    public string? Message { get; init; }
}
