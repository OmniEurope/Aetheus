// SPDX-License-Identifier: EUPL-1.2

namespace Aetheus.Front.Components.Pipelines;

/// <summary>A pipeline the wizard creates: the suffix of its name and the template it extends.</summary>
internal sealed record PipelineRole(string Suffix, string TemplateName);

/// <summary>What the options of the Capabilities step hold, before the type of the application filters them (see the
/// <c>on</c> parameter of <see cref="PipelineSetupRolePlan.For"/>).</summary>
internal readonly record struct PipelineSetupRoleChoices(
    bool Ci, bool Quality, bool Security, bool Qa, bool QaExtended, bool SecurityHistory,
    bool Candidate, bool Nightly, bool Production, bool FastDeploy, bool Packages);

/// <summary>The pipelines the chosen options create, in the order the summary lists them.</summary>
internal static class PipelineSetupRolePlan
{
    /// <param name="choices">The options as ticked.</param>
    /// <param name="on">Whether an option counts: it is chosen AND the type proposes it (the wizard's own rule).</param>
    /// <param name="sameProject">The Full-stack tile (true) or the front-end and the back-end apart (false), which picks the
    /// production template (R-11).</param>
    /// <param name="stack">The stack, which names the analytics package template.</param>
    /// <param name="external">The options of an external repository, which add their own pipelines.</param>
    public static IReadOnlyList<PipelineRole> For(
        PipelineSetupRoleChoices choices, Func<string, bool, bool> on, bool? sameProject, PipelineSetupStack stack,
        PipelineSetupExternalChoices external)
    {
        var roles = new List<PipelineRole>();
        var requiresCi = on("ci", choices.Ci) || on("quality", choices.Quality) || on("security", choices.Security)
            || on("qa", choices.Qa) || on("qa-extended", choices.QaExtended) || on("security-history", choices.SecurityHistory)
            || on("candidate", choices.Candidate) || on("nightly", choices.Nightly);
        Add(requiresCi, "ci", "application-ci");
        Add(on("quality", choices.Quality), "quality", "application-quality");
        Add(on("security", choices.Security), "security", "application-security");
        Add(on("qa", choices.Qa), "qa", "application-qa");
        Add(on("security-history", choices.SecurityHistory), "security-history", "application-security-history");
        Add(on("qa-extended", choices.QaExtended), "qa-extended", "application-qa-extended");
        Add(on("candidate", choices.Candidate) || on("nightly", choices.Nightly), "candidate", "application-candidate");
        Add(on("nightly", choices.Nightly), "nightly", "application-nightly");
        // R-11: the template is the shape the user chose (the Full-stack tile, a monolith, or a front and an API).
        Add(on("production", choices.Production), PipelineSetupRoles.DeployProd, PipelineSetupRoles.DeployProdTemplateFor(sameProject));
        Add(on("fast-deploy", choices.FastDeploy), "release-fast", PipelineSetupRoles.ReleaseFastTemplate);
        if (on("packages", choices.Packages))
        {
            Add(true, "package-telemetry", "package-telemetry");
            Add(true, stack.AnalyticsPackageTemplate, stack.AnalyticsPackageTemplate);
            Add(true, "package-web-analytics-browser", "package-web-analytics-browser");
            Add(true, "publish-observability-packages", "publish-observability-packages");
        }
        foreach (var (suffix, templateName) in external.Roles(on)) Add(true, suffix, templateName);
        return roles;

        void Add(bool enabled, string suffix, string templateName)
        {
            if (enabled) roles.Add(new PipelineRole(suffix, templateName));
        }
    }
}
