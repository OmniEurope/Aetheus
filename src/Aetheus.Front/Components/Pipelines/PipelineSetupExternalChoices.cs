// SPDX-License-Identifier: EUPL-1.2
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;

namespace Aetheus.Front.Components.Pipelines;

/// <summary>
/// Recette R-12 (user decision 2026-11-09): what the capabilities step offers an external repository. Security and quality are
/// each two exclusive choices, the minimal one or the complete one, never both, and each may also be left out.
/// <list type="bullet">
/// <item>Minimal security is <c>application-security-external</c> (secrets, code, dependencies, infrastructure files); complete
/// security is <c>application-security</c> written without the stage that restores a CI manifest (no CI of ours builds an external
/// repository), so it adds the JavaScript dependency scan and the proof that the source analysed is the requested revision.</item>
/// <item>Complete quality is the quality template without the CI evidence to restore (R-34), which runs the repository's tests with
/// coverage when the platform can run them; minimal quality declares no command to run, so only complexity and lint of the languages
/// found are published.</item>
/// </list>
/// Neither writes anything into the external repository (R-23): every file they read is the repository's own and optional.
/// The option names are those of the wizard's address; <see cref="Quality"/> keeps the name the quality of an external repository
/// already had, so an address written before the minimal one existed reopens as it was.
/// </summary>
internal sealed class PipelineSetupExternalChoices
{
    public const string SecurityOption = PipelineSetupExternalOptions.Security;
    public const string SecurityCompleteOption = PipelineSetupExternalOptions.SecurityComplete;
    public const string QualityOption = PipelineSetupExternalOptions.Quality;
    public const string QualityMinimalOption = PipelineSetupExternalOptions.QualityMinimal;

    /// <summary>Minimal security, offered and ticked by default.</summary>
    public bool Security { get; set; } = true;

    /// <summary>Complete security, exclusive with <see cref="Security"/>.</summary>
    public bool SecurityComplete { get; set; }

    /// <summary>Complete quality, ticked by default (it is the quality an external repository always had).</summary>
    public bool Quality { get; set; } = true;

    /// <summary>Minimal quality, exclusive with <see cref="Quality"/>.</summary>
    public bool QualityMinimal { get; set; }

    /// <summary>The quality of any of the two kinds, by the name of its option.</summary>
    public static bool IsQuality(string capability) => capability is QualityOption or QualityMinimalOption;

    public void PickSecurity(bool value)
    {
        Security = value;
        if (value) SecurityComplete = false;
    }

    public void PickSecurityComplete(bool value)
    {
        SecurityComplete = value;
        if (value) Security = false;
    }

    public void PickQuality(bool value)
    {
        Quality = value;
        if (value) QualityMinimal = false;
    }

    public void PickQualityMinimal(bool value)
    {
        QualityMinimal = value;
        if (value) Quality = false;
    }

    /// <summary>What a type starts with: the minimal security and the complete quality, as before the choices were two.</summary>
    public void Reset()
    {
        Security = Quality = true;
        SecurityComplete = QualityMinimal = false;
    }

    /// <summary>An address that names both of a pair (written by hand) keeps the complete security and the complete quality.</summary>
    public void Normalize()
    {
        if (SecurityComplete) Security = false;
        if (Quality) QualityMinimal = false;
    }

    /// <summary>The pipelines the chosen options create, as (suffix, template). <paramref name="chosen"/> says whether an option counts: it
    /// is chosen and the type proposes it (and, for the quality, the repository holds a language it analyses).</summary>
    public IEnumerable<(string Suffix, string TemplateName)> Roles(Func<string, bool, bool> chosen)
    {
        if (chosen(SecurityOption, Security)) yield return ("security", "application-security-external");
        if (chosen(SecurityCompleteOption, SecurityComplete)) yield return ("security", "application-security");
        if (chosen(QualityOption, Quality) || chosen(QualityMinimalOption, QualityMinimal)) yield return ("quality", "application-quality");
    }

    public (string Name, Func<bool> Get, Action<bool> Set)[] Choices =>
    [
        (SecurityOption, () => Security, value => Security = value),
        (SecurityCompleteOption, () => SecurityComplete, value => SecurityComplete = value),
        (QualityOption, () => Quality, value => Quality = value),
        (QualityMinimalOption, () => QualityMinimal, value => QualityMinimal = value)
    ];

    public void AddAttributes(RenderTreeBuilder builder, object receiver, Action refresh, bool qualityOffered)
    {
        Add(builder, 250, nameof(PipelineSetupWizardStep.ExternalSecurity), Security,
            nameof(PipelineSetupWizardStep.ExternalSecurityChanged), PickSecurity);
        Add(builder, 252, nameof(PipelineSetupWizardStep.ExternalSecurityComplete), SecurityComplete,
            nameof(PipelineSetupWizardStep.ExternalSecurityCompleteChanged), PickSecurityComplete);
        Add(builder, 254, nameof(PipelineSetupWizardStep.ExternalQuality), Quality,
            nameof(PipelineSetupWizardStep.ExternalQualityChanged), PickQuality);
        Add(builder, 256, nameof(PipelineSetupWizardStep.ExternalQualityMinimal), QualityMinimal,
            nameof(PipelineSetupWizardStep.ExternalQualityMinimalChanged), PickQualityMinimal);
        builder.AddAttribute(258, nameof(PipelineSetupWizardStep.ExternalQualityOffered), qualityOffered);

        void Add(RenderTreeBuilder target, int sequence, string valueName, bool value, string callbackName, Action<bool> setter)
        {
            target.AddAttribute(sequence, valueName, value);
            target.AddAttribute(sequence + 1, callbackName, EventCallback.Factory.Create<bool>(receiver, next =>
            {
                setter(next);
                refresh();
            }));
        }
    }
}
